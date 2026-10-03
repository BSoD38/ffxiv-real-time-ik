using System.Collections.Generic;
using KamiToolKit.BaseTypes;

namespace FootIk;

internal sealed partial class Overlay
{
    private static readonly string[] EdgesFields =
    [
        nameof(Settings.GatherFeet), nameof(Settings.GatherToPosition), nameof(Settings.MinStanceFrac), nameof(Settings.GatherStraighten),
        nameof(Settings.GatherForward), nameof(Settings.GatherUpright), nameof(Settings.GatherPrecision), nameof(Settings.GatherTauIn), nameof(Settings.GatherTauOut),
        nameof(Settings.MaxStanceFrac), nameof(Settings.MaxBodyShiftFrac),
    ];

    private List<NodeBase> BuildEdges()
    {
        var c = this.Owner.Settings;
        return
        [
            this.Check("Gather feet", () => c.GatherFeet, v => c.GatherFeet = v,
                "Move your character's feet together when standing on narrow platforms to avoid them floating over an edge."),
            this.Check("Platforming mode", () => c.GatherToPosition, v => c.GatherToPosition = v,
                "Enabling this prioritises feet positions that avoid sliding your character off-centre. This helps better seeing where your character really is, at the cost of worse poses.",
                () => c.GatherFeet),
            this.SlideInt("Precision", () => c.GatherPrecision, v => c.GatherPrecision = v, 1, 4, v => $"{v}  ({4 * v} directions)",
                "How carefully the plugin looks around each foot for ground to stand on. Higher finds narrow rails and beams more reliably and keeps the feet steadier, but costs more each frame. Lower it if the game slows down near edges.",
                () => c.GatherFeet),
            this.Fold("Advanced", () =>
            [
                this.Slide("Min stance", () => c.MinStanceFrac, v => c.MinStanceFrac = v, 0.02f, 0.4f, 100f, v => $"{v:F2}  ({this.Metres(v)} m)",
                    "How close the feet can be of each other when gathering. Avoids the feet crossing or overlapping each other.", () => c.GatherFeet),
                this.Slide("Max stance", () => c.MaxStanceFrac, v => c.MaxStanceFrac = v, 0.5f, 4f, 100f, v => $"{v:F2}  ({this.Metres(v)} m)",
                    "How far apart the feet may end up when gathering. Wider than this and the plugin leaves the feet alone for that frame, so a bad reading never splays the legs.", () => c.GatherFeet),
                this.Slide("Max body move", () => c.MaxBodyShiftFrac, v => c.MaxBodyShiftFrac = v, 0.25f, 3f, 100f, v => $"{v:F2}  ({this.Metres(v)} m)",
                    "How far gathering may move your character's body onto the feet. Further than this and the plugin leaves the feet alone for that frame, so a bad reading never throws your character across the room.", () => c.GatherFeet),
                this.Slide("Straighten legs", () => c.GatherStraighten, v => c.GatherStraighten = v, 0f, 1f, 100f, v => $"{v:F2}",
                    "How much the legs straighten when the feet are gathered. Avoids legs being flexed while gathered on some races.", () => c.GatherFeet),
                this.Slide("Straighten back", () => c.GatherUpright, v => c.GatherUpright = v, 0f, 1f, 100f, v => $"{v:F2}",
                    "How much a hunched back straightens when the feet are gathered. Avoids stooping over narrow supports on some races.", () => c.GatherFeet),
                this.Slide("Feet forward", () => c.GatherForward, v => c.GatherForward = v, 0f, 1f, 100f, v => $"{v:F2}",
                    "How strongly the knees and feet turn to face forward when gathered. Avoids duck feet on some races.", () => c.GatherFeet),
                this.Slide("Ease in", () => c.GatherTauIn, v => c.GatherTauIn = v, 0.05f, 1f, 100f, v => $"{v:F2} s",
                    "How quickly the feet move in onto a support.", () => c.GatherFeet),
                this.Slide("Ease out", () => c.GatherTauOut, v => c.GatherTauOut = v, 0.05f, 1f, 100f, v => $"{v:F2} s",
                    "How quickly they return when stepping off.", () => c.GatherFeet),
            ]),
        ];
    }
}
