using System.Collections.Generic;
using KamiToolKit.BaseTypes;

namespace FootIk;

internal sealed partial class Overlay
{
    private static readonly string[] ShovingFields =
    [
        nameof(Settings.Bump), nameof(Settings.Shoved), nameof(Settings.BumpNpcs), nameof(Settings.Grunt), nameof(Settings.ShoveSound),
        nameof(Settings.ShoveVolume), nameof(Settings.BumpCooldown),
        nameof(Settings.BumpSameCooldown), nameof(Settings.BumpRadiusFrac), nameof(Settings.BumpMaxDeg), nameof(Settings.BumpShoveFrac),
        nameof(Settings.BumpHeadHold), nameof(Settings.BumpBodyTurn), nameof(Settings.BumpMinSpeed), nameof(Settings.BumpRiseSeconds),
        nameof(Settings.BumpTau),
    ];

    private List<NodeBase> BuildShoving()
    {
        var c = this.Owner.Settings;
        return
        [
            this.Check("Enable shoving", () => c.Bump, v => c.Bump = v,
                "Allows you to shove people when running into them. The shove depends on the speed and angle of impact, the difference in height of both people, and other factors."),
            this.Check("Shove NPCs", () => c.BumpNpcs, v => c.BumpNpcs = v,
                "Whether NPCs count. Off, only players can bump you or be bumped. NPCs might not react to shoving.", () => c.Bump),
            this.Check("Let others shove you", () => c.Shoved, v => c.Shoved = v,
                "Other players running or walking into you shove you as well.", () => c.Bump),
            this.Note("They flinch back only when they have IK applied: see \"Apply on other characters\" in the performance settings."),
            this.Check("Grunt when shoved", () => c.Grunt, v => c.Grunt = v,
                "Make both characters grunt when they one or the other is shoved. Has a random chance of playing a grunt.", () => c.Bump),
            this.Check("Impact sound", () => c.ShoveSound, v => c.ShoveSound = v,
                "Plays a soft thud where the two of you collide. Needs Penumbra installed.", () => c.Bump),
            this.Slide("Impact volume", () => c.ShoveVolume, v => c.ShoveVolume = v, 0f, 1f, 100f, v => $"{v * 100f:F0}%",
                "How loud the thud is. The game's Sound Effects volume applies on top.", () => c.Bump && c.ShoveSound),
            this.Section("Cooldowns"),
            this.Slide("Bump cooldown", () => c.BumpCooldown, v => c.BumpCooldown = v, 0f, 3f, 10f, v => $"{v:F1} s",
                "The least time between two bumps, whoever they are with. Raise it if you don't like continusously shoving crowds.", () => c.Bump),
            this.Slide("Same character", () => c.BumpSameCooldown, v => c.BumpSameCooldown = v, 0f, 10f, 10f, v => $"{v:F1} s",
                "How long before running into the same character again counts as a new shove.", () => c.Bump),
            this.Fold("Advanced", () =>
            [
                this.Slide("Bump distance", () => c.BumpRadiusFrac, v => c.BumpRadiusFrac = v, 0.1f, 0.8f, 100f, v => $"{v:F2}  ({this.Metres(v * 2f)} m apart)",
                    "How close two characters must come to count as touching. Too low and you pass through people without a reaction; too high and you shove people you clearly missed.", () => c.Bump),
                this.Slide("Bump strength", () => c.BumpMaxDeg, v => c.BumpMaxDeg = v, 0f, 60f, 1f, v => $"{v:F0} deg",
                    "How far the upper body tips away and turns towards the other character at the moment of impact.", () => c.Bump),
                this.Slide("Give way", () => c.BumpShoveFrac, v => c.BumpShoveFrac = v, 0f, 0.5f, 100f, v => $"{v:F2}  ({v * this.Owner.Snap.LegLength * 100f:F0} cm)",
                    "How far the hips are pushed off the feet, which is what bends the knees and makes a standing character look shoved rather than folded at the waist. Too high and they sink into a crouch.", () => c.Bump),
                this.Slide("Keep looking ahead", () => c.BumpHeadHold, v => c.BumpHeadHold = v, 0f, 1f, 100f, v => $"{v:F2}",
                    "How much the neck holds the head still while the body is shoved out from under it. Full keeps the character looking where it was looking; zero lets the head ride round with the shoulders.", () => c.Bump),
                this.Slide("Whole-body turn", () => c.BumpBodyTurn, v => c.BumpBodyTurn = v, 0f, 1.5f, 100f, v => $"{v:F2}",
                    "How much of the turn the whole body takes at running speed, hips and all, on top of the shoulders; the legs keep following the stride. Zero keeps every bump in the upper body; standing characters always do.", () => c.Bump),
                this.Slide("Bump speed", () => c.BumpMinSpeed, v => c.BumpMinSpeed = v, 0.2f, 10f, 10f, v => $"{v:F1} m/s",
                    "How fast the two of you must be closing on each other for it to count as a bump. Set it above walking speed and only running bumps; set it low and brushing past someone jolts you.", () => c.Bump),
                this.Slide("Bump rise", () => c.BumpRiseSeconds, v => c.BumpRiseSeconds = v, 0.02f, 0.3f, 100f, v => $"{v:F2} s",
                    "How quickly the body reaches its maximum rotation angle after the impact.", () => c.Bump),
                this.Slide("Bump recovery", () => c.BumpTau, v => c.BumpTau = v, 0.1f, 1.5f, 100f, v => $"{v:F2} s",
                    "How long the body takes to straighten up again.", () => c.Bump),
            ]),
        ];
    }
}
