using System;
using System.Numerics;

namespace FootIk;

// One per tracked character, held in Plugin.states and keyed by the character's address. Plain mutable fields like
// Frame and Snapshot: the tick writes them and nothing else reads them.
internal sealed class CharState
{
    public ulong Id;               // the spawn this state belongs to; the allocator reuses object addresses
    public bool Seen;              // found in the object table this frame
    public bool Fresh = true;      // no previous frame to measure speed against
    public LegChain Chain;
    public float Blend;            // 0..1
    public float SmoothDrop;       // model units, negative = down
    public float Written;          // the draw offset Y we hold, not the game's total
    public Vector3 PelvisForMove;  // world offset added to the draw object in the movement hook
    public Vector3 MoveWritten;    // the part of it the draw object currently carries
    public Vector3 LastLogical;
    public readonly Vector3[] GatherShift = new Vector3[2]; // model space
    public readonly Vector3[] WallShift = new Vector3[2];   // model space
    public readonly Vector3[] LatchTarget = new Vector3[2]; // world space
    public readonly bool[] Latched = new bool[2];
    public Vector3 BodyShift;      // model space
    public float Lean;             // radians
    public Quaternion Tilt = Quaternion.Identity;

    // Nothing of ours is left on the character: the offsets are off and the pose is the animation's own again.
    public bool Idle => this.Blend == 0f && this.Written == 0f && this.PelvisForMove == Vector3.Zero && this.MoveWritten == Vector3.Zero;

    public void ClearGather()
    {
        Array.Clear(this.GatherShift);
        Array.Clear(this.WallShift);
        Array.Clear(this.Latched);
    }
}
