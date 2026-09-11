using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace FootIk;

public sealed unsafe partial class Plugin : IDalamudPlugin
{
    // Both from CustomizePlus Core/Data/Constants.cs, copied 2026-09-10.
    // Render: bone edits made here, before Original, survive to the rendered frame.
    // Move (GameObject_UpdateVisualPosition): draw-object position edits made after Original persist for the frame.
    private const string RenderSig = "E8 ?? ?? ?? ?? 48 81 C3 ?? ?? ?? ?? BF ?? ?? ?? ?? 33 ED";
    private const string MoveSig = "E8 ?? ?? ?? ?? 84 DB 74 3A";

    private delegate nint RenderDelegate(nint a1, nint a2, nint a3, int a4);

    private delegate void MoveDelegate(nint gameObject);

    // [PluginService] is AttributeTargets.Property. A field will not compile.
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IPluginLog           Log         { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider Interop     { get; private set; } = null!;
    [PluginService] internal static ISigScanner          SigScanner  { get; private set; } = null!;
    [PluginService] internal static IObjectTable         Objects     { get; private set; } = null!;
    [PluginService] internal static ICondition           Condition   { get; private set; } = null!;
    [PluginService] internal static IClientState         ClientState { get; private set; } = null!;
    [PluginService] internal static IGameGui             GameGui     { get; private set; } = null!;
    [PluginService] internal static ICommandManager      Commands    { get; private set; } = null!;

    public Settings Settings { get; }

    public string HookStatus { get; private set; } = "not installed";
    public string MoveHookStatus { get; private set; } = "not installed";
    public string SelfTest { get; } = Solver.SelfTest();
    public bool Tripped { get; private set; }
    public int Faults { get; private set; }
    public string? LastError { get; private set; }
    public double LastMicros { get; private set; }
    public double MaxMicros { get; private set; }
    public Snapshot Snap;

    private readonly Hook<RenderDelegate>? renderHook;
    private readonly Hook<MoveDelegate>? moveHook;
    private readonly Overlay overlay;
    private readonly WindowSystem windows = new("FootIk");
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double lastTick;
    private nint localAddress;

    private LegChain chain;
    private float blend;           // 0..1
    private float smoothDrop;      // model units, negative = down
    private float written;         // the draw offset Y we hold, not the game's total
    private Vector3 pelvisForMove; // world offset added to the draw object in the movement hook
    private Vector3 moveWritten;   // the part of it the draw object currently carries
    private Vector3 lastLogical;
    private readonly Vector3[] gatherShift = new Vector3[2]; // model space
    private readonly Vector3[] wallShift = new Vector3[2];   // model space
    private readonly Vector3[] latchTarget = new Vector3[2]; // world space
    private readonly bool[] latched = new bool[2];
    private Vector3 bodyShift;     // model space
    private float lean;            // radians
    private Quaternion tilt = Quaternion.Identity; // model space

    public Plugin()
    {
        this.Settings = LoadSettings();

        try
        {
            if (!SigScanner.TryScanText(RenderSig, out var addr))
            {
                throw new InvalidOperationException("signature not found (game patched? refresh from CustomizePlus Constants.cs)");
            }

            this.renderHook = Interop.HookFromAddress<RenderDelegate>(addr, this.RenderDetour);
            this.renderHook.Enable();
            this.HookStatus = $"hooked RenderManager::Render at 0x{addr:X}";
        }
        catch (Exception ex)
        {
            this.renderHook?.Dispose();
            this.renderHook = null;
            this.HookStatus = $"failed: {ex.Message}";
            Log.Error(ex, "FootIk: render hook unavailable, plugin inert");
        }

        try
        {
            if (!SigScanner.TryScanText(MoveSig, out var addr))
            {
                throw new InvalidOperationException("signature not found");
            }

            this.moveHook = Interop.HookFromAddress<MoveDelegate>(addr, this.MoveDetour);
            this.moveHook.Enable();
            this.MoveHookStatus = $"hooked UpdateVisualPosition at 0x{addr:X}";
        }
        catch (Exception ex)
        {
            this.moveHook?.Dispose();
            this.moveHook = null;
            this.MoveHookStatus = $"failed: {ex.Message}";
            Log.Error(ex, "FootIk: movement hook unavailable, horizontal body offsets disabled");
        }

        this.overlay = new Overlay(this);
        this.windows.AddWindow(this.overlay);
        PluginInterface.UiBuilder.Draw += this.windows.Draw;
        PluginInterface.UiBuilder.Draw += this.overlay.DrawWorldDots;
        PluginInterface.UiBuilder.OpenMainUi += this.overlay.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += this.overlay.Toggle;
        Commands.AddHandler("/ik", new CommandInfo((_, _) => this.overlay.Toggle()) { HelpMessage = "Toggle the Inverse Kinematics window." });
        // A wrong solver writes wrong-but-finite poses, which no guard downstream can catch: start inert instead.
        this.Tripped = this.SelfTest != "PASS";
        Log.Information("FootIk loaded. {Status}. {Move}. Solver self-test: {Test}", this.HookStatus, this.MoveHookStatus, this.SelfTest);
    }

    public void Rearm()
    {
        this.Tripped = false;
        this.Faults = 0;
        this.LastError = null;
    }

    private static Settings LoadSettings()
    {
        Settings settings;
        try
        {
            // Null when nothing is saved yet, and when the stored type no longer resolves: Dalamud writes the assembly
            // name into the JSON, so renaming this assembly abandons the old file rather than failing loudly.
            settings = PluginInterface.GetPluginConfig() as Settings ?? new Settings();
        }
        catch (Exception ex)
        {
            // Better a loud default than a silent one: the user needs to know their settings are gone.
            Log.Error(ex, "Saved settings could not be read. Starting from defaults.");
            settings = new Settings();
        }

        settings.Repair();
        return settings;
    }

    // Called when a widget finishes an edit, not while one is being dragged: SavePluginConfig writes the file
    // synchronously on the draw thread.
    public void SaveSettings()
    {
        try
        {
            PluginInterface.SavePluginConfig(this.Settings);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Settings could not be saved.");
        }
    }

    private nint RenderDetour(nint a1, nint a2, nint a3, int a4)
    {
        this.SafeTick();
        return this.renderHook!.Original(a1, a2, a3, a4);
    }

    // Delta-only, like the draw offset: the game does not always rewrite the draw position from the logical one, and
    // while it eases the one toward the other an offset re-added on top of itself every frame compounds until the
    // character is thrown clear of the camera. What was added last frame comes off before Original sees the position.
    private void MoveDetour(nint gameObject)
    {
        var ours = gameObject == this.localAddress;
        if (ours && this.moveWritten != Vector3.Zero && ShiftDraw(gameObject, -this.moveWritten))
        {
            this.moveWritten = Vector3.Zero;
        }

        this.moveHook!.Original(gameObject);
        if (ours && this.pelvisForMove != Vector3.Zero && ShiftDraw(gameObject, this.pelvisForMove))
        {
            this.moveWritten = this.pelvisForMove;
        }
    }

    private static bool ShiftDraw(nint gameObject, Vector3 by)
    {
        var draw = ((Character*)gameObject)->DrawObject;
        if (draw == null || draw->GetObjectType() != ObjectType.CharacterBase)
        {
            return false;
        }

        draw->Object.Position.X += by.X;
        draw->Object.Position.Y += by.Y;
        draw->Object.Position.Z += by.Z;
        return true;
    }

    private void SafeTick()
    {
        if (this.Tripped)
        {
            return;
        }

        var t0 = this.clock.Elapsed.TotalMilliseconds;
        try
        {
            this.Tick();
        }
        catch (Exception ex)
        {
            // Only managed failures land here; a bad pointer would take the client down, hence the guards in ResolvePose.
            this.Faults++;
            this.LastError = ex.Message;
            if (this.Faults >= 5)
            {
                this.Tripped = true;
                Log.Error(ex, "FootIk: tripped after {Faults} faults", this.Faults);

                // Nothing may leave SafeTick: an exception here would cross back into the render detour and skip Original.
                try
                {
                    this.Release();
                }
                catch (Exception undo)
                {
                    Log.Error(undo, "FootIk: could not undo our own offsets on trip");
                }
            }
        }

        this.LastMicros = (this.clock.Elapsed.TotalMilliseconds - t0) * 1000.0;
        this.MaxMicros = Math.Max(this.MaxMicros, this.LastMicros);
    }

    // Horizontal needs the movement hook: the game only honours the Y of the draw offset.
    private void ApplyPelvis(Character* chr, Vector3 total)
    {
        if (!float.IsFinite(total.X + total.Y + total.Z))
        {
            return;
        }

        if (total.Y != this.written)
        {
            ref var off = ref chr->GameObject.DrawOffset;
            chr->GameObject.SetDrawOffset(off.X, off.Y + (total.Y - this.written), off.Z);
            this.written = total.Y;
        }

        this.pelvisForMove = this.moveHook == null ? Vector3.Zero : new Vector3(total.X, 0f, total.Z);
    }

    // Takes our own offsets back off the character. The breaker path needs it too: a tripped Tick never reaches
    // ApplyPelvis again, so without this the character stays sunk until the plugin is unloaded.
    private void Release()
    {
        this.pelvisForMove = default;
        if (this.written != 0f && Objects.LocalPlayer is { } lp)
        {
            ref var off = ref ((Character*)lp.Address)->GameObject.DrawOffset;
            ((Character*)lp.Address)->GameObject.SetDrawOffset(off.X, off.Y - this.written, off.Z);
        }

        this.written = 0f;
    }

    public void Dispose()
    {
        this.renderHook?.Dispose();
        this.Release();
        this.moveHook?.Dispose();

        Commands.RemoveHandler("/ik");
        PluginInterface.UiBuilder.Draw -= this.windows.Draw;
        PluginInterface.UiBuilder.Draw -= this.overlay.DrawWorldDots;
        this.windows.RemoveAllWindows();
        PluginInterface.UiBuilder.OpenMainUi -= this.overlay.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= this.overlay.Toggle;
    }
}
