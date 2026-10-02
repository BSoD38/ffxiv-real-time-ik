using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using KamiToolKit;

namespace FootIk;

public sealed unsafe partial class Plugin : IAsyncDalamudPlugin
{
    // Both from CustomizePlus Core/Data/Constants.cs.
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
    [PluginService] internal static IDataManager         Data        { get; private set; } = null!;
    [PluginService] internal static ICommandManager      Commands    { get; private set; } = null!;
    [PluginService] internal static IFramework           Framework   { get; private set; } = null!;

    public Settings Settings { get; }

    public string HookStatus { get; private set; } = "not installed";
    public string MoveHookStatus { get; private set; } = "not installed";
    public string SelfTest { get; } = Solver.SelfTest();
    public bool Tripped { get; private set; }
    public int Faults { get; private set; }
    public string? LastError { get; private set; }
    public double LastMicros { get; private set; }
    public string? LastBump { get; private set; }
    public int Tracked { get; private set; }
    public Snapshot Snap;

    private const int MaxTracked = 50;

    private readonly Hook<RenderDelegate>? renderHook;
    private readonly Hook<MoveDelegate>? moveHook;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private Overlay? overlay;

    // Both detours read this on the same thread, so no synchronisation.
    private readonly Dictionary<nint, CharState> states = [];
    private double lastTick;

    public Plugin()
    {
        this.Settings = LoadSettings();
        this.renderHook = Install<RenderDelegate>(RenderSig, this.RenderDetour, "RenderManager::Render", out var renderStatus);
        this.HookStatus = renderStatus;
        this.moveHook = Install<MoveDelegate>(MoveSig, this.MoveDetour, "UpdateVisualPosition", out var moveStatus);
        this.MoveHookStatus = moveStatus;

        PluginInterface.UiBuilder.Draw += this.UpdateMesh;
        Framework.Update += this.PlaySounds;

        // A wrong solver writes wrong-but-finite poses, which no guard downstream can catch: start inert instead.
        this.Tripped = this.SelfTest != "PASS";

        // Only once both fields are set: each detour dereferences its own, and the constructor runs off the main thread.
        this.moveHook?.Enable();
        this.renderHook?.Enable();
        Log.Information("FootIk loaded. {Status}. {Move}. Solver self-test: {Test}", this.HookStatus, this.MoveHookStatus, this.SelfTest);
    }

    // The window waits on KamiToolKit loading its textures, and nothing in this class may await: `unsafe` forbids it.
    public Task LoadAsync(CancellationToken cancellationToken) => Overlay.InstallAsync(this, cancellationToken);

    private static Hook<T>? Install<T>(string sig, T detour, string what, out string status) where T : Delegate
    {
        try
        {
            if (!SigScanner.TryScanText(sig, out var addr))
            {
                throw new InvalidOperationException("signature not found (game patched? refresh from CustomizePlus Constants.cs)");
            }

            var hook = Interop.HookFromAddress(addr, detour);
            status = $"hooked {what} at 0x{addr:X}";
            return hook;
        }
        catch (Exception ex)
        {
            status = $"failed: {ex.Message}";
            Log.Error(ex, "FootIk: {What} hook unavailable", what);
            return null;
        }
    }

    internal void AttachWindow(Overlay window)
    {
        this.overlay = window;
        PluginInterface.UiBuilder.Draw += window.DrawWorldDots;
        PluginInterface.UiBuilder.OpenMainUi += window.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += window.Toggle;
        Commands.AddHandler("/ik", new CommandInfo((_, _) => window.Toggle()) { HelpMessage = "Toggle the Inverse Kinematics window." });
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
            // Null if nothing is saved, or the assembly was renamed: Dalamud stores the type name.
            settings = PluginInterface.GetPluginConfig() as Settings ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saved settings could not be read. Starting from defaults.");
            settings = new Settings();
        }

        settings.Repair();
        return settings;
    }

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

    // Delta-only: last frame's shift comes off before Original, or it compounds.
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
    }

    // Horizontal needs the movement hook: the game only honours the Y of the draw offset.
    private void ApplyPelvis(Character* chr, CharState st, Vector3 total)
    {
        if (!float.IsFinite(total.X + total.Y + total.Z))
        {
            return;
        }

        // The game clears DrawOffset whenever it moves a character: re-assert our share on top of the rest, every frame.
        // Cleared, the field holds only what a hook adds.
        ref var off = ref chr->GameObject.DrawOffset;
        var held = off.Y;
        st.SeenOffsetY = held;
        var others = MathF.Abs(held - st.Hooked.Y) < 1e-6f && st.Written != 0f ? st.Hooked.Y : held - st.Written;
        var wanted = others + total.Y;
        if (MathF.Abs(wanted - held) <= 1e-6f || SetOffset(chr, st, new Vector3(off.X, wanted, off.Z)))
        {
            st.Written = total.Y;
        }

        st.PelvisForMove = this.moveHook == null ? Vector3.Zero : new Vector3(total.X, 0f, total.Z);
    }

    // SimpleHeels hooks SetDrawOffset and adds its heel to whatever it is given, so pass the heel less. False when the
    // hook dropped the write; it keeps the value as its base anyway, so the old base goes back.
    private static bool SetOffset(Character* chr, CharState st, Vector3 want)
    {
        Vector3 before = chr->GameObject.DrawOffset;
        var pass = want - st.Hooked;
        chr->GameObject.SetDrawOffset(pass.X, pass.Y, pass.Z);
        Vector3 landed = chr->GameObject.DrawOffset;
        if (landed == before)
        {
            var keep = before - st.Hooked;
            chr->GameObject.SetDrawOffset(keep.X, keep.Y, keep.Z);
            return false;
        }

        var added = landed - pass;
        if (float.IsFinite(added.X + added.Y + added.Z) && Vector3.DistanceSquared(added, st.Hooked) >= 1e-8f)
        {
            st.Hooked = added;
            pass = want - added;
            chr->GameObject.SetDrawOffset(pass.X, pass.Y, pass.Z);
        }

        return true;
    }

    // Walks the object table, not states: a tracked address may have been freed.
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
                var chr = (Character*)o.Address;
                Vector3 off = chr->GameObject.DrawOffset;
                SetOffset(chr, st, off with { Y = off.Y - st.Written });
            }

            if (st.MoveWritten != Vector3.Zero)
            {
                ShiftDraw(o.Address, -st.MoveWritten);
            }
        }

        this.states.Clear();
        this.Tracked = 0;
    }

    public ValueTask DisposeAsync()
    {
        Commands.RemoveHandler("/ik");
        Framework.Update -= this.PlaySounds;
        PluginInterface.UiBuilder.Draw -= this.UpdateMesh;

        var window = this.overlay;
        this.overlay = null;
        if (window is not null)
        {
            PluginInterface.UiBuilder.Draw -= window.DrawWorldDots;
            PluginInterface.UiBuilder.OpenMainUi -= window.Toggle;
            PluginInterface.UiBuilder.OpenConfigUi -= window.Toggle;
        }

        return Overlay.ShutdownAsync(this, window);
    }

    // Framework thread only: Release reads the object table. Each hook comes down before the offsets it would re-add.
    internal void Teardown()
    {
        this.renderHook?.Dispose();
        this.Release();
        this.moveHook?.Dispose();
        this.DisposeMesh();
        this.DropShoveSound();
    }
}
