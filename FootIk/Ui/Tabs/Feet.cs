using System;
using System.Collections.Generic;
using Dalamud.Game.Text.SeStringHandling;
using KamiToolKit.BaseTypes;

namespace FootIk;

internal sealed partial class Overlay
{
    private static readonly string[] FeetFields =
    [
        nameof(Settings.Where), nameof(Settings.OpenWorld), nameof(Settings.Duties), nameof(Settings.WeaponDrawn), nameof(Settings.Cutscenes),
        nameof(Settings.KeepFeetOutOfWalls), nameof(Settings.MaxRaiseFrac), nameof(Settings.MaxKneeBendDeg),
        nameof(Settings.StraightenLimit), nameof(Settings.MaxAnkleAngleDeg), nameof(Settings.TiltFadeFrac), nameof(Settings.MaxDropFrac),
        nameof(Settings.MaxPelvisRaiseFrac), nameof(Settings.MaxStepDownFrac), nameof(Settings.BlendSeconds), nameof(Settings.PelvisTau),
        nameof(Settings.StrideTau), nameof(Settings.FootTau), nameof(Settings.WallClearanceFrac), nameof(Settings.MaxStepFrac), nameof(Settings.LiftThresholdFrac), nameof(Settings.RayUpFrac),
        nameof(Settings.RayDownFrac), nameof(Settings.RestAdjustFrac), nameof(Settings.SlopeLiftFrac),
    ];

