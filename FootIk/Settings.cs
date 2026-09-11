using System.Reflection;
using Dalamud.Configuration;

namespace FootIk;

// Fields ending in Frac are fractions of the bind-pose leg length, so races of every size behave alike.
// Saved by Dalamud as JSON. These are fields rather than properties because ImGui takes each one by reference.
public sealed class Settings : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled = true;
    public bool ShowMarkers;
    public float BlendSeconds = 0.15f;
    public float PelvisTau = 0.08f;

    public float LegBalance = 0.7f; // 0 leaves the body where the animation puts it, 1 splits a height difference evenly
    public float MaxDropFrac = 0.40f;
    public float MaxRaiseFrac = 0.35f;
    public float MaxStepFrac = 0.60f;
    public float LiftThresholdFrac = 0.18f;
    public float RayUpFrac = 0.6f;
    public float RayDownFrac = 1.2f;
    public float StraightenLimit = 0.98f;
    public float MaxKneeBendDeg = 110f; // measured from a straight leg, so higher allows a deeper crouch
    public float RestAdjustFrac;
    public float SlopeLiftFrac; // ponytail: interim workaround for the slope sink, see PLAN.md M5

    public float MaxAnkleAngleDeg = 30f;
    public float TiltFadeFrac = 0.05f;

    public float MaxStepDownFrac = 0.25f;
    public bool GatherFeet;
    public float GatherTauIn = 0.1f;
    public float GatherTauOut = 0.1f;
    public float StillSpeed = 0.15f; // m/s
    public int GatherPrecision = 2; // search directions = 4x this
    public bool KeepFeetOutOfWalls = true;
    public float WallClearanceFrac = 0.06f; // half a foot's width: the box a foot may not stand inside a wall with
    public float MinStanceFrac = 0.10f;
    public float GatherStraighten = 0.7f;
    public float GatherForward = 0.7f;
    public float MaxPelvisRaiseFrac = 0.3f;

    public bool SlopeLean;
    public float LeanUphillGain = 0.5f;
    public float LeanDownhillGain = 1.0f; // hunched races (Hrothgar) want a weak uphill lean but a strong downhill one
    public float MaxLeanDeg = 30f;
    public float MaxTotalPitchDeg = 60f; // the Hrothgar run animation is already pitched ~40 forward
    public float MinTotalPitchDeg = -5f; // the spine may lean back a little past upright, never further
    public float LeanTau = 0.3f;

    public bool Emotes = true;
    public float MaxSitTiltDeg = 20f;
    public float SitTiltTau = 0.4f;

    // A truncated or hand-edited file can deserialise a NaN, and NaN spreads through the pose maths silently: every
    // comparison against it is false, so the guards downstream let it through as "not too far" rather than catching it.
    // Reflection so a field added later is covered without anyone remembering to come back here.
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
