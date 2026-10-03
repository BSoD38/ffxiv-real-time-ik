using System;
using System.Collections.Generic;
using System.Numerics;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

namespace FootIk;

internal sealed partial class Overlay
{
    private List<NodeBase> BuildStatus()
    {
        var p = this.Owner;
        var c = p.Settings;

        var rearm = new TextButtonNode
        {
            Height = BarHeight,
            Width = 140f,
            String = "Start again",
            OnClick = p.Rearm,
        };
        this.Gate(() => p.Tripped, on => rearm.IsVisible = on);

        return
        [
            this.Readout(() => p.Tripped
                ? $"Stopped after {p.Faults} errors. Everyone is back at the game's own height."
                : Hooked(p) ? "Running." : "Not running. See Details below."),
            rearm,
            this.Readout(() => p.LastError is null ? string.Empty : $"Last error: {p.LastError}"),
            this.Readout(() => $"Frame cost {p.LastMicros:F0} us"),
            this.Check("Show markers in the world", () => c.ShowMarkers, v => c.ShowMarkers = v, "Draws a dot at each ankle and the ground under it."),
            this.Check("Show a one-metre ruler at your feet", () => c.ShowRuler, v => c.ShowRuler = v, "Draws a metre along the ground and a metre up, ticked every ten centimetres."),
            this.Section("Your character"),
            this.Readout(() => Activity(in p.Snap, c)),
            this.Readout(() => $"Moving at {p.Snap.Speed:F1} m/s"),
            this.Readout(() => $"Body height {Cm(p.Snap.Applied)}   lean {p.Snap.LeanDeg:F0} deg   straightened {p.Snap.UprightDeg:F0} deg   bump {p.Snap.BumpDeg:F0} deg   tilt to the ground {p.Snap.SitTiltDeg:F0} deg"),
            this.Readout(() => $"Last bumped into: {p.LastBump ?? "nobody yet"}"),
            this.Section("Feet"),
            this.ColumnHeads(),
            this.Columns("standing", () => State(in p.Snap.Left), () => State(in p.Snap.Right)),
            this.Columns("planted", () => $"{p.Snap.Left.Planted:P0}", () => $"{p.Snap.Right.Planted:P0}"),
            this.Columns("height", () => Cm(p.Snap.Left.Offset), () => Cm(p.Snap.Right.Offset)),
            this.Columns("tilt", () => $"{p.Snap.Left.TiltDeg:F0} deg", () => $"{p.Snap.Right.TiltDeg:F0} deg"),
            this.Columns("moved", () => Cm((p.Snap.Left.GatherShift + p.Snap.Left.WallShift).Length()), () => Cm((p.Snap.Right.GatherShift + p.Snap.Right.WallShift).Length())),
            this.Fold("Details", this.BuildDetails),
        ];
    }