    private List<NodeBase> BuildFeet()
    {
        var c = this.Owner.Settings;
        Func<bool> inGame = () => c.WorksIn(false);
        return
        [
            this.Pick("Enabled", () => c.Where, v => c.Where = v,
                "Where the plugin places feet. Group pose is off by default: posing tools such as Brio and Ktisis move the same bones, and the two will fight over them."),
            this.Check("Keep feet out of walls", () => c.KeepFeetOutOfWalls, v => c.KeepFeetOutOfWalls = v,
                "Stops the feet from clipping into walls, kerbs and steps by moving them slightly aside. Works best when feet gathering is enabled."),
            this.Section("Active in"),
            this.Check("Open world and cities", () => c.OpenWorld, v => c.OpenWorld = v,
                "Anywhere outside a duty: open areas, cities and housing.", inGame),
            this.Check("Duties", () => c.Duties, v => c.Duties = v,
                "Dungeons, trials, raids and other instanced content.", inGame),
            this.Check("Weapon drawn", () => c.WeaponDrawn, v => c.WeaponDrawn = v,
                "While the weapon is out. Other characters follow their own weapon.", inGame),
            this.Check(new Lumina.Text.SeStringBuilder().AppendIcon((uint)BitmapFontIcon.Warning).Append(" Cutscenes").ToReadOnlySeString(),
                () => c.Cutscenes, v => c.Cutscenes = v,
                "Experimental and may cause problems: the plugin was not made for how cutscenes stage characters, so feet and bodies may end up in the wrong place.", inGame),
            this.Fold("Advanced", () =>
            [
                this.Section("Feet/leg placement settings"),
                this.Slide("Max foot raise", () => c.MaxRaiseFrac, v => c.MaxRaiseFrac = v, 0f, 0.8f, 100f, v => $"{v:F2}  ({this.Metres(v)} m)",
                    "How far a foot may be lifted up. A foot that has to rise above this value will be underground. High values may cause unnatural poses."),
                this.Slide("Max knee bend", () => c.MaxKneeBendDeg, v => c.MaxKneeBendDeg = v, 30f, 150f, 1f, v => $"{v:F0} deg",
                    "How far the knee may bend. Values that are too low may cause the highest foot to clip into the floor. Values that are too high may cause unnatural poses."),
                this.Slide("Straighten limit", () => c.StraightenLimit, v => c.StraightenLimit = v, 0.90f, 0.999f, 1000f, v => $"{v:F3}",
                    "How far a leg can extend to reach its target. Helps races with hunched backs (e.g. male Hrothgar) more naturally place their feet."),
                this.Slide("Max tilt", () => c.MaxAnkleAngleDeg, v => c.MaxAnkleAngleDeg = v, 0f, 60f, 1f, v => $"{v:F0} deg",
                    "How far the foot may rotate to match the surface under it. Zero turns ankle alignment off."),
                this.Slide("Tilt fade", () => c.TiltFadeFrac, v => c.TiltFadeFrac = v, 0.01f, 0.3f, 100f, v => $"{v:F2}  ({this.Metres(v, 3)} m)",
                    "Tilt fades out over this height above resting height, so a lifting or heel-striking foot keeps the pose the animation gave it."),
                this.Section("Body placement settings"),
                this.Slide("Max body drop", () => c.MaxDropFrac, v => c.MaxDropFrac = v, 0f, 0.8f, 100f, v => $"{v:F2}  ({this.Metres(v)} m)",
                    "How far the body may sink toward a foot standing lower than the character. Low values may cause the lowest foot to hover above the ground. High values may cause unnatural poses."),
                this.Slide("Max body rise", () => c.MaxPelvisRaiseFrac, v => c.MaxPelvisRaiseFrac = v, 0f, 0.4f, 100f, v => $"{v:F2}  ({this.Metres(v)} m)",
                    "How far the body may rise when a foot stands higher than the character. Low values may cause the highest leg to bend too much. High values may cause unnatural poses."),
                this.Slide("Edge drop", () => c.MaxStepDownFrac, v => c.MaxStepDownFrac = v, 0.05f, 0.8f, 100f, v => $"{v:F2}  ({this.Metres(v)} m)",
                    "How far below the character a foot will reach for the ground. A foot over a bigger drop than this stays in place instead of stretching down."),
                this.Slide("Blend in/out", () => c.BlendSeconds, v => c.BlendSeconds = v, 0f, 1f, 100f, v => $"{v:F2} s",
                    "Fade time when the effect turns on or off, such as mounting or entering a cutscene."),
                this.Slide("Drop smoothing", () => c.PelvisTau, v => c.PelvisTau = v, 0.01f, 0.5f, 100f, v => $"{v:F2} s",
                    "Smoothing on the body height. Higher values will be smoother but might react late to floor height changes."),
                this.Slide("Running smoothing", () => c.StrideTau, v => c.StrideTau = v, 0f, 0.6f, 100f, v => $"{v:F2} s",
                    "How smoothly the body moves up and down while running, for example on stairs. Too low and the body lurches with every step. Too high and a foot may hover a moment as it lands. Zero makes the body and legs behave at every speed as they do when standing."),
                this.Slide("Foot smoothing", () => c.FootTau, v => c.FootTau = v, 0f, 0.15f, 100f, v => $"{v:F2} s",
                    "Smoothing on each foot's height as it moves onto a new stair step or ledge. A moving foot starts rising early for a step ahead of it. Too low and the feet jump from step to step. Too high and the feet lift early for steps and hover a moment after stepping down. Zero turns it off."),
                this.Section("Keep feet out of walls"),
                this.Slide("Foot width", () => c.WallClearanceFrac, v => c.WallClearanceFrac = v, 0f, 0.2f, 100f, v => $"{v:F2}  ({this.Metres(v * 2f)} m wide)",
                    "How wide the feet are considered to be when avoiding walls. Increase if the feet still clip into walls. Decrease if they stay too far away from them.",
                    () => c.KeepFeetOutOfWalls),
                this.Section("Collision detection settings"),
                this.Slide("Ignore ground beyond", () => c.MaxStepFrac, v => c.MaxStepFrac = v, 0.1f, 1.2f, 100f, v => $"{v:F2}  ({this.Metres(v)} m)",
                    "Ground further than this from standing height is not treated as a surface for that foot."),
                this.Slide("Planted threshold", () => c.LiftThresholdFrac, v => c.LiftThresholdFrac = v, 0.02f, 0.6f, 100f, v => $"{v:F2}  ({this.Metres(v)} m)",
                    "A foot within this height of its resting height carries full weight; weight fades to none at twice the distance."),
                this.Slide("Probe start", () => c.RayUpFrac, v => c.RayUpFrac = v, 0.1f, 2f, 100f, v => $"{v:F2}  ({this.Metres(v)} m)",
                    "How far above standing height the mod checks for floors. Must be high enough to correctly detect floors."),
                this.Slide("Probe reach", () => c.RayDownFrac, v => c.RayDownFrac = v, 0.1f, 3f, 100f, v => $"{v:F2}  ({this.Metres(v)} m)",
                    "How far down the mod checks for ground before reporting nothing there."),
                this.Section("Adjustments"),
                this.Slide("Rest adjust", () => c.RestAdjustFrac, v => c.RestAdjustFrac = v, -0.1f, 0.1f, 1000f, v => $"{v:F3}  ({this.Metres(v, 3)} m)",
                    "Manual correction to the base height of the feet. Change when the feet always seem misplaced."),
                this.Slide("Slope lift", () => c.SlopeLiftFrac, v => c.SlopeLiftFrac = v, 0f, 0.5f, 1000f, v => $"{v:F3}  ({this.Metres(v, 3)} m)",
                    "Extra foot raise proportional to how steep the ground is. Workaround to feet clipping slightly into the ground when standing on slopes."),
            ]),
        ];
    }
}
