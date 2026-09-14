using System;
using System.Reflection;
using Dalamud.Configuration;

namespace FootIk;

// Who the mod works on besides you, widest first.
public enum Who
{
    Everyone,
    Players,
    FriendsAndParty,
    Party,
}

// Fields ending in Frac are fractions of the bind-pose leg length, so races of every size behave alike.
// Saved by Dalamud as JSON. These are fields rather than properties because ImGui takes each one by reference.
public sealed class Settings : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled = true;
    public bool ShowMarkers;
    public bool ShowRuler;
    public float BlendSeconds = 0.15f;
    public float PelvisTau = 0.08f;

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
    public float GatherStraighten = 0.7f;
    public float GatherForward = 0.7f;
    public float MaxPelvisRaiseFrac = 0.3f;

    public bool MoveWeapons = true; // carried weapons follow the bones we turn, instead of staying where the animation left them
    public bool SlopeLean;
    public float LeanUphillGain = 0.5f;
    public float LeanDownhillGain = 1.0f; // hunched races (Hrothgar) want a weak uphill lean but a strong downhill one
    public float MaxLeanDeg = 30f;
    public float MaxTotalPitchDeg = 60f; // the Hrothgar run animation is already pitched ~40 forward
    public float MinTotalPitchDeg = -5f; // the spine may lean back a little past upright, never further
    public float LeanTau = 0.3f;

    public bool Bump;
    public bool Shoved = true;           // someone running into you counts, not only you running into them
    public bool Grunt;                   // both bodies play their own damage-taken voice line on a bump
    public float BumpRadiusFrac = 0.45f; // half the centre distance at which two bodies touch, in leg lengths
    public float BumpMinSpeed = 5f;      // m/s closing on them, a world speed like StillSpeed
    public float BumpMaxDeg = 25f;
    public float BumpRiseSeconds = 0.08f;
    public float BumpTau = 0.5f;
    public float BumpBodyTurn = 1f;       // share of the turn the whole body takes at running speed, feet included
    public float BumpShoveFrac = 0.1f;    // how far the hips are carried off the planted feet, in leg lengths
    public float BumpHeadHold = 0.4f;     // how much of the spine turn and body yaw the neck undoes, so the head holds still
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
    public float MeshRadius = 30f; // yalms around the local player that are read, a world distance like OthersRadius
    public float MeshBand = 0.35f; // metres the visible ground may differ from collision and still be believed: about a tall stair tread, a property of the level rather than the character

    // Puts fields back to how they shipped; null resets every one of them.
    public void Reset(string[]? fields = null)
    {
        var defaults = new Settings();
        foreach (var field in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (fields is null || Array.IndexOf(fields, field.Name) >= 0)
            {
                field.SetValue(this, field.GetValue(defaults));
            }
        }
    }

    // A truncated or hand-edited file can deserialise a NaN, and every comparison against NaN is false, so the guards
    // downstream let it through as "not too far". Reflection so a field added later is covered.
    public void Repair()
    {
        var defaults = new Settings();
        foreach (var field in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.FieldType == typeof(float) && !float.IsFinite((float)field.GetValue(this)!))
            {
                field.SetValue(this, field.GetValue(defaults));
            }
        }
    }
}