    private List<NodeBase> BuildDetails()
    {
        var p = this.Owner;
        return
        [
            this.Section("Plugin"),
            this.Readout(() => $"Render hook: {p.HookStatus}"),
            this.Readout(() => $"Move hook: {p.MoveHookStatus}"),
            this.Readout(() => $"Solver self-test: {p.SelfTest}   faults {p.Faults}"),
            this.Readout(() => p.Snap.HasPose && !p.Snap.ChainResolved ? "Leg bones not found (j_asi_[a,b,d,e]_[lr] missing)" : string.Empty),
            this.Section("Gate"),
            this.Readout(() => $"Mode {p.Snap.Mode} ({p.Snap.ModeParam})   open {YesNo(p.Snap.Gate)}   blend {p.Snap.Blend:F2}"),
            this.Readout(() => $"Jumping {YesNo(p.Snap.IsJumping)}   group pose {YesNo(p.Snap.GPose)}   blocking condition {YesNo(p.Snap.Conditions)}   on the floor {YesNo(p.Snap.OnFloor)}"),
            this.Readout(() => $"In a duty {YesNo(p.Snap.InDuty)}   cutscene {YesNo(p.Snap.InCutscene)}   weapon drawn {YesNo(p.Snap.WeaponDrawn)}   off here {YesNo(p.Snap.OffHere)}"),
            this.Section("Body"),
            this.Readout(() => $"Drop raw {p.Snap.RawDrop:F3}   smooth {p.Snap.SmoothDrop:F3}   applied {p.Snap.Applied:F3}"),
            this.Readout(() => $"Draw offset ours {p.Snap.OffsetWritten:F3}   heels {p.Snap.OffsetHooked:F3}   game holds {p.Snap.OffsetSeen:F3}"),
            this.Readout(() => $"Ground under body {p.Snap.BaseY:F3}   shift {Fmt(p.Snap.BodyShift)}"),
            this.Readout(() => $"Spine pitch {p.Snap.SpinePitchDeg:F1} deg   hips {p.Snap.HipFrac:F2}   character height {p.Snap.Height:F2}"),
            this.Section("Feet"),
            this.ColumnHeads(),
            this.Columns("ground hit", () => YesNo(p.Snap.Left.Hit), () => YesNo(p.Snap.Right.Hit)),
            this.Columns("ground Y", () => $"{p.Snap.Left.GroundModelY:F3}", () => $"{p.Snap.Right.GroundModelY:F3}"),
            this.Columns("ankle above ground", () => $"{p.Snap.Left.AnkleAboveGround:F3}", () => $"{p.Snap.Right.AnkleAboveGround:F3}"),
            this.Columns("rest (bind)", () => $"{p.Snap.Left.Rest:F3}", () => $"{p.Snap.Right.Rest:F3}"),
            this.Columns("can extend", () => $"{p.Snap.Left.MaxExtend:F3}", () => $"{p.Snap.Right.MaxExtend:F3}"),
            this.Columns("can raise", () => $"{p.Snap.Left.MaxRaiseByKnee:F3}", () => $"{p.Snap.Right.MaxRaiseByKnee:F3}"),
            this.Columns("contact", () => $"{p.Snap.Left.Contact:F2}", () => $"{p.Snap.Right.Contact:F2}"),
            this.Columns("yaw", () => $"{p.Snap.Left.YawDeg:F1} deg", () => $"{p.Snap.Right.YawDeg:F1} deg"),
            this.Columns("solved", () => YesNo(p.Snap.Left.Solved), () => YesNo(p.Snap.Right.Solved)),
            this.Columns("gather shift", () => Fmt(p.Snap.Left.GatherShift), () => Fmt(p.Snap.Right.GatherShift)),
            this.Columns("gather refused", () => p.Snap.Left.GatherBlock.ToString(), () => p.Snap.Right.GatherBlock.ToString()),
            this.Columns("wall push", () => Fmt(p.Snap.Left.WallShift), () => Fmt(p.Snap.Right.WallShift)),
            this.Columns("ankle (model)", () => Fmt(p.Snap.Left.AnkleModel), () => Fmt(p.Snap.Right.AnkleModel)),
            this.Columns("ground point", () => Fmt(p.Snap.Left.HitPoint), () => Fmt(p.Snap.Right.HitPoint)),
            this.Columns("ground normal", () => Fmt(p.Snap.Left.HitNormal), () => Fmt(p.Snap.Right.HitNormal)),
            this.Columns("material", () => $"0x{p.Snap.Left.Material:X}", () => $"0x{p.Snap.Right.Material:X}"),
        ];
    }

    private static bool Hooked(Plugin p) =>
        p.HookStatus.StartsWith("hooked", StringComparison.Ordinal) && p.MoveHookStatus.StartsWith("hooked", StringComparison.Ordinal) && p.SelfTest == "PASS";

    private static string Activity(in Snapshot s, Settings c)
    {
        var where = c.Where;
        if (where == Where.Off)
        {
            return "Switched off.";
        }

        if (!s.HasPose)
        {
            return "No character to work on.";
        }

        if (s.GPose && where == Where.InGame)
        {
            return "Paused in group pose.";
        }

        if (!s.GPose && where == Where.GroupPose)
        {
            return "Waiting for group pose.";
        }

        if (s.Conditions)
        {
            return "Paused: mounted, swimming, flying or loading.";
        }

        if (s.OffHere)
        {
            return s.InCutscene && !c.Cutscenes ? "Off in cutscenes."
                : s.WeaponDrawn && !c.WeaponDrawn ? "Off with the weapon drawn."
                : s.InDuty ? "Off in duties." : "Off outside duties.";
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

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string Fmt(Vector3 v) => $"{v.X:F2}, {v.Y:F2}, {v.Z:F2}";
}
