using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace FootIk;

// Why a gather target was refused, for the status readout.
public enum Block
{
    None,
    Off, // the step did not run at all: switched off, the gate closed, or an emote
    Settled, // it ran; this foot stands on its own ground, or on an incline where a foot below level is expected
    NoEdge, // the search found no standable ground to move onto
    Squeezed, // no two spots a stance apart: the feet stand closer than the minimum
}

// Written once per tick, read on the draw thread.
public struct FootSnapshot
{
    public Block GatherBlock;
    public Vector3 AnkleModel;
    public Vector3 AnkleWorld;
    public Vector3 ToeWorld;
    public float Rest;
    public bool Hit;
    public Vector3 HitPoint;
    public Vector3 HitNormal;
    public ulong Material;
    public float GroundModelY;
    public float AnkleAboveGround;
    public float Delta;
    public bool OverEdge;
    public bool BelowLevel; // ground a step or more below the level the character stands on
    public bool Gathered;
    public Vector3 GatherShift;
    public Vector3 WallShift;
    public float Planted;
    public float MaxExtend;
    public float MaxRaiseByKnee;
    public float Offset;
    public float Contact;
    public float TiltDeg;
    public float YawDeg;
    public bool Solved;
    public Vector3 IkTargetWorld;
}

public struct Snapshot
{
    public bool HasPose;
    public bool ChainResolved;
    public bool Gate;
    public bool IsJumping;
    public bool Conditions;
    public bool GPose;
    public CharacterModes Mode;
    public byte ModeParam;
    public float Blend;
    public float Speed;
    public float LeanDeg;
    public float SpinePitchDeg;
    public bool OnFloor;
    public float SitTiltDeg;
    public float HipFrac;
    public bool ArmsResolved;
    public float LeftHandY, RightHandY;    // wrist height over the ground under it, model units; NaN when nothing is under it
    public float LegLength;
    public float BaseY;
    public float RawDrop;
    public float SmoothDrop;
    public float Applied;
    public float OffsetSeen;
    public float OffsetWritten;
    public Vector3 BodyShift;
    public FootSnapshot Left;
    public FootSnapshot Right;
}
