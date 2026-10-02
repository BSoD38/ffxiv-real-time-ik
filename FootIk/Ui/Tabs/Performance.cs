using System.Collections.Generic;
using KamiToolKit.BaseTypes;

namespace FootIk;

internal sealed partial class Overlay
{
    private static readonly string[] PerformanceFields =
    [
        nameof(Settings.Others), nameof(Settings.MaxOthers), nameof(Settings.OthersRadius), nameof(Settings.Who),
        nameof(Settings.OthersGatherPrecision), nameof(Settings.MeshRefine), nameof(Settings.MeshRadius), nameof(Settings.MeshBand),
    ];

    private List<NodeBase> BuildPerformance()
    {
        var c = this.Owner.Settings;
        var p = this.Owner;
        return
        [
            this.Check("Apply on other characters", () => c.Others, v => c.Others = v,
                "Applies IK to other players and NPCs around you. Each one costs FPS. Tweak the settings if the game stutters too much."),
            this.SlideInt("Max characters", () => c.MaxOthers, v => c.MaxOthers = v, 1, 50, v => $"{v}",
                "How many characters to apply IK to. Lower this if your frame rate drops in crowds.", () => c.Others),
            this.Slide("Max distance", () => c.OthersRadius, v => c.OthersRadius = v, 3f, 50f, 1f, v => $"{v:F0} yalms",
                "How far away from the camera IK is applied.", () => c.Others),
            this.Pick("Apply IK to", () => c.Who, v => c.Who = v,
                "Which characters around you have IK applied. Party includes your alliance.", () => c.Others),
            this.SlideInt("Gather feet precision", () => c.OthersGatherPrecision, v => c.OthersGatherPrecision = v, 0, 4,
                v => v == 0 ? "off" : $"{v}  ({4 * v} directions)",
                "Whether other characters also get their feet gathered onto narrow ledges and rails, and how carefully. Needs Gather feet on the Edges tab.",
                () => c.Others && c.GatherFeet),
            this.Check("Use higher precision collision", () => c.MeshRefine, v => c.MeshRefine = v,
                "Places feet on the ground you actually see instead of the simplified shape the game uses for collision. Fixes stairs that otherwise behave like a ramp. Uses some memory, and a little frame time while loading a new area."),
            this.Fold("Advanced", () =>
            [
                this.Slide("Read distance", () => c.MeshRadius, v => c.MeshRadius = v, 10f, 60f, 1f, v => $"{v:F0} yalms",
                    "How far around you the visible ground is read. Keep it at least as far as Max distance above, or far-away characters fall back to the simple shape.", () => c.MeshRefine),
                this.Slide("Max difference", () => c.MeshBand, v => c.MeshBand = v, 0.05f, 1f, 100f, v => $"{v:F2} m",
                    "How far the visible ground may be from the simple shape and still be believed. Too low and stairs are missed; too high and a foot may land on furniture.", () => c.MeshRefine),
            ]),
            // The scan switches its own setting off when it faults, so the reason it did has to be somewhere.
            this.Readout(() => p.MeshStatus == "off" ? string.Empty : $"Higher precision collision: {p.MeshStatus}"),
            this.Readout(() => $"Working on {p.Tracked} character{(p.Tracked == 1 ? string.Empty : "s")}."),
            this.Readout(() => $"Frame cost {p.LastMicros:F0} us."),
        ];
    }
}
