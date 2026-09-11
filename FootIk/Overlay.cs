using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FootIk;

internal sealed class Overlay : Window
{
    // Labels are short names: an ImGui label sits to the right of its widget, so a long one widens the whole window.
    // Units and computed metres go in the slider's format string, explanations in tooltips.
    private const float MinSliderWidth = 80f;

    private readonly Plugin plugin;

    // Sliders fill the row minus room for the labels. The reserve is the widest label drawn in this tab last frame,
    // which keeps them aligned without every call site repeating its label.
    private readonly float[] labelReserve = new float[6];
    private int tab;
    private float labelMax;

    public Overlay(Plugin plugin) : base("Inverse Kinematics")
    {
        this.plugin = plugin;
        this.Size = new Vector2(440, 520);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("tabs"))
        {
            return;
        }

        this.Tab(0, "Feet", this.DrawFeet);
        this.Tab(1, "Ankles", this.DrawAnkles);
        this.Tab(2, "Edges", this.DrawEdges);
        this.Tab(3, "Lean", this.DrawLean);
        this.Tab(4, "Emotes", this.DrawEmotes);
        this.Tab(5, "Status", this.DrawStatus);
        ImGui.EndTabBar();
    }

    private void Tab(int index, string name, Action body)
    {
        if (!ImGui.BeginTabItem(name))
        {
            return;
        }

        this.tab = index;
        this.labelMax = 0f;
        ImGui.Spacing();
        body();
        this.labelReserve[index] = this.labelMax;
        ImGui.EndTabItem();
    }

    private static bool Advanced() => ImGui.CollapsingHeader("Advanced");

    private void Slider(string label, ref float v, float min, float max, string format, string help)
    {
        var style = ImGui.GetStyle();
        this.labelMax = MathF.Max(this.labelMax, ImGui.CalcTextSize(label).X);
        var reserve = this.labelReserve[this.tab] + ImGui.CalcTextSize("(?)").X + style.ItemInnerSpacing.X + (style.ItemSpacing.X * 2f);
        ImGui.SetNextItemWidth(MathF.Max(MinSliderWidth, ImGui.GetContentRegionAvail().X - reserve));
        ImGui.SliderFloat(label, ref v, min, max, format);

        // On release rather than on change: a drag would otherwise write the file every frame.
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            this.plugin.SaveSettings();
        }

        Help(help);
    }

    private void SliderInt(string label, ref int v, int min, int max, string help)
    {
        var style = ImGui.GetStyle();
        this.labelMax = MathF.Max(this.labelMax, ImGui.CalcTextSize(label).X);
        var reserve = this.labelReserve[this.tab] + ImGui.CalcTextSize("(?)").X + style.ItemInnerSpacing.X + (style.ItemSpacing.X * 2f);
        ImGui.SetNextItemWidth(MathF.Max(MinSliderWidth, ImGui.GetContentRegionAvail().X - reserve));
        ImGui.SliderInt(label, ref v, min, max);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            this.plugin.SaveSettings();
        }

        Help(help);
    }

    private void Check(string label, ref bool v)
    {
        if (ImGui.Checkbox(label, ref v))
        {
            this.plugin.SaveSettings();
        }
    }

    private static void Help(string help)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (!ImGui.IsItemHovered())
        {
            return;
        }

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);
        ImGui.TextUnformatted(help);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static void Section(string name)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted(name);
        ImGui.Spacing();
    }

    private void DrawFeet()
    {
        var c = this.plugin.Settings;
        var L = this.plugin.Snap.LegLength;

        this.Check("Enabled", ref c.Enabled);
        ImGui.TextDisabled($"Leg length {L:F2} m. Distances scale with it.");
        ImGui.Spacing();

        Slider("Leg balance", ref c.LegBalance, 0f, 1f, "%.2f",
            "How much both legs share the effort when the feet stand at different heights. Low values keep one leg straight and bend the other a lot. High values bend both legs a little.");
        Slider("Max body drop", ref c.MaxDropFrac, 0f, 0.8f, $"%.2f  ({c.MaxDropFrac * L:F2} m)",
            "How far the body may sink toward a foot standing lower than the character. Low values may cause the lowest foot to hover above the ground. High values may cause unnatural poses.");
        Slider("Max body rise", ref c.MaxPelvisRaiseFrac, 0f, 0.4f, $"%.2f  ({c.MaxPelvisRaiseFrac * L:F2} m)",
            "How far the body may rise when a foot stands higher than the character. Low values may cause the highest leg to bend too much. High values may cause unnatural poses.");
        Slider("Max foot raise", ref c.MaxRaiseFrac, 0f, 0.8f, $"%.2f  ({c.MaxRaiseFrac * L:F2} m)",
            "How far a foot may be lifted up. A foot that has to rise above this value will be underground. High values may cause unnatural poses.");
        Slider("Edge drop", ref c.MaxStepDownFrac, 0.05f, 0.8f, $"%.2f  ({c.MaxStepDownFrac * L:F2} m)",
            "How far below the character a foot will reach for the ground. A foot over a bigger drop than this stays in place instead of stretching down.");
        this.Check("Keep feet out of walls", ref c.KeepFeetOutOfWalls);
        Help("Stops the feet from clipping into walls, kerbs and steps by moving them slightly aside.");
        if (c.KeepFeetOutOfWalls)
        {
            Slider("Foot width", ref c.WallClearanceFrac, 0f, 0.2f, $"%.2f  ({c.WallClearanceFrac * 2f * L:F2} m wide)",
                "How wide the feet are considered to be when avoiding walls. Increase if the feet still clip into walls. Decrease if they stay too far away from them.");
        }
        Slider("Straighten limit", ref c.StraightenLimit, 0.90f, 0.999f, "%.3f",
            "How far a leg can extend to reach its target. Helps races with hunched backs (e.g. male Hrothgar) more naturally place their feet.");
        Slider("Max knee bend", ref c.MaxKneeBendDeg, 30f, 150f, "%.0f deg",
            "How far the knee may bend. Values that are too low may cause the highest foot to clip into the floor. Values that are too high may cause unnatural poses.");
        Slider("Blend in/out", ref c.BlendSeconds, 0f, 1f, "%.2f s",
            "Fade time when the effect turns on or off, such as mounting or entering a cutscene.");
        Slider("Drop smoothing", ref c.PelvisTau, 0.01f, 0.5f, "%.2f s",
            "Smoothing on the body height. Higher values will be smoother but might react late to floor height changes.");

        if (Advanced())
        {
            Slider("Ignore ground beyond", ref c.MaxStepFrac, 0.1f, 1.2f, $"%.2f  ({c.MaxStepFrac * L:F2} m)",
                "Ground further than this from standing height is not treated as a surface for that foot.");
            Slider("Planted threshold", ref c.LiftThresholdFrac, 0.02f, 0.6f, $"%.2f  ({c.LiftThresholdFrac * L:F2} m)",
                "A foot within this height of its resting height carries full weight; weight fades to none at twice the distance.");
            Slider("Probe start", ref c.RayUpFrac, 0.1f, 2f, $"%.2f  ({c.RayUpFrac * L:F2} m)",
                "How far above standing height the mod checks for floors. Must be high enough to correctly detect floors.");
            Slider("Probe reach", ref c.RayDownFrac, 0.1f, 3f, $"%.2f  ({c.RayDownFrac * L:F2} m)",
                "How far down the mod checks for ground before reporting nothing there.");
            Slider("Rest adjust", ref c.RestAdjustFrac, -0.1f, 0.1f, $"%.3f  ({c.RestAdjustFrac * L:F3} m)",
                "Manual correction to the base height of the feet. Change when the feet seem always misplaced.");
            Slider("Slope lift", ref c.SlopeLiftFrac, 0f, 0.5f, $"%.3f  ({c.SlopeLiftFrac * L:F3} m)",
                "Extra foot raise proportional to how steep the ground is. Workaround to feet clipping slightly into the ground when standing on slopes.");
        }
    }

    private void DrawAnkles()
    {
        var c = this.plugin.Settings;
        var L = this.plugin.Snap.LegLength;

        Slider("Max tilt", ref c.MaxAnkleAngleDeg, 0f, 60f, "%.0f deg",
            "How far the foot may rotate to match the surface under it. Zero turns ankle alignment off.");

        if (Advanced())
        {
            Slider("Tilt fade", ref c.TiltFadeFrac, 0.01f, 0.3f, $"%.2f  ({c.TiltFadeFrac * L:F3} m)",
                "Tilt fades out over this height above resting height, so a lifting or heel-striking foot keeps the pose the animation gave it.");
        }
    }

    private void DrawEdges()
    {
        var c = this.plugin.Settings;
        var L = this.plugin.Snap.LegLength;

        this.Check("Gather feet", ref c.GatherFeet);
        ImGui.TextDisabled("Move your character's feet together when standing on narrow platforms to avoid them floating over an edge.");
        ImGui.Spacing();

        ImGui.BeginDisabled(!c.GatherFeet);
        Slider("Min stance", ref c.MinStanceFrac, 0.02f, 0.4f, $"%.2f  ({c.MinStanceFrac * L:F2} m)",
            "How close the feet can be of each other when gathering. Avoids the feet crossing or overlapping each other.");
        Slider("Straighten legs", ref c.GatherStraighten, 0f, 1f, "%.2f",
            "How much the legs straighten when the feet are gathered. Avoids legs being flexed while gathered on some races.");
        Slider("Feet forward", ref c.GatherForward, 0f, 1f, "%.2f",
            "How strongly the knees and feet turn to face forward when gathered. Avoids duck feet on some races.");
        this.SliderInt("Precision", ref c.GatherPrecision, 1, 4,
            "How carefully the plugin looks around each foot for ground to stand on. Higher finds narrow rails and beams more reliably and keeps the feet steadier, but costs more each frame. Lower it if the game slows down near edges.");
        ImGui.EndDisabled();

        if (Advanced())
        {
            Slider("Ease in", ref c.GatherTauIn, 0.05f, 1f, "%.2f s",
                "How quickly the feet move in onto a support.");
            Slider("Ease out", ref c.GatherTauOut, 0.05f, 1f, "%.2f s",
                "How quickly they return when stepping off.");
        }
    }

    private void DrawLean()
    {
        var c = this.plugin.Settings;

        this.Check("Lean into slopes", ref c.SlopeLean);
        Help("While moving, makes the character lean forwards or backwards when running uphill and downhill.");
        ImGui.TextDisabled("Balance against the slope while running.");
        ImGui.Spacing();

        ImGui.BeginDisabled(!c.SlopeLean);
        Slider("Uphill gain", ref c.LeanUphillGain, 0f, 2f, "%.2f",
            "How strongly the character leans forwards when running uphill, at full run speed.");
        Slider("Downhill gain", ref c.LeanDownhillGain, 0f, 2f, "%.2f",
            "How far the character leans back when running downhill.");
        Slider("Max lean", ref c.MaxLeanDeg, 0f, 45f, "%.0f deg",
            "How far the character can lean in either direction.");
        ImGui.EndDisabled();

        if (Advanced())
        {
            Slider("Max spine pitch", ref c.MaxTotalPitchDeg, 10f, 90f, "%.0f deg",
                "Limits how much the character may lean while taking account their base animations. Avoids hunched races lean forwards too much.");
            Slider("Lean smoothing", ref c.LeanTau, 0.05f, 1f, "%.2f s",
                "Smoothing on the lean angle, so a sudden change in terrain does not snap the torso.");
        }
    }

    private void DrawEmotes()
    {
        var c = this.plugin.Settings;

        this.Check("Work during emotes", ref c.Emotes);
        Help("Keeps placing the feet through dances and other looping emotes, and settles the body onto the slope when sitting or sleeping on the ground.");
        ImGui.Spacing();

        ImGui.BeginDisabled(!c.Emotes);
        Slider("Max sit tilt", ref c.MaxSitTiltDeg, 0f, 45f, "%.0f deg",
            "How far the body may tilt to rest on sloped ground when sitting or sleeping on it. Zero keeps the body upright.");
        Slider("Sit tilt smoothing", ref c.SitTiltTau, 0.05f, 1.5f, "%.2f s",
            "How quickly the body settles onto the slope when sitting down, and comes back up when standing.");
        ImGui.EndDisabled();
    }

    private void DrawStatus()
    {
        var p = plugin;
        ref var s = ref p.Snap;

        ImGui.TextUnformatted($"Render hook: {p.HookStatus}");
        ImGui.TextUnformatted($"Move hook: {p.MoveHookStatus}");
        ImGui.TextUnformatted($"Solver self-test: {p.SelfTest}");
        ImGui.TextUnformatted($"Tick: {p.LastMicros:F0} us, max {p.MaxMicros:F0} us");
        ImGui.TextUnformatted($"Faults: {p.Faults}  {(p.Tripped ? "TRIPPED" : "armed")}");
        if (p.LastError is not null)
        {
            ImGui.TextUnformatted($"Last error: {p.LastError}");
        }

        if (p.Tripped && ImGui.Button("Re-arm"))
        {
            p.Rearm();
        }

        this.Check("Show markers in the world", ref p.Settings.ShowMarkers);

        Section("State");
        ImGui.TextUnformatted($"Gate {s.Gate}   blend {s.Blend:F2}   speed {s.Speed:F2} m/s");
        ImGui.TextUnformatted($"Mode {s.Mode} ({s.ModeParam})   jumping {s.IsJumping}   gpose {s.GPose}");
        ImGui.TextUnformatted($"Blocking condition {s.Conditions}");
        ImGui.TextUnformatted($"Body drop raw {s.RawDrop:F3}  smooth {s.SmoothDrop:F3}  applied {s.Applied:F3}");
        ImGui.TextUnformatted($"Ground under body {s.BaseY:F3}");
        ImGui.TextUnformatted($"Body shift {Fmt(s.BodyShift)}");
        ImGui.TextUnformatted($"Spine pitch {s.SpinePitchDeg:F1} deg   lean {s.LeanDeg:F1} deg");
        ImGui.TextUnformatted($"Sitting {s.Sitting}   body tilt {s.SitTiltDeg:F1} deg");
        if (s.HasPose && !s.ChainResolved)
        {
            ImGui.TextUnformatted("Chain: not resolved (j_asi_[a,b,d,e]_[lr] missing)");
        }

        Section("Feet");
        if (!ImGui.BeginTable("feet", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            return;
        }

        ImGui.TableSetupColumn(" ");
        ImGui.TableSetupColumn("Left");
        ImGui.TableSetupColumn("Right");
        ImGui.TableHeadersRow();

        ref var l = ref s.Left;
        ref var r = ref s.Right;
        Row("ground hit", l.Hit, r.Hit);
        Row("state", State(in l), State(in r));
        Row("ground Y", l.GroundModelY, r.GroundModelY, "F3");
        Row("ankle above ground", l.AnkleAboveGround, r.AnkleAboveGround, "F3");
        Row("rest (bind)", l.Rest, r.Rest, "F3");
        Row("delta", l.Delta, r.Delta, "F3");
        Row("planted", l.Planted, r.Planted, "F2");
        Row("can extend", l.MaxExtend, r.MaxExtend, "F3");
        Row("can raise", l.MaxRaiseByKnee, r.MaxRaiseByKnee, "F3");
        Row("offset", l.Offset, r.Offset, "F3");
        Row("contact", l.Contact, r.Contact, "F2");
        Row("tilt", l.TiltDeg, r.TiltDeg, "F1");
        Row("yaw", l.YawDeg, r.YawDeg, "F1");
        Row("solved", l.Solved, r.Solved);
        Row("gather shift", Fmt(l.GatherShift), Fmt(r.GatherShift));
        Row("gather refused", l.GatherBlock.ToString(), r.GatherBlock.ToString());
        Row("wall push", Fmt(l.WallShift), Fmt(r.WallShift));
        Row("ankle (model)", Fmt(l.AnkleModel), Fmt(r.AnkleModel));
        Row("ground point", Fmt(l.HitPoint), Fmt(r.HitPoint));
        Row("ground normal", Fmt(l.HitNormal), Fmt(r.HitNormal));
        Row("material", $"0x{l.Material:X}", $"0x{r.Material:X}");
        ImGui.EndTable();
    }

    private static string State(in FootSnapshot f) => f.Gathered ? "gathered" : f.OverEdge ? "over edge" : "on ground";

    private static void Row(string name, string left, string right)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(name);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(left);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(right);
    }

    private static void Row(string name, float left, float right, string format) =>
        Row(name, left.ToString(format), right.ToString(format));

    private static void Row(string name, bool left, bool right) => Row(name, left ? "yes" : "no", right ? "yes" : "no");

    public void DrawWorldDots()
    {
        ref var s = ref this.plugin.Snap;
        if (!this.plugin.Settings.ShowMarkers || !s.HasPose)
        {
            return;
        }

        var dl = ImGui.GetBackgroundDrawList();
        DrawFootDots(dl, in s.Left);
        DrawFootDots(dl, in s.Right);
    }

    private static void DrawFootDots(ImDrawListPtr dl, in FootSnapshot f)
    {
        if (Plugin.GameGui.WorldToScreen(f.AnkleWorld, out var a))
        {
            dl.AddCircleFilled(a, 5f, 0xFF00FF00);
        }

        if (f.Solved && Plugin.GameGui.WorldToScreen(f.IkTargetWorld, out var t))
        {
            dl.AddCircle(t, 6f, 0xFF00FFFF, 0, 2f);
        }

        if (f.Hit && Plugin.GameGui.WorldToScreen(f.HitPoint, out var g) && Plugin.GameGui.WorldToScreen(f.HitPoint + f.HitNormal * 0.25f, out var n))
        {
            dl.AddLine(g, n, 0xFFFFFF00, 2f);
        }
    }

    private static string Fmt(Vector3 v) => $"{v.X:F2}, {v.Y:F2}, {v.Z:F2}";
}
