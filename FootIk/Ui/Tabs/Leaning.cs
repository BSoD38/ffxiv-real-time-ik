using System.Collections.Generic;
using KamiToolKit.BaseTypes;

namespace FootIk;

internal sealed partial class Overlay
{
    private static readonly string[] LeanFields =
    [
        nameof(Settings.SlopeLean), nameof(Settings.LeanUphillGain), nameof(Settings.LeanDownhillGain), nameof(Settings.MaxLeanDeg),
        nameof(Settings.MaxTotalPitchDeg), nameof(Settings.MinTotalPitchDeg), nameof(Settings.LeanTau),
    ];

    private List<NodeBase> BuildLean()
    {
        var c = this.Owner.Settings;
        return
        [
            this.Check("Lean into slopes", () => c.SlopeLean, v => c.SlopeLean = v,
                "While moving, makes the character lean forwards or backwards when running uphill and downhill."),
            this.Note("Balance against the slope while running."),
            this.Slide("Uphill gain", () => c.LeanUphillGain, v => c.LeanUphillGain = v, 0f, 2f, 100f, v => $"{v:F2}",
                "How strongly the character leans forwards when running uphill, at full run speed.", () => c.SlopeLean),
            this.Slide("Downhill gain", () => c.LeanDownhillGain, v => c.LeanDownhillGain = v, 0f, 2f, 100f, v => $"{v:F2}",
                "How far the character leans back when running downhill.", () => c.SlopeLean),
            this.Slide("Max lean", () => c.MaxLeanDeg, v => c.MaxLeanDeg = v, 0f, 45f, 1f, v => $"{v:F0} deg",
                "How far the character can lean in either direction.", () => c.SlopeLean),
            this.Fold("Advanced", () =>
            [
                this.Slide("Max spine pitch", () => c.MaxTotalPitchDeg, v => c.MaxTotalPitchDeg = v, 10f, 90f, 1f, v => $"{v:F0} deg",
                    "Limits how much the character may lean while taking account their base animations. Avoids hunched races lean forwards too much."),
                this.Slide("Min spine pitch", () => c.MinTotalPitchDeg, v => c.MinTotalPitchDeg = v, -45f, 0f, 1f, v => $"{v:F0} deg",
                    "Limits how far back the character may lean, taking account of their base animation. Avoids upright races arching backwards when running downhill."),
                this.Slide("Lean smoothing", () => c.LeanTau, v => c.LeanTau = v, 0.05f, 1f, 100f, v => $"{v:F2} s",
                    "Smoothing on the lean angle, so a sudden change in terrain does not snap the torso."),
            ]),
        ];
    }
}
