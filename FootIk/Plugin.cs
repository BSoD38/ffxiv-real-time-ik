using System;
using System.Collections.Generic;
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
    public int Tracked { get; private set; }
    public Snapshot Snap;

    private const int MaxTracked = 50;

    private readonly Hook<RenderDelegate>? renderHook;
    private readonly Hook<MoveDelegate>? moveHook;
    private readonly Overlay overlay;
    private readonly WindowSystem windows = new("FootIk");
    private readonly Stopwatch clock = Stopwatch.StartNew();

    // Keyed by the character address, which the allocator reuses, so each state carries the spawn id it belongs to.
    // Read from both detours; they run on the same thread, so no synchronisation.
    private readonly Dictionary<nint, CharState> states = [];
    private double lastTick;

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
        this.states.TryGetValue(gameObject, out var st);
        if (st != null && st.MoveWritten != Vector3.Zero && ShiftDraw(gameObject, -st.MoveWritten))
        {
            st.MoveWritten = Vector3.Zero;
        }

        this.moveHook!.Original(gameObject);
        if (st != null && st.PelvisForMove != Vector3.Zero && ShiftDraw(gameObject, st.PelvisForMove))
        {
            st.MoveWritten = st.PelvisForMove;
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
    private void ApplyPelvis(Character* chr, CharState st, Vector3 total)
    {
        if (!float.IsFinite(total.X + total.Y + total.Z))
        {
            return;
        }

        // The game clears DrawOffset whenever it moves a character, and a stair step is exactly that, so our share goes
        // with it (2026-09-11, measured in game: the field read zero while we believed a tread was applied). A write made
        // only when our own target changes is therefore never re-asserted, and the body drop we worked out is lost for as
        // long as it stays constant - which is most of a climb. So: work out what the field holds besides us, put ours
        // back on top of that, and do it whenever the field is not already what it should be. Still only ever our own
        // share, so SimpleHeels and friends keep theirs; a cleared field simply has nobody's in it any more.
        ref var off = ref chr->GameObject.DrawOffset;
        var held = off.Y;
        st.SeenOffsetY = held;
        var others = MathF.Abs(held) < 1e-6f && st.Written != 0f ? 0f : held - st.Written;
        var wanted = others + total.Y;
        if (MathF.Abs(wanted - held) > 1e-6f)
        {
            chr->GameObject.SetDrawOffset(off.X, wanted, off.Z);
        }

        st.Written = total.Y;

        st.PelvisForMove = this.moveHook == null ? Vector3.Zero : new Vector3(total.X, 0f, total.Z);
    }

    // Takes our own offsets back off every character we hold. The breaker path needs it too: a tripped Tick never
    // reaches ApplyPelvis again, so without this the characters stay sunk until the plugin is unloaded. Walks the
    // object table rather than the states directly: an address we tracked last frame may have been freed since.
    private void Release()
    {
        for (var i = 0; i < Objects.Length; i++)
        {
            var o = Objects[i];
            if (o == null || !this.states.TryGetValue(o.Address, out var st) || st.Id != o.GameObjectId)
            {
                continue;
            }

            if (st.Written != 0f)
            {
                ref var off = ref ((Character*)o.Address)->GameObject.DrawOffset;
                ((Character*)o.Address)->GameObject.SetDrawOffset(off.X, off.Y - st.Written, off.Z);
            }

            if (st.MoveWritten != Vector3.Zero)
            {
                ShiftDraw(o.Address, -st.MoveWritten);
            }
        }

        this.states.Clear();
        this.Tracked = 0;
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
