using System.Collections.Generic;
using KamiToolKit.BaseTypes;

namespace FootIk;

internal sealed partial class Overlay
{
    private static readonly string[] EmotesFields =
    [
        nameof(Settings.Emotes), nameof(Settings.FloorTilt), nameof(Settings.MaxSitTiltDeg), nameof(Settings.SitTiltTau),
    ];

    private List<NodeBase> BuildEmotes()
    {
        var c = this.Owner.Settings;
        return
        [
            this.Check("Work during emotes", () => c.Emotes, v => c.Emotes = v,
                "Keeps placing the feet through dances and other looping emotes, and settles the body onto the slope when sitting."),
            this.Check("Tilt lying-down poses too", () => c.FloorTilt, v => c.FloorTilt = v,
                "Turns the body to follow the slope when lying on the ground. Affects emotes like pushups and playing dead.",
                () => c.Emotes),
            this.Slide("Max tilt angle", () => c.MaxSitTiltDeg, v => c.MaxSitTiltDeg = v, 0f, 45f, 1f, v => $"{v:F0} deg",
                "How far the body may tilt to rest on sloped ground when sitting or sleeping on it. Zero keeps the body upright.", () => c.Emotes),
            this.Slide("Tilt smoothing speed", () => c.SitTiltTau, v => c.SitTiltTau = v, 0.05f, 1.5f, 100f, v => $"{v:F2} s",
                "How quickly the body settles onto the slope when sitting down, and comes back up when standing.", () => c.Emotes),
        ];
    }
}
