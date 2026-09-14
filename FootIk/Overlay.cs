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
    private const string ConfirmTitle = "Reset settings";

    // What each tab's reset button puts back; the Status tab has no entry and resets everything instead.
    // nameof so renaming a setting breaks the build rather than leaving a button that quietly does nothing.
    private static readonly string[][] TabFields =
    [
        [
            nameof(Settings.Enabled), nameof(Settings.KeepFeetOutOfWalls), nameof(Settings.MaxRaiseFrac), nameof(Settings.MaxKneeBendDeg),
            nameof(Settings.StraightenLimit), nameof(Settings.MaxAnkleAngleDeg), nameof(Settings.TiltFadeFrac), nameof(Settings.MaxDropFrac),
            nameof(Settings.MaxPelvisRaiseFrac), nameof(Settings.MaxStepDownFrac), nameof(Settings.BlendSeconds), nameof(Settings.PelvisTau),
            nameof(Settings.WallClearanceFrac), nameof(Settings.MaxStepFrac), nameof(Settings.LiftThresholdFrac), nameof(Settings.RayUpFrac),
            nameof(Settings.RayDownFrac), nameof(Settings.RestAdjustFrac), nameof(Settings.SlopeLiftFrac),
        ],
        [
            nameof(Settings.GatherFeet), nameof(Settings.GatherToPosition), nameof(Settings.MinStanceFrac), nameof(Settings.GatherStraighten),
            nameof(Settings.GatherForward), nameof(Settings.GatherPrecision), nameof(Settings.GatherTauIn), nameof(Settings.GatherTauOut),
        ],
        [
            nameof(Settings.SlopeLean), nameof(Settings.LeanUphillGain), nameof(Settings.LeanDownhillGain), nameof(Settings.MaxLeanDeg),
            nameof(Settings.MoveWeapons), nameof(Settings.MaxTotalPitchDeg), nameof(Settings.MinTotalPitchDeg), nameof(Settings.LeanTau),
        ],
        [
            nameof(Settings.Bump), nameof(Settings.Shoved), nameof(Settings.Grunt), nameof(Settings.BumpCooldown),
            nameof(Settings.BumpSameCooldown), nameof(Settings.BumpRadiusFrac), nameof(Settings.BumpMaxDeg), nameof(Settings.BumpShoveFrac),
            nameof(Settings.BumpHeadHold), nameof(Settings.BumpBodyTurn), nameof(Settings.BumpMinSpeed), nameof(Settings.BumpRiseSeconds),
            nameof(Settings.BumpTau),
        ],
        [
            nameof(Settings.Emotes), nameof(Settings.FloorTilt), nameof(Settings.MaxSitTiltDeg), nameof(Settings.SitTiltTau),
        ],
        [
            nameof(Settings.Others), nameof(Settings.MaxOthers), nameof(Settings.OthersRadius), nameof(Settings.Who),
            nameof(Settings.OthersGatherPrecision), nameof(Settings.MeshRefine), nameof(Settings.MeshRadius), nameof(Settings.MeshBand),
        ],
    ];
    private readonly Plugin plugin;
    private readonly (string Name, Action Draw)[] tabs;

    // Sliders fill the row minus room for the labels. The reserve is the widest label drawn in this tab last frame,
    // which keeps them aligned without every call site repeating its label.
    private readonly float[] labelReserve;
    private int tab;
    private float rawPeak;
    private float lostPeak;
    private float labelMax;

    public Overlay(Plugin plugin) : base("Inverse Kinematics")
    {
        this.plugin = plugin;
        this.tabs =
        [
            ("Feet", this.DrawFeet),
            ("Edges", this.DrawEdges),
            ("Lean", this.DrawLean),
            ("Shoving", this.DrawShoving),
            ("Emotes", this.DrawEmotes),
            ("Performance", this.DrawPerformance),
            ("Status", this.DrawStatus),
        ];
        this.labelReserve = new float[this.tabs.Length];
        this.Size = new Vector2(440, 520);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        this.TabRow();
        this.Body();
        this.DrawConfirm();
    }

    // The bar is boxed into a child so the reset button can sit beside it on the same row and the tabs never run
    // under it, and so the tab bar and the button both stay put while the body below them scrolls.
    private void TabRow()
    {
        var label = this.tab >= TabFields.Length ? "Reset everything" : "Reset this tab";
        var style = ImGui.GetStyle();
        var button = ImGui.CalcTextSize(label).X + (style.FramePadding.X * 2f);
        var bar = MathF.Max(MinSliderWidth, ImGui.GetContentRegionAvail().X - button - style.ItemSpacing.X);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.BeginChild("bar", new Vector2(bar, ImGui.GetFrameHeight() + 2f), false))
        {
            if (ImGui.BeginTabBar("tabs"))
            {
                for (var i = 0; i < this.tabs.Length; i++)
                {
                    if (ImGui.BeginTabItem(this.tabs[i].Name))
                    {
                        this.tab = i;
                        ImGui.EndTabItem();
                    }
                }

                ImGui.EndTabBar();
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();

        ImGui.SameLine();
        if (ImGui.Button(label))
        {
            ImGui.OpenPopup(ConfirmTitle);
        }
    }

    // Drawn outside the tab bar, so the bar can be narrower than the settings under it. Each tab keeps its own
    // scroll position because the id is seeded with its index.
    private void Body()
    {
        this.labelMax = 0f;
        ImGui.PushID(this.tab);
        if (ImGui.BeginChild("body", Vector2.Zero, false))
        {
            ImGui.Spacing();
            this.tabs[this.tab].Draw();
        }

        ImGui.EndChild();
        ImGui.PopID();
        this.labelReserve[this.tab] = this.labelMax;
    }

    private void DrawConfirm()
    {
        if (!ImGui.BeginPopupModal(ConfirmTitle, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        var global = this.tab >= TabFields.Length;
        ImGui.TextUnformatted(global
            ? "Put every setting, on every tab, back to how it shipped?"
            : "Put the settings on this tab back to how they shipped? The other tabs are left alone.");
        ImGui.Spacing();
        if (ImGui.Button(global ? "Reset everything" : "Reset this tab"))
        {
            this.plugin.Settings.Reset(global ? null : TabFields[this.tab]);
            this.plugin.SaveSettings();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private static bool Advanced() => ImGui.CollapsingHeader("Advanced");

    private void SetWidth(string label)
    {
        var style = ImGui.GetStyle();
        this.labelMax = MathF.Max(this.labelMax, ImGui.CalcTextSize(label).X);
        var reserve = this.labelReserve[this.tab] + ImGui.CalcTextSize("(?)").X + style.ItemInnerSpacing.X + (style.ItemSpacing.X * 2f);
        ImGui.SetNextItemWidth(MathF.Max(MinSliderWidth, ImGui.GetContentRegionAvail().X - reserve));
    }

    private void Slider(string label, ref float v, float min, float max, string format, string help)
    {
        this.SetWidth(label);
        ImGui.SliderFloat(label, ref v, min, max, format);

        // On release rather than on change: a drag would otherwise write the file every frame.
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            this.plugin.SaveSettings();
        }

        Help(help);
    }

    private void SliderInt(string label, ref int v, int min, int max, string format, string help)
    {
        this.SetWidth(label);
        ImGui.SliderInt(label, ref v, min, max, format);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            this.plugin.SaveSettings();
        }

        Help(help);
    }

    private void Combo(string label, ref Who v, string items, string help)
    {
        this.SetWidth(label);
        var i = (int)v;
        if (ImGui.Combo(label, ref i, items))
        {
            v = (Who)i;
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
        ImGui.Spacing();
        this.Check("Keep feet out of walls", ref c.KeepFeetOutOfWalls);
        Help("Stops the feet from clipping into walls, kerbs and steps by moving them slightly aside. Works best when feet gathering is enabled.");

        if (Advanced())
        {
            ImGui.Text("Feet/leg placement settings");
            this.Slider("Max foot raise", ref c.MaxRaiseFrac, 0f, 0.8f, $"%.2f  ({c.MaxRaiseFrac * L:F2} m)",
                "How far a foot may be lifted up. A foot that has to rise above this value will be underground. High values may cause unnatural poses.");
            this.Slider("Max knee bend", ref c.MaxKneeBendDeg, 30f, 150f, "%.0f deg",
                "How far the knee may bend. Values that are too low may cause the highest foot to clip into the floor. Values that are too high may cause unnatural poses.");
            this.Slider("Straighten limit", ref c.StraightenLimit, 0.90f, 0.999f, "%.3f",
                "How far a leg can extend to reach its target. Helps races with hunched backs (e.g. male Hrothgar) more naturally place their feet.");
            this.Slider("Max tilt", ref c.MaxAnkleAngleDeg, 0f, 60f, "%.0f deg",
                "How far the foot may rotate to match the surface under it. Zero turns ankle alignment off.");
            this.Slider("Tilt fade", ref c.TiltFadeFrac, 0.01f, 0.3f, $"%.2f  ({c.TiltFadeFrac * L:F3} m)",
                "Tilt fades out over this height above resting height, so a lifting or heel-striking foot keeps the pose the animation gave it.");
            ImGui.Spacing();
            ImGui.Text("Body placement settings");
            this.Slider("Max body drop", ref c.MaxDropFrac, 0f, 0.8f, $"%.2f  ({c.MaxDropFrac * L:F2} m)",
                "How far the body may sink toward a foot standing lower than the character. Low values may cause the lowest foot to hover above the ground. High values may cause unnatural poses.");
            this.Slider("Max body rise", ref c.MaxPelvisRaiseFrac, 0f, 0.4f, $"%.2f  ({c.MaxPelvisRaiseFrac * L:F2} m)",
                "How far the body may rise when a foot stands higher than the character. Low values may cause the highest leg to bend too much. High values may cause unnatural poses.");
            this.Slider("Edge drop", ref c.MaxStepDownFrac, 0.05f, 0.8f, $"%.2f  ({c.MaxStepDownFrac * L:F2} m)",
                "How far below the character a foot will reach for the ground. A foot over a bigger drop than this stays in place instead of stretching down.");
            this.Slider("Blend in/out", ref c.BlendSeconds, 0f, 1f, "%.2f s",
                "Fade time when the effect turns on or off, such as mounting or entering a cutscene.");
            this.Slider("Drop smoothing", ref c.PelvisTau, 0.01f, 0.5f, "%.2f s",
                "Smoothing on the body height. Higher values will be smoother but might react late to floor height changes.");
            ImGui.Spacing();
            ImGui.BeginDisabled(c.KeepFeetOutOfWalls);
            ImGui.Text("Keep feet out of walls");
            this.Slider("Foot width", ref c.WallClearanceFrac, 0f, 0.2f, $"%.2f  ({c.WallClearanceFrac * 2f * L:F2} m wide)",
                "How wide the feet are considered to be when avoiding walls. Increase if the feet still clip into walls. Decrease if they stay too far away from them.");
            ImGui.EndDisabled();
            ImGui.Spacing();
            ImGui.Text("Collision detection settings");
            this.Slider("Ignore ground beyond", ref c.MaxStepFrac, 0.1f, 1.2f, $"%.2f  ({c.MaxStepFrac * L:F2} m)",
                "Ground further than this from standing height is not treated as a surface for that foot.");
            this.Slider("Planted threshold", ref c.LiftThresholdFrac, 0.02f, 0.6f, $"%.2f  ({c.LiftThresholdFrac * L:F2} m)",
                "A foot within this height of its resting height carries full weight; weight fades to none at twice the distance.");
            this.Slider("Probe start", ref c.RayUpFrac, 0.1f, 2f, $"%.2f  ({c.RayUpFrac * L:F2} m)",
                "How far above standing height the mod checks for floors. Must be high enough to correctly detect floors.");
            this.Slider("Probe reach", ref c.RayDownFrac, 0.1f, 3f, $"%.2f  ({c.RayDownFrac * L:F2} m)",
                "How far down the mod checks for ground before reporting nothing there.");
            ImGui.Spacing();
            ImGui.Text("Adjustments");
            this.Slider("Rest adjust", ref c.RestAdjustFrac, -0.1f, 0.1f, $"%.3f  ({c.RestAdjustFrac * L:F3} m)",
                "Manual correction to the base height of the feet. Change when the feet always seem misplaced.");
            this.Slider("Slope lift", ref c.SlopeLiftFrac, 0f, 0.5f, $"%.3f  ({c.SlopeLiftFrac * L:F3} m)",
                "Extra foot raise proportional to how steep the ground is. Workaround to feet clipping slightly into the ground when standing on slopes.");
        }
    }

    private void DrawEdges()
    {
        var c = this.plugin.Settings;
        var L = this.plugin.Snap.LegLength;

        this.Check("Gather feet", ref c.GatherFeet);
        Help("Move your character's feet together when standing on narrow platforms to avoid them floating over an edge.");
        ImGui.Spacing();
        ImGui.BeginDisabled(!c.GatherFeet);
        this.Check("Platforming mode", ref c.GatherToPosition);
        Help("Enabling this prioritises feet positions that avoid sliding your character off-centre. This helps better seeing where your character really is, at the cost of worse poses.");
        this.SliderInt("Precision", ref c.GatherPrecision, 1, 4, $"%d  ({4 * c.GatherPrecision} directions)",
            "How carefully the plugin looks around each foot for ground to stand on. Higher finds narrow rails and beams more reliably and keeps the feet steadier, but costs more each frame. Lower it if the game slows down near edges.");
        ImGui.EndDisabled();

        if (Advanced())
        {
            ImGui.BeginDisabled(!c.GatherFeet);
            ImGui.Spacing();
            this.Slider("Min stance", ref c.MinStanceFrac, 0.02f, 0.4f, $"%.2f  ({c.MinStanceFrac * L:F2} m)",
                "How close the feet can be of each other when gathering. Avoids the feet crossing or overlapping each other.");
            this.Slider("Straighten legs", ref c.GatherStraighten, 0f, 1f, "%.2f",
                "How much the legs straighten when the feet are gathered. Avoids legs being flexed while gathered on some races.");
            this.Slider("Feet forward", ref c.GatherForward, 0f, 1f, "%.2f",
                "How strongly the knees and feet turn to face forward when gathered. Avoids duck feet on some races.");
            this.Slider("Ease in", ref c.GatherTauIn, 0.05f, 1f, "%.2f s",
                "How quickly the feet move in onto a support.");
            this.Slider("Ease out", ref c.GatherTauOut, 0.05f, 1f, "%.2f s",
                "How quickly they return when stepping off.");
            ImGui.EndDisabled();

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
        this.Slider("Uphill gain", ref c.LeanUphillGain, 0f, 2f, "%.2f",
            "How strongly the character leans forwards when running uphill, at full run speed.");
        this.Slider("Downhill gain", ref c.LeanDownhillGain, 0f, 2f, "%.2f",
            "How far the character leans back when running downhill.");
        this.Slider("Max lean", ref c.MaxLeanDeg, 0f, 45f, "%.0f deg",
            "How far the character can lean in either direction.");
        ImGui.EndDisabled();

        Section("Carried weapons");
        this.Check("Weapons follow the body", ref c.MoveWeapons);
        Help("Sheathed weapons and shields ride along when the upper body leans, turns or tilts, instead of staying where the animation left them. Turn it off if a weapon ends up floating.");

        if (Advanced())
        {
            this.Slider("Max spine pitch", ref c.MaxTotalPitchDeg, 10f, 90f, "%.0f deg",
                "Limits how much the character may lean while taking account their base animations. Avoids hunched races lean forwards too much.");
            this.Slider("Min spine pitch", ref c.MinTotalPitchDeg, -45f, 0f, "%.0f deg",
                "Limits how far back the character may lean, taking account of their base animation. Avoids upright races arching backwards when running downhill.");
            this.Slider("Lean smoothing", ref c.LeanTau, 0.05f, 1f, "%.2f s",
                "Smoothing on the lean angle, so a sudden change in terrain does not snap the torso.");
        }
    }

    private void DrawShoving()
    {
        var c = this.plugin.Settings;
        var L = this.plugin.Snap.LegLength;

        this.Check("Flinch when running into someone", ref c.Bump);
        Help("Allows you to shove people when running into them. The shove depends on the speed and angle of impact, the difference in height of both people, and other factors.");
        ImGui.Spacing();
        ImGui.BeginDisabled(!c.Bump);
        this.Check("Let others shove you", ref c.Shoved);
        Help("Other players running or walking into you shove you as well.");
        ImGui.TextDisabled("They flinch back only when \"Other players\" or \"Everyone\" is set in the performance settings.");
        ImGui.EndDisabled();

        ImGui.BeginDisabled(!c.Bump);
        this.Check("Grunt when shoved", ref c.Grunt);
        Help("Make both characters grunt when they one or the other is shoved. Has a random chance of playing a grunt.");
        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.BeginDisabled(!c.Bump);
        ImGui.Text("Cooldowns");
        this.Slider("Bump cooldown", ref c.BumpCooldown, 0f, 3f, "%.1f s",
            "The least time between two bumps, whoever they are with. Raise it if a crowd keeps your character flinching.");
        this.Slider("Same character", ref c.BumpSameCooldown, 0f, 10f, "%.1f s",
            "How long before running into the same character again counts as a new bump.");
        ImGui.EndDisabled();
        
        if (Advanced()) {
            ImGui.BeginDisabled(!c.Bump);
            this.Slider("Bump distance", ref c.BumpRadiusFrac, 0.1f, 0.8f, $"%.2f  ({c.BumpRadiusFrac * 2f * L:F2} m apart)",
                "How close two characters must come to count as touching. Too low and you pass through people without a reaction; too high and you flinch at people you clearly missed.");
            this.Slider("Bump strength", ref c.BumpMaxDeg, 0f, 60f, "%.0f deg",
                "How far the upper body tips away and turns towards the other character at the moment of impact.");
            this.Slider("Give way", ref c.BumpShoveFrac, 0f, 0.5f, $"%.2f  ({c.BumpShoveFrac * L * 100f:F0} cm)",
                "How far the hips are pushed off the feet, which is what bends the knees and makes a standing character look shoved rather than folded at the waist. Too high and they sink into a crouch.");
            this.Slider("Keep looking ahead", ref c.BumpHeadHold, 0f, 1f, "%.2f",
                "How much the neck holds the head still while the body is shoved out from under it. Full keeps the character looking where it was looking; zero lets the head ride round with the shoulders.");
            this.Slider("Whole-body turn", ref c.BumpBodyTurn, 0f, 1.5f, "%.2f",
                "How much of the turn the whole body takes at running speed, hips and all, on top of the shoulders; the legs keep following the stride. Zero keeps every bump in the upper body; standing characters always do.");
            this.Slider("Bump speed", ref c.BumpMinSpeed, 0.2f, 10f, "%.1f m/s",
                "How fast you must be moving towards someone for it to count as a bump. Set it above walking speed and only running bumps.");
            this.Slider("Bump rise", ref c.BumpRiseSeconds, 0.02f, 0.3f, "%.2f s",
                "How quickly the body reaches its full flinch after the impact.");
            this.Slider("Bump recovery", ref c.BumpTau, 0.1f, 1.5f, "%.2f s",
                "How long the body takes to straighten up again.");
            ImGui.EndDisabled();
        }
    }

    private void DrawEmotes()
    {
        var c = this.plugin.Settings;

        this.Check("Work during emotes", ref c.Emotes);
        Help("Keeps placing the feet through dances and other looping emotes, and settles the body onto the slope when sitting or sleeping on the ground.");
        ImGui.Spacing();

        ImGui.BeginDisabled(!c.Emotes);
        this.Check("Tilt lying-down poses too", ref c.FloorTilt);
        Help("Turns the body to follow the slope when lying on the ground, not just when sitting. Affects emotes like pushups and playing dead. Turn it off if the tilt looks worse than leaving the animation alone.");
        this.Slider("Max sit tilt", ref c.MaxSitTiltDeg, 0f, 45f, "%.0f deg",
            "How far the body may tilt to rest on sloped ground when sitting or sleeping on it. Zero keeps the body upright.");
        this.Slider("Sit tilt smoothing", ref c.SitTiltTau, 0.05f, 1.5f, "%.2f s",
            "How quickly the body settles onto the slope when sitting down, and comes back up when standing.");
        ImGui.EndDisabled();
    }

    private void DrawPerformance()
    {
        var c = this.plugin.Settings;

        this.Check("Apply on other characters", ref c.Others);
        Help("Applies IK to other players and NPCs around you. Each one costs FPS. Tweak the settings if the game stutters too much.");
        ImGui.Spacing();

        ImGui.BeginDisabled(!c.Others);
        this.SliderInt("Max characters", ref c.MaxOthers, 1, 50, "%d",
            "How many characters to apply IK to. Lower this if your frame rate drops in crowds.");
        this.Slider("Max distance", ref c.OthersRadius, 3f, 50f, "%.0f yalms",
            "How far away a character can be and still have its feet placed.");
        this.Combo("Apply IK to", ref c.Who, "Everyone\0Other players\0Friends and party\0Party only\0",
            "Which characters around you have IK applied. \"Everyone\" includes NPCs.");
        ImGui.BeginDisabled(!c.GatherFeet);
        this.SliderInt("Gather feet precision", ref c.OthersGatherPrecision, 0, 4,
            c.OthersGatherPrecision == 0 ? "off" : $"%d  ({4 * c.OthersGatherPrecision} directions)",
            "Whether other characters also get their feet gathered onto narrow ledges and rails, and how carefully. Needs Gather feet on the Edges tab.");
        ImGui.EndDisabled();
        ImGui.EndDisabled();

        ImGui.Spacing();
        this.Check("Use higher precision collision", ref c.MeshRefine);
        Help("Places feet on the ground you actually see instead of the simplified shape the game uses for walking. Fixes stairs that otherwise behave like a ramp. Uses some memory, and a little frame time while a new area loads.");

        if (Advanced())
        {
            ImGui.BeginDisabled(!c.MeshRefine);
            this.Slider("Read distance", ref c.MeshRadius, 10f, 60f, "%.0f yalms",
                "How far around you the visible ground is read. Keep it at least as far as Max distance above, or far-away characters fall back to the simple shape.");
            this.Slider("Max difference", ref c.MeshBand, 0.05f, 1f, "%.2f m",
                "How far the visible ground may be from the simple shape and still be believed. Too low and stairs are missed; too high and a foot may land on furniture.");
            ImGui.EndDisabled();
        }

        ImGui.Spacing();
        ImGui.TextDisabled($"Working on {this.plugin.Tracked} character{(this.plugin.Tracked == 1 ? string.Empty : "s")}.");
        ImGui.TextDisabled($"Frame cost {this.plugin.LastMicros:F0} us, max {this.plugin.MaxMicros:F0} us.");
    }

    private void DrawStatus()
    {
        var p = this.plugin;
        var c = p.Settings;
        ref var s = ref p.Snap;

        var hooked = p.HookStatus.StartsWith("hooked", StringComparison.Ordinal) && p.MoveHookStatus.StartsWith("hooked", StringComparison.Ordinal);
        if (p.Tripped)
        {
            ImGui.TextWrapped($"Stopped after {p.Faults} errors. Everyone is back at the game's own height.");
            if (ImGui.Button("Start again"))
            {
                p.Rearm();
            }
        }
        else
        {
            ImGui.TextUnformatted(hooked && p.SelfTest == "PASS" ? "Running." : "Not running. See Details below.");
        }

        if (p.LastError is not null)
        {
            ImGui.TextWrapped($"Last error: {p.LastError}");
        }

        ImGui.TextUnformatted($"Frame cost {p.LastMicros:F0} us, peak {p.MaxMicros:F0} us");
        this.Check("Show markers in the world", ref c.ShowMarkers);
        this.Check("Show a one-metre ruler at your feet", ref c.ShowRuler);

        Section("Your character");
        ImGui.TextUnformatted(Activity(in s, c.Enabled));
        ImGui.TextUnformatted($"Moving at {s.Speed:F1} m/s");
        ImGui.TextUnformatted($"Body height {Cm(s.Applied)}   lean {s.LeanDeg:F0} deg   bump {s.BumpDeg:F0} deg   tilt to the ground {s.SitTiltDeg:F0} deg");

        Section("Feet");
        ref var l = ref s.Left;
        ref var r = ref s.Right;
        if (ImGui.BeginTable("feet", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn(" ");
            ImGui.TableSetupColumn("Left");
            ImGui.TableSetupColumn("Right");
            ImGui.TableHeadersRow();
            Row("standing", State(in l), State(in r));
            Row("planted", $"{l.Planted:P0}", $"{r.Planted:P0}");
            Row("height", Cm(l.Offset), Cm(r.Offset));
            Row("tilt", $"{l.TiltDeg:F0} deg", $"{r.TiltDeg:F0} deg");
            Row("moved", Cm((l.GatherShift + l.WallShift).Length()), Cm((r.GatherShift + r.WallShift).Length()));
            ImGui.EndTable();
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Details"))
        {
            ImGui.TextUnformatted($"Render hook: {p.HookStatus}");
            ImGui.TextUnformatted($"Move hook: {p.MoveHookStatus}");
            ImGui.TextUnformatted($"Solver self-test: {p.SelfTest}");
            ImGui.TextUnformatted($"Faults: {p.Faults}  {(p.Tripped ? "TRIPPED" : "armed")}");
            ImGui.TextUnformatted($"Gate {s.Gate}   blend {s.Blend:F2}   speed {s.Speed:F2} m/s");
            ImGui.TextUnformatted($"Mode {s.Mode} ({s.ModeParam})   jumping {s.IsJumping}   gpose {s.GPose}   blocking condition {s.Conditions}");
            // Peaks decay slowly so a value that only exists for a frame or two while walking can still be read off.
            this.rawPeak = MathF.Max(this.rawPeak * 0.99f, MathF.Abs(s.RawDrop));
            ImGui.TextUnformatted($"Body drop raw {s.RawDrop:F3}  smooth {s.SmoothDrop:F3}  applied {s.Applied:F3}  peak {this.rawPeak:F3}");
            this.lostPeak = MathF.Max(this.lostPeak * 0.99f, MathF.Abs(s.OffsetSeen - s.OffsetWritten));
            ImGui.TextUnformatted($"Draw offset ours {s.OffsetWritten:F3}  game holds {s.OffsetSeen:F3}  lost peak {this.lostPeak:F3}");
            if (ImGui.SmallButton("Reset peaks"))
            {
                this.rawPeak = 0f;
                this.lostPeak = 0f;
            }

            ImGui.TextUnformatted($"Ground under body {s.BaseY:F3}   body shift {Fmt(s.BodyShift)}");
            ImGui.TextUnformatted($"Spine pitch {s.SpinePitchDeg:F1} deg   lean {s.LeanDeg:F1} deg   body height {s.Height:F2}   weapons following {s.Weapons}");
            ImGui.TextUnformatted($"On the floor {s.OnFloor}   hips {s.HipFrac:F2}   body tilt {s.SitTiltDeg:F1} deg");
            if (s.HasPose && !s.ChainResolved)
            {
                ImGui.TextUnformatted("Chain: not resolved (j_asi_[a,b,d,e]_[lr] missing)");
            }

            if (ImGui.BeginTable("feetdetail", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
            {
                ImGui.TableSetupColumn(" ");
                ImGui.TableSetupColumn("Left");
                ImGui.TableSetupColumn("Right");
                ImGui.TableHeadersRow();
                Row("ground hit", l.Hit, r.Hit);
                Row("ground Y", l.GroundModelY, r.GroundModelY, "F3");
                Row("ankle above ground", l.AnkleAboveGround, r.AnkleAboveGround, "F3");
                Row("rest (bind)", l.Rest, r.Rest, "F3");
                Row("delta", l.Hit ? l.Rest - l.AnkleAboveGround : 0f, r.Hit ? r.Rest - r.AnkleAboveGround : 0f, "F3");
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
        }

        if (ImGui.CollapsingHeader("Higher precision collision"))
        {
            if (!c.MeshRefine)
            {
                ImGui.TextDisabled("Off. Turn it on from the Performance tab.");
            }
            else
            {
                p.MeshDetail = true;
                ImGui.TextUnformatted(p.MeshStatus);
                ImGui.TextUnformatted($"Parts {p.MeshParts}, with collider in range {p.MeshWithCollider}, terrain plates {p.MeshPlates}, unreadable {p.MeshFailed}");
                ImGui.TextUnformatted($"Scan {p.MeshScanMs:F1} ms   build (worker) {p.MeshBuildMs:F1} ms, max {p.MeshBuildMaxMs:F1}");
                ImGui.TextUnformatted($"Refined {p.MeshRefined}   missed {p.MeshMissed}   last delta {p.MeshLastDelta:+0.000;-0.000} m");
                ImGui.TextWrapped($"Last refined by {p.MeshLastPath}  ({(p.MeshLastSolid ? "collider" : "NO collider")}, triangle {p.MeshLastSpan:F0} m wide, facing {(p.MeshLastUp ? "up" : "down")})");
                ImGui.TextUnformatted($"Rays this frame {p.MeshRaysPerFrame} over {p.MeshObjects} objects = {p.MeshRaysPerFrame * p.MeshObjects} bounds tests");
                ImGui.TextWrapped($"Terrain plate: {p.MeshPlateSample}");
                ImGui.Spacing();
                ImGui.TextDisabled("Parts whose footprint contains you, nearest surface first, and what they hold under you.");
                if (p.MeshUnder.Count == 0)
                {
                    ImGui.TextDisabled("Reading, on the next scan.");
                }

                foreach (var (_, line) in p.MeshUnder)
                {
                    ImGui.TextWrapped(line);
                }

                if (p.MeshHuge.Count > 0)
                {
                    ImGui.Spacing();
                    ImGui.TextDisabled($"Never read: bounding sphere over {Plugin.MeshMaxSphere:F0} m.");
                    foreach (var line in p.MeshHuge)
                    {
                        ImGui.TextWrapped(line);
                    }
                }

                ImGui.Spacing();
                ImGui.TextDisabled($"Parts within 6 m of you ({p.MeshOrigin.X:F1}, {p.MeshOrigin.Z:F1}). A centre far from the part you stand on means its placement is not world-space.");
                foreach (var (_, line) in p.MeshNearby)
                {
                    ImGui.TextWrapped(line);
                }
            }
        }
    }

    private static string Activity(in Snapshot s, bool enabled)
    {
        if (!enabled)
        {
            return "Switched off.";
        }

        if (!s.HasPose)
        {
            return "No character to work on.";
        }

        if (s.GPose)
        {
            return "Paused in group pose.";
        }

        if (s.Conditions)
        {
            return "Paused: mounted, swimming, flying, in a cutscene or loading.";
        }

        if (s.IsJumping)
        {
            return "Paused while jumping.";
        }

        if (s.OnFloor)
        {
            return "Lying on the floor: only the body tilt applies.";
        }

        if (!s.Gate)
        {
            return $"Paused during {s.Mode}.";
        }

        return s.Blend < 0.99f ? $"Fading in, {s.Blend:P0}." : "Placing feet.";
    }

    private static string Cm(float metres) => $"{metres * 100f:+0;-0;0} cm";

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
        var c = this.plugin.Settings;
        if (!s.HasPose || (!c.ShowMarkers && !c.ShowRuler))
        {
            return;
        }

        var dl = ImGui.GetBackgroundDrawList();
        if (c.ShowMarkers)
        {
            DrawFootDots(dl, in s.Left);
            DrawFootDots(dl, in s.Right);
        }

        if (c.ShowRuler)
        {
            DrawRuler(dl, in s);
        }
    }

    // One metre along the character's right on the ground under the body and one metre up from it, a tick every ten centimetres.
    private static void DrawRuler(ImDrawListPtr dl, in Snapshot s)
    {
        var side = new Vector3(MathF.Cos(s.Yaw), 0f, -MathF.Sin(s.Yaw));
        DrawRuler(dl, s.Ground, side, Vector3.UnitY);
        DrawRuler(dl, s.Ground, Vector3.UnitY, side);
    }

    private static void DrawRuler(ImDrawListPtr dl, Vector3 from, Vector3 along, Vector3 tick)
    {
        if (!Plugin.GameGui.WorldToScreen(from, out var a) || !Plugin.GameGui.WorldToScreen(from + along, out var b))
        {
            return;
        }

        dl.AddLine(a, b, 0xFFFFFFFF, 2f);
        for (var i = 0; i <= 10; i++)
        {
            var p = from + (along * (0.1f * i));
            var t = tick * (i % 5 == 0 ? 0.06f : 0.03f);
            if (Plugin.GameGui.WorldToScreen(p, out var p0) && Plugin.GameGui.WorldToScreen(p + t, out var p1))
            {
                dl.AddLine(p0, p1, 0xFFFFFFFF, 2f);
            }
        }

        dl.AddText(b + new Vector2(6f, -8f), 0xFFFFFFFF, "1 m");
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
