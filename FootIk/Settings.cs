using System;
using System.ComponentModel;
using System.Reflection;
using Dalamud.Configuration;

namespace FootIk;

// Descriptions are the dropdown labels.
public enum Where
{
    [Description("Off")] Off,
    [Description("In game")] InGame,
    [Description("In group pose")] GroupPose,
    [Description("In game and group pose")] Both,
}

// Who the mod works on besides you, widest first. Saved as a number, so a new choice goes at the end.
public enum Who
{
    [Description("All players and NPCs")] Everyone,
    [Description("All players")] Players,
    [Description("Friends and party")] FriendsAndParty,
    [Description("Party only")] Party,
    [Description("Friends only")] Friends,
}

// Fields ending in Frac are fractions of the bind-pose leg length, so races of every size behave alike.
public sealed class Settings : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public Where Where = Where.InGame; // group pose off by default: posing tools move the same bones
    public bool OpenWorld = true;
    public bool Duties = true;
    public bool WeaponDrawn = true;
    public bool Cutscenes;
    public bool ShowMarkers;
    public bool ShowRuler;
    public float BlendSeconds = 0.15f;
    public float PelvisTau = 0.08f;
    public float StrideTau = 0.15f; // 0 keeps the standing rule at every speed
    public float FootTau = 0.03f; // also sets how far ahead a moving foot looks for a step; 0 turns both off

    public float MaxDropFrac = 0.40f;
    public float MaxRaiseFrac = 0.35f;
    public float MaxStepFrac = 0.60f;
    public float LiftThresholdFrac = 0.18f;
    public float RayUpFrac = 0.6f;
    public float RayDownFrac = 1.2f;
    public float StraightenLimit = 0.98f;
    public float MaxKneeBendDeg = 110f; // measured from a straight leg, so higher allows a deeper crouch
    public float RestAdjustFrac;
    public float SlopeLiftFrac; // stopgap: on natural slopes the feet land short of the visible ground, so add raise by slope tangent

    public float MaxAnkleAngleDeg = 30f;
    public float TiltFadeFrac = 0.05f;

    public float MaxStepDownFrac = 0.25f;
    public bool GatherFeet;
    public bool GatherToPosition; // gather onto the logical position instead of the nearest support, and leave the body on it
    public float GatherTauIn = 0.1f;
    public float GatherTauOut = 0.1f;
    public int GatherPrecision = 2; // search directions = 4x this
    public int OthersGatherPrecision; // the same for other characters; 0 leaves their feet where the animation puts them
    public bool KeepFeetOutOfWalls = true;
    public float WallClearanceFrac = 0.06f; // half a foot's width: the box a foot may not stand inside a wall with
    public float MinStanceFrac = 0.10f;
    public float MaxStanceFrac = 2f;    // feet targets further apart than this are given up on
    public float MaxBodyShiftFrac = 1f; // a gathered stance that would carry the body further than this is given up on
    public float GatherStraighten = 0.7f;
    public float GatherForward = 0.7f;
    public float GatherUpright; // share of the spine's forward pitch taken out while gathered
    public float MaxPelvisRaiseFrac = 0.3f;

    public bool SlopeLean;
    public float LeanUphillGain = 0.5f;
    public float LeanDownhillGain = 1.0f; // hunched races (Hrothgar) want a weak uphill lean but a strong downhill one
    public float MaxLeanDeg = 30f;
    public float MaxTotalPitchDeg = 60f; // the Hrothgar run animation is already pitched ~40 forward
    public float MinTotalPitchDeg = -5f; // the spine may lean back a little past upright, never further
    public float LeanTau = 0.3f;

    public bool Bump;
    public bool Shoved = true;
    public bool BumpNpcs;
    public bool Grunt;
    public bool ShoveSound;              // sounds/shove.scd; needs Penumbra
    public float ShoveVolume = 1f;
    public float BumpRadiusFrac = 0.45f; // half the centre distance at which two bodies touch
    public float BumpMinSpeed = 5f;      // m/s closing speed, a world speed like StillSpeed
    public float BumpMaxDeg = 25f;
    public float BumpRiseSeconds = 0.08f;
    public float BumpTau = 0.5f;
    public float BumpBodyTurn = 1f;
    public float BumpShoveFrac = 0.1f;
    public float BumpHeadHold = 0.4f;
    public float BumpCooldown = 0.6f;     // s between any two bumps
    public float BumpSameCooldown = 0.9f; // s before the same character counts again

    public bool Emotes = true;
    public float MaxSitTiltDeg = 20f;
    public bool FloorTilt = true;
    public float SitTiltTau = 0.4f;

    public bool Others;
    public int MaxOthers = 4;
    public Who Who;
    public float OthersRadius = 15f; // yalms from the local player, not a body-relative distance

    public bool MeshRefine; // experimental: the ground height read off the visible level geometry, gated by collision
    public float MeshRadius = 30f; // yalms, a world distance like OthersRadius
    public float MeshBand = 0.35f; // metres, not a Frac: a property of the level

    public bool WorksIn(bool groupPose) => groupPose ? this.Where is Where.GroupPose or Where.Both : this.Where is Where.InGame or Where.Both;

    // Every context the character is in has to be ticked.
    public bool WorksHere(bool duty, bool weaponDrawn, bool cutscene) =>
        (duty ? this.Duties : this.OpenWorld) && (this.WeaponDrawn || !weaponDrawn) && (this.Cutscenes || !cutscene);

    // Puts fields back to how they shipped; null resets every one of them.
    public void Reset(string[]? fields = null) => this.Restore(f => fields is null || Array.IndexOf(fields, f.Name) >= 0);

    // A NaN from a hand-edited file passes every < / > guard downstream.
    public void Repair() => this.Restore(f => f.FieldType == typeof(float) && !float.IsFinite((float)f.GetValue(this)!));

    // Reflection so a field added later is covered without being listed anywhere.
    private void Restore(Func<FieldInfo, bool> wanted)
    {
        var defaults = new Settings();
        foreach (var field in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (wanted(field))
            {
                field.SetValue(this, field.GetValue(defaults));
            }
        }
    }
}
