using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;

namespace FootIk;

// Model-space heights are relative to the character origin, except during an emote, where step thresholds and the probe
// window are judged from the ground under the body instead (Frame.BaseY): root motion can carry it far from the origin.
public sealed unsafe partial class Plugin
{
    private ref struct Frame
    {
        public float Dt;
        public float LegLen, MaxDrop, MaxRaise, MaxStep, MaxStepDown, LiftThreshold, StepTol;
        public Vector3 Pos, Scale, PosAnchor;
        public Quaternion Rot;
        public float OriginY, RayUp, RayDown, MaxDist, Reach;
        public float BaseY, ProbeTop;
        public float Speed;
        public Vector3 VelDir;
        public bool Still;
        public int Dirs; // gather search directions, 0 when this character does not gather at all
        public float Yaw;
        public hkQsTransformf* Bones;
        public hkaPose* Pose;
        public Skeleton* Skel;
        public CharState St;
        public Span<WeaponAnchor> Weapons;
    }

    // What the planted feet ask of the body height, and the range their legs can still work in.
    private struct BodyRange
    {
        public float Lo;
        public float Hi;
        public float WantSum;
        public float WantWeight;
        public float GatherWant;
    }

    private const float StillSpeed = 0.15f; // m/s, not a *Frac: every race moves at the same world speed.
    private const float RunSpeed = 6f;      // m/s, a flat-out run on foot
    private const float WarpSpeed = 30f;    // m/s: faster than any mount, so a step this large is a teleport, not travel

    // Where a body near us stood when we last looked. Being run into needs the other one's speed, and the game keeps
    // none we can read - MoveController carries only MovementState - so it is measured here rather than taken from a
    // CharState, which only characters the plugin is already working on have.
    private struct Neighbour
    {
        public ulong Id;
        public Vector3 Pos;
        public double Seen;
    }

    private readonly Dictionary<nint, Neighbour> neighbours = [];

    // Hip height over leg length: below FloorHips the character is on the floor rather than on its feet, and above
    // StandingHips it is clearly back on them. The Status tab reads it out; an idle pose measured over 0.85 in game.
    private const float FloorHips = 0.4f;
    private const float StandingHips = 0.85f;

    // How much of a sideways shove leans the torso over; the rest of it turns the torso instead.
    private const float BumpSideLean = 0.35f;

    // Shoulder height over leg length, for a body whose arms did not resolve and for one nobody is ticking. Measured
    // off a standing midlander; every race is within a few centimetres of it once scaled by the leg.
    private const float ShoulderOverLeg = 1.45f;

    // What runs into someone is the shoulder line, not the point on the floor between the feet: it leads by most of a
    // foot when the body leans into a run, and swings across when it turns. Falls back to a height up the origin for a
    // skeleton with no arms (a carbuncle-shaped model can reach this).
    private static Vector3 ShoulderMid(in Frame f)
    {
        ref readonly var ch = ref f.St.Chain;
        if (!ch.LeftArm.Resolved || !ch.RightArm.Resolved)
        {
            return f.PosAnchor with { Y = f.PosAnchor.Y + (ShoulderOverLeg * f.LegLen * f.Scale.Y) };
        }

        var mid = (Bones.Pos(in f.Bones[ch.LeftArm.Hip]) + Bones.Pos(in f.Bones[ch.RightArm.Hip])) * 0.5f;
        return f.PosAnchor + Vector3.Transform(f.Scale * mid, f.Rot);
    }

    // retire closes the gate whatever the character is doing, so the blend carries our offsets back off it.
    private void TickOne(Character* chr, CharState st, float dt, double rawDt, bool local, bool retire, ref Snapshot snap)
    {
        var c = this.Settings;
        var mode = chr->Mode;
        snap.Mode = mode;
        snap.ModeParam = chr->ModeParam;
        snap.Height = chr->Height * chr->Scale;
        snap.IsJumping = chr->IsJumping();
        snap.GPose = ClientState.IsGPosing;
        // Group pose may raise the cutscene condition too, so it is not trusted while posing on purpose.
        var on = c.WorksIn(snap.GPose);
        snap.Conditions = (!(on && snap.GPose) && Condition.Any(this.globalFlags)) || (local && Condition.Any(this.gateFlags));
        // MovementState is the per-character reading of what ICondition tells us about ourselves: flying and diving.
        var clear = on && !retire && !snap.Conditions && chr->MoveController.MovementState == MovementStateOptions.Normal;
        // InPositionLoop carries the EmoteMode row in ModeParam: 1 ground sit, 2 chair, 3 sleep. A chair is furniture,
        // not ground. Every other looping emote is EmoteLoop, a dance and /pushups alike, so the pose decides: latched
        // low, released high, because /pushups crosses any single threshold every rep. HipFrac is last frame's.
        if (mode == CharacterModes.EmoteLoop && st.HipFrac < FloorHips)
        {
            st.FloorLoop = true;
        }
        else if (st.HipFrac >= StandingHips)
        {
            st.FloorLoop = false;
        }

        snap.OnFloor = clear && c.Emotes && ((mode == CharacterModes.InPositionLoop && chr->ModeParam is 1 or 3) || st.FloorLoop);
        snap.Gate = clear && !snap.IsJumping && !snap.OnFloor && (mode == CharacterModes.Normal || (c.Emotes && mode == CharacterModes.EmoteLoop));

        // A zero in the skeleton scale turns every model-space conversion into infinity, and SmoothDrop keeps a NaN forever.
        var poseOk = ResolvePose(chr, ref st.Chain, ref snap, out var pose, out var skel)
            && skel->Transform.Scale.X > 1e-4f && skel->Transform.Scale.Y > 1e-4f && skel->Transform.Scale.Z > 1e-4f;
        Span<WeaponAnchor> anchors = stackalloc WeaponAnchor[4];
        scoped var f = new Frame { St = st, Dt = dt, Pose = pose, Skel = skel };
        f.LegLen = poseOk ? st.Chain.LegLength : 0f;
        f.MaxDrop = c.MaxDropFrac * f.LegLen;
        f.MaxRaise = c.MaxRaiseFrac * f.LegLen;
        f.MaxStep = c.MaxStepFrac * f.LegLen;
        f.MaxStepDown = c.MaxStepDownFrac * f.LegLen;
        f.StepTol = 0.15f * f.LegLen; // a stair tread, not a curb or a rail
        f.Dirs = 4 * Math.Clamp(local ? c.GatherPrecision : c.OthersGatherPrecision, 0, MaxDirs / 4);
        f.LiftThreshold = MathF.Max(c.LiftThresholdFrac * f.LegLen, 1e-3f);
        snap.LegLength = f.LegLen;
        st.Blend = Math.Clamp(st.Blend + ((snap.Gate && poseOk ? 1f : -1f) * (c.BlendSeconds > 0 ? dt / c.BlendSeconds : 1f)), 0f, 1f);
        snap.Blend = st.Blend;
        var active = poseOk && (snap.Gate || st.Blend > 0f);

        // From the logical position, over the unclamped interval: a load hitch must not read as a sprint and drop every hold.
        Vector3 logicalNow = chr->GameObject.Position;
        var moved = logicalNow - st.LastLogical;
        f.Speed = !st.Fresh && rawDt > 1e-4 ? moved.Length() / (float)rawDt : 0f;
        f.VelDir = f.Speed > 0.05f ? Vector3.Normalize(moved) : Vector3.Zero;
        st.Speed = f.Speed;
        st.LastLogical = logicalNow;
        st.Fresh = false;
        f.Still = f.Speed < StillSpeed;
        snap.Speed = f.Speed;

        var rawDrop = 0f;
        var body = new BodyRange { Lo = float.MinValue, Hi = float.MaxValue, GatherWant = float.MaxValue };
        var bodyDesired = Vector3.Zero;
        if (poseOk)
        {
            this.ReadTransform(chr, ref f);
            f.Weapons = c.MoveWeapons && active ? anchors[..AnchorWeapons(chr, skel, pose, anchors)] : Span<WeaponAnchor>.Empty;
            // Written here rather than under the gate: the overlay draws the ruler from these, and a snapshot is blank
            // every tick, so leaving them to the gated path puts the ruler at the world origin whenever it is shut.
            snap.Yaw = f.Yaw;
            snap.Ground = new Vector3(f.PosAnchor.X, f.OriginY, f.PosAnchor.Z);
            var hips = (Bones.Pos(in f.Bones[st.Chain.Left.Hip]).Y + Bones.Pos(in f.Bones[st.Chain.Right.Hip]).Y) * 0.5f;
            st.HipFrac = hips / f.LegLen;
            snap.HipFrac = st.HipFrac;
            st.Torso = ShoulderMid(in f);
        }

        if (active && c.Bump)
        {
            this.FindBump(chr, ref f, local);
        }

        if (active)
        {
            // Only an emote can carry the body away from the origin. While walking the ground under the hips may be a
            // deck below the rail, while the origin is the level the character stands on.
            if (mode != CharacterModes.Normal)
            {
                this.ProbeBase(ref f);
                snap.Ground.Y = f.OriginY + (f.BaseY * f.Scale.Y);
            }

            snap.BaseY = f.BaseY;
            Span<Vector3> desired = stackalloc Vector3[2];
            this.ProbeFoot(ref f, 0, ref snap.Left);
            this.ProbeFoot(ref f, 1, ref snap.Right);
            this.GatherFeet(ref f, ref snap, desired);
            this.PushFromWalls(ref f, 0, ref snap.Left);
            this.PushFromWalls(ref f, 1, ref snap.Right);

            this.ArrangeStance(ref f, ref snap, desired, out bodyDesired);

            for (var s = 0; s < 2; s++)
            {
                ref var foot = ref Foot(ref snap, s);
                this.MeasureFoot(ref f, s, ref foot, desired[s], ref body);
            }

            // `hi` below pins the body to the lowest planted foot. No share is left for the other leg: legs only
            // shorten, and the lowest is already as straight as it gets.
            var want = body.WantWeight > 0f ? body.WantSum / body.WantWeight : f.BaseY;
            if (body.GatherWant < float.MaxValue)
            {
                want = MathF.Max(want, body.GatherWant);
            }

            // Reach wins over knee comfort: a foot left hanging reads worse than a knee folded past its cap.
            var hi = MathF.Min(body.Hi, f.BaseY + (c.MaxPelvisRaiseFrac * f.LegLen));
            var lo = MathF.Min(MathF.Max(body.Lo, f.BaseY - f.MaxDrop), hi);
            rawDrop = Math.Clamp(want, lo, hi);
        }
        else
        {
            st.ClearGather();
        }

        st.SmoothDrop = Toward(st.SmoothDrop, rawDrop, Ease(dt, c.PelvisTau));
        var applied = st.SmoothDrop * st.Blend;
        snap.RawDrop = rawDrop;
        snap.SmoothDrop = st.SmoothDrop;
        snap.Applied = applied;
        st.BodyShift = Toward(st.BodyShift, bodyDesired, Ease(dt, bodyDesired.LengthSquared() > st.BodyShift.LengthSquared() ? c.GatherTauIn : c.GatherTauOut));
        snap.BodyShift = st.BodyShift;
        // Blended like the drop is: without that factor, closing the gate leaves the body shifted sideways for good.
        var bodyWorld = active ? Vector3.Transform(f.Scale * st.BodyShift, f.Rot) * st.Blend : Vector3.Zero;
        this.ApplyPelvis(chr, st, new Vector3(bodyWorld.X, applied, bodyWorld.Z));
        snap.OffsetSeen = st.SeenOffsetY;
        snap.OffsetWritten = st.Written;

        if (active && st.Blend > 0)
        {
            for (var s = 0; s < 2; s++)
            {
                ref var foot = ref Foot(ref snap, s);
                this.PlaceFoot(ref f, s, ref foot, applied);
            }
        }

        this.LeanSpine(ref f, ref snap, active);
        this.ShoveTorso(ref f, ref snap, active);
        this.TiltBody(ref f, ref snap, poseOk);
        if (f.Weapons.Length > 0)
        {
            CarryWeapons(f.Skel, f.Pose, f.Weapons);
        }

        snap.Weapons = f.Weapons.Length;
    }

    private static ref FootSnapshot Foot(ref Snapshot snap, int s) => ref (s == 0 ? ref snap.Left : ref snap.Right);

    private void ReadTransform(Character* chr, ref Frame f)
    {
        var c = this.Settings;
        var t = f.Skel->Transform;
        f.Pos = t.Position;
        f.Rot = t.Rotation;
        var facing = Vector3.Transform(Vector3.UnitZ, f.Rot);
        f.Yaw = MathF.Atan2(facing.X, facing.Z);
        f.Scale = t.Scale;

        // Probes are cast from where the animation puts the feet without our own offsets, or moving the body would undo
        // itself. Horizontally that is the logical position; vertically it keeps the game's visual offsets and drops ours.
        Vector3 logical = chr->GameObject.Position;
        f.PosAnchor = new Vector3(logical.X, f.Pos.Y - f.St.Written, logical.Z);
        f.OriginY = f.PosAnchor.Y;
        f.Bones = f.Pose->ModelPose.Data;

        f.RayUp = c.RayUpFrac * f.LegLen * f.Scale.Y;
        f.RayDown = c.RayDownFrac * f.LegLen * f.Scale.Y;
        f.MaxDist = f.RayUp + f.RayDown;
        f.ProbeTop = f.OriginY + f.RayUp;
        // Deliberately not tied to the stance: a smaller minimum stance must bring the feet closer, not shorten the search.
        f.Reach = 0.85f * f.LegLen * f.Scale.Y;
    }

    // The ground a prone body rests on, fitted to its contacts: pitch from hands against toes, roll from right against
    // left, each weighted by how near the ground it is, so a limb in the air drops out and an unplanted axis stays flat.
    // Probes half a leg from the origin sample beside the torso instead; solving the limbs was tried twice and reverted.
    private bool ContactTilt(ref Frame f, Vector3 fwd, Vector3 side, float cap, out Quaternion tilt)
    {
        tilt = Quaternion.Identity;
        if (!f.St.Chain.LeftArm.Resolved || !f.St.Chain.RightArm.Resolved)
        {
            return false;
        }

        Span<int> bones = [f.St.Chain.LeftArm.Ankle, f.St.Chain.RightArm.Ankle, f.St.Chain.Left.Toes, f.St.Chain.Right.Toes];
        Span<Vector3> at = stackalloc Vector3[4];
        Span<float> ground = stackalloc float[4];
        Span<float> weight = stackalloc float[4];
        for (var i = 0; i < 4; i++)
        {
            at[i] = f.PosAnchor + Vector3.Transform(f.Scale * Bones.Pos(in f.Bones[bones[i]]), f.Rot);
            weight[i] = 0f;
            if (!this.TryGround(at[i], in f, out _, out var g))
            {
                continue;
            }

            ground[i] = g;
            weight[i] = Math.Clamp(1f - (MathF.Abs((at[i].Y - g) / f.Scale.Y) / f.StepTol), 0f, 1f);
        }

        // Both groups of a pair need something planted, or that axis has no baseline to measure a slope over.
        var slopeFwd = Slope(at, ground, weight, 0, 1, 2, 3, fwd, in f);
        var slopeSide = Slope(at, ground, weight, 1, 3, 0, 2, side, in f);
        if (slopeFwd == 0f && slopeSide == 0f)
        {
            return false;
        }

        tilt = Solver.GroundTilt(slopeFwd, 0f, slopeSide, 0f, 0.5f, fwd, side, f.Rot, cap);
        return true;
    }

    // Ground rise per unit along `axis`, between the weighted centre of one pair of contacts and the other's.
    private static float Slope(scoped ReadOnlySpan<Vector3> at, scoped ReadOnlySpan<float> ground, scoped ReadOnlySpan<float> weight, int a1, int a2, int b1, int b2, Vector3 axis, in Frame f)
    {
        var wa = weight[a1] + weight[a2];
        var wb = weight[b1] + weight[b2];
        if (wa < 1e-3f || wb < 1e-3f)
        {
            return 0f;
        }

        var pa = ((at[a1] * weight[a1]) + (at[a2] * weight[a2])) / wa;
        var pb = ((at[b1] * weight[b1]) + (at[b2] * weight[b2])) / wb;
        var ga = ((ground[a1] * weight[a1]) + (ground[a2] * weight[a2])) / wa;
        var gb = ((ground[b1] * weight[b1]) + (ground[b2] * weight[b2])) / wb;
        var span = Vector3.Dot(pa - pb, axis);
        return MathF.Abs(span) < 0.1f * f.LegLen * f.Scale.Y ? 0f : (ga - gb) / span;
    }

    // The ground under the body itself, the baseline for every step threshold and the probe window: an animation that
    // carries the root far from the origin stands the feet well above or below the logical position.
    private void ProbeBase(ref Frame f)
    {
        var hips = (Bones.Pos(in f.Bones[f.St.Chain.Left.Hip]) + Bones.Pos(in f.Bones[f.St.Chain.Right.Hip])) * 0.5f;
        var at = f.PosAnchor + Vector3.Transform(f.Scale * new Vector3(hips.X, 0f, hips.Z), f.Rot);
        if (!this.TryGround(at, in f, out _, out var y))
        {
            // Nothing in the window: look again from headroom height, no higher, or a bridge overhead reads as the floor.
            var top = f.OriginY + (1.5f * f.LegLen * f.Scale.Y);
            if (top <= f.ProbeTop || !this.Raycast(new Vector3(at.X, top, at.Z), out var hit, top - f.ProbeTop + 0.01f))
            {
                return;
            }

            y = GroundAt(in hit, at.X, at.Z, out _);
        }

        f.BaseY = (y - f.OriginY) / f.Scale.Y;
        f.ProbeTop = f.OriginY + (f.BaseY * f.Scale.Y) + f.RayUp;
    }

    // Three probes along the sole, cast from character height so a foot against a riser does not see the step top; the
    // median keeps it on the tread it mostly stands on and lets a toe clip the next riser, as the base game does.
    // Tried and reverted: highest hit wins (floats the foot), and preferring flat hits over ramps.
    // tread, and preferring flat hits over ramps does not help on the bevelled stairs most of the game is built from.
    private void ProbeFoot(ref Frame f, int s, ref FootSnapshot foot)
    {
        var side = f.St.Chain.Side(s);
        foot.AnkleModel = Bones.Pos(in f.Bones[side.Ankle]);
        foot.AnkleWorld = f.PosAnchor + Vector3.Transform(f.Scale * foot.AnkleModel, f.Rot);
        var toeWorld = f.PosAnchor + Vector3.Transform(f.Scale * Bones.Pos(in f.Bones[side.Toes]), f.Rot);
        foot.Rest = side.RestBind + this.Settings.RestAdjustFrac * f.LegLen;

        foot.ToeWorld = toeWorld;
        var mid = (foot.AnkleWorld + toeWorld) * 0.5f;
        var top = f.ProbeTop;
        Span<RaycastHit> cand = stackalloc RaycastHit[3];
        var n = 0;
        if (this.Raycast(new Vector3(foot.AnkleWorld.X, top, foot.AnkleWorld.Z), out var heel, f.MaxDist))
        {
            cand[n++] = heel;
        }

        if (this.Raycast(new Vector3(mid.X, top, mid.Z), out var midHit, f.MaxDist))
        {
            cand[n++] = midHit;
        }

        if (this.Raycast(new Vector3(toeWorld.X, top, toeWorld.Z), out var toe, f.MaxDist))
        {
            cand[n++] = toe;
        }

        foot.Hit = n > 0;
        if (!foot.Hit)
        {
            foot.OverEdge = true;
            return;
        }

        // Sorting network by height: three hits give the median, two give the lower (never float over a void edge).
        if (n > 1 && cand[1].Point.Y < cand[0].Point.Y)
        {
            (cand[0], cand[1]) = (cand[1], cand[0]);
        }

        if (n > 2 && cand[2].Point.Y < cand[1].Point.Y)
        {
            (cand[1], cand[2]) = (cand[2], cand[1]);
        }

        if (n > 2 && cand[1].Point.Y < cand[0].Point.Y)
        {
            (cand[0], cand[1]) = (cand[1], cand[0]);
        }

        var chosen = n == 3 ? cand[1] : cand[0];

        foot.HitPoint = chosen.Point;
        foot.Material = chosen.Material;

        // Evaluated at the ankle's XZ rather than at the winning probe: on a slope the mid-foot probe is half a foot away.
        foot.GroundModelY = (GroundAt(in chosen, foot.AnkleWorld.X, foot.AnkleWorld.Z, out foot.HitNormal) - f.OriginY) / f.Scale.Y;
        foot.OverEdge = foot.GroundModelY - f.BaseY < -f.MaxStepDown;
    }

    private void ArrangeStance(ref Frame f, ref Snapshot snap, scoped Span<Vector3> desired, out Vector3 bodyDesired)
    {
        var c = this.Settings;
        bodyDesired = Vector3.Zero;
        if (!snap.Left.Gathered && !snap.Right.Gathered)
        {
            return;
        }

        // The wall push and recentring since may have closed the pair the search picked apart: this has the final say.
        // Take the smaller move, a stance sideways or a boot length lengthwise, uncrossing first if it goes lengthwise.
        var minSep = 2f * c.MinStanceFrac * f.LegLen;
        var footLen = this.FootLength(in snap, in f);
        var side = f.St.Chain.BindSide;
        var fwd = f.St.Chain.BindForward;
        var sep = this.Separation(in snap, desired);
        var lat = Vector3.Dot(sep, side);
        var lon = Vector3.Dot(sep, fwd);
        if (lat < minSep && MathF.Abs(lon) < footLen)
        {
            var moveL = snap.Left.Gathered;
            var moveR = snap.Right.Gathered;
            if (minSep - lat <= footLen - MathF.Abs(lon) + MathF.Max(0f, -lat))
            {
                this.Separate(in snap, desired, side, minSep, moveL, moveR);
            }
            else
            {
                this.Separate(in snap, desired, side, 0f, moveL, moveR);
                this.Separate(in snap, desired, lon >= 0f ? fwd : -fwd, footLen, moveL, moveR);
            }
        }

        // Not in platforming mode: the body moving onto the support is exactly what hides the logical position.
        if (this.moveHook == null || c.GatherToPosition)
        {
            return;
        }

        // Stand over the feet: the bind-pose stance centre over the midpoint of the final targets. From the bind pose
        // rather than the animated ankles, so neither idle sway nor a stepping turn moves the body.
        var mid = (desired[0] + desired[1]) * 0.5f;
        var ankleMid = (snap.Left.AnkleModel + snap.Right.AnkleModel) * 0.5f;
        bodyDesired = mid + new Vector3(ankleMid.X, 0f, ankleMid.Z) - f.St.Chain.BindMid;
        desired[0] -= bodyDesired;
        desired[1] -= bodyDesired;
    }

    // The boot's length as the wall box sees it: ankle to toe bone plus a clearance at each end, model units.
    private float FootLength(in Snapshot snap, in Frame f)
    {
        var l = new Vector3(snap.Left.ToeWorld.X - snap.Left.AnkleWorld.X, 0f, snap.Left.ToeWorld.Z - snap.Left.AnkleWorld.Z).Length();
        var r = new Vector3(snap.Right.ToeWorld.X - snap.Right.AnkleWorld.X, 0f, snap.Right.ToeWorld.Z - snap.Right.AnkleWorld.Z).Length();
        return (MathF.Max(l, r) / f.Scale.Y) + (2f * this.Settings.WallClearanceFrac * f.LegLen);
    }

    // Right target minus left target, horizontal, model space, wall push included.
    private Vector3 Separation(in Snapshot snap, scoped Span<Vector3> desired)
    {
        var tl = snap.Left.AnkleModel + desired[0] + snap.Left.WallShift;
        var tr = snap.Right.AnkleModel + desired[1] + snap.Right.WallShift;
        return new Vector3(tr.X - tl.X, 0f, tr.Z - tl.Z);
    }

    private void Separate(in Snapshot snap, scoped Span<Vector3> desired, Vector3 axis, float want, bool moveLeft, bool moveRight)
    {
        var push = want - Vector3.Dot(this.Separation(in snap, desired), axis);
        if (push <= 0f || (!moveLeft && !moveRight))
        {
            return;
        }

        var each = moveLeft && moveRight ? push * 0.5f : push;
        if (moveLeft)
        {
            desired[0] -= axis * each;
        }

        if (moveRight)
        {
            desired[1] += axis * each;
        }
    }

    // Keeps the boot out of walls wherever it stands. Runs before the stance rule, which keeps the final say: a wall
    // pushing one foot toward the other must not stack them. Recomputed from the animation every frame, never
    // accumulated, so it cannot drift.
    private void PushFromWalls(ref Frame f, int s, ref FootSnapshot foot)
    {
        var c = this.Settings;
        ref var wall = ref f.St.WallShift[s];

        // Weight fades to zero at twice the threshold; a partially weighted foot would get a partial correction and hover.
        foot.Planted = foot.Hit && MathF.Abs(foot.GroundModelY - f.BaseY) <= f.MaxStep && !foot.OverEdge
            ? Math.Clamp(2f - (foot.AnkleModel.Y - foot.Rest) / f.LiftThreshold, 0f, 1f)
            : 0f;

        var want = Vector3.Zero;
        var half = c.WallClearanceFrac * f.LegLen * f.Scale.Y;
        if (c.KeepFeetOutOfWalls && foot.Planted > 0f && half > 1e-4f)
        {
            var facing = new Vector3(foot.ToeWorld.X - foot.AnkleWorld.X, 0f, foot.ToeWorld.Z - foot.AnkleWorld.Z);
            var toeLen = facing.Length();
            if (toeLen > 1e-4f)
            {
                facing /= toeLen;
                var placed = foot.AnkleWorld + Vector3.Transform(f.Scale * (f.St.GatherShift[s] + f.St.BodyShift) * f.St.Blend, f.Rot);
                var groundY = f.OriginY + (foot.GroundModelY * f.Scale.Y);
                var pushWorld = this.WallPush(placed, facing, ahead: toeLen + half, clearance: half, height: foot.Rest * f.Scale.Y * 0.6f, groundY, in f);
                want = Vector3.Transform(pushWorld, Quaternion.Inverse(f.Rot)) / f.Scale * foot.Planted;
                want.Y = 0f;
            }
        }

        wall = Toward(wall, want, Ease(f.Dt, 0.1f));
        foot.WallShift = wall;
    }

    private void MeasureFoot(ref Frame f, int s, ref FootSnapshot foot, Vector3 desired, ref BodyRange body)
    {
        var c = this.Settings;
        var side = f.St.Chain.Side(s);
        ref var shift = ref f.St.GatherShift[s];
        shift = Toward(shift, desired, Ease(f.Dt, desired.LengthSquared() > shift.LengthSquared() ? c.GatherTauIn : c.GatherTauOut));
        foot.GatherShift = shift;
        if (!foot.Hit)
        {
            return;
        }

        var anklePlaced = foot.AnkleModel + shift + foot.WallShift;
        foot.AnkleAboveGround = foot.AnkleModel.Y - foot.GroundModelY;

        var a = Bones.Pos(in f.Bones[side.Hip]);
        var b = Bones.Pos(in f.Bones[side.Knee]);
        var v = anklePlaced - a;
        var l1 = (b - a).Length();
        var l2 = (foot.AnkleModel - b).Length();
        var vxz2 = v.X * v.X + v.Z * v.Z;
        var reach = (l1 + l2) * c.StraightenLimit;
        // How far the ankle can descend before the leg is as straight as allowed.
        foot.MaxExtend = MathF.Max(0f, MathF.Sqrt(MathF.Max(0f, reach * reach - vxz2)) + v.Y);
        // How far it can rise before the knee bends further than allowed (law of cosines on the interior angle).
        var interior = (180f - c.MaxKneeBendDeg) * MathF.PI / 180f;
        var dMin2 = l1 * l1 + l2 * l2 - (2f * l1 * l2 * MathF.Cos(interior));
        foot.MaxRaiseByKnee = MathF.Max(0f, -v.Y - MathF.Sqrt(MathF.Max(0f, dMin2 - vxz2)));

        if (foot.Planted > 0)
        {
            // Where the terrain under this foot asks the body to sit.
            body.WantSum += foot.GroundModelY * foot.Planted;
            body.WantWeight += foot.Planted;

            // The body cannot rise past this leg's reach, nor sink past its knee cap, in proportion to the foot's weight.
            var reachLimit = foot.MaxExtend + foot.GroundModelY;
            var kneeLimit = foot.GroundModelY - foot.MaxRaiseByKnee;
            body.Hi = MathF.Min(body.Hi, reachLimit < 0 ? reachLimit * foot.Planted : reachLimit);
            body.Lo = MathF.Max(body.Lo, kneeLimit > 0 ? kneeLimit * foot.Planted : kneeLimit);
        }

        if (foot.Gathered)
        {
            // Stand tall on a narrow support, spending the leg with the least extension to spare: a preference must never
            // drive the other leg straight, where the foot beside it would float.
            body.GatherWant = MathF.Min(body.GatherWant, foot.MaxExtend * c.GatherStraighten);
        }
    }

    private void PlaceFoot(ref Frame f, int s, ref FootSnapshot foot, float applied)
    {
        var c = this.Settings;
        if (!foot.Hit || MathF.Abs(foot.GroundModelY - f.BaseY) > f.MaxStep || foot.OverEdge)
        {
            return;
        }

        // Terrain height minus what the pelvis already took, so the foot keeps the clearance the animation gave it.
        var slopeTan = foot.HitNormal.Y > 0.2f ? MathF.Sqrt(MathF.Max(0f, 1f - foot.HitNormal.Y * foot.HitNormal.Y)) / foot.HitNormal.Y : 0f;
        var offset = foot.GroundModelY - applied + MathF.Max(0f, foot.Rest - foot.AnkleModel.Y) + c.SlopeLiftFrac * f.LegLen * slopeTan;
        offset = offset < 0
            ? MathF.Max(offset * foot.Planted, -foot.MaxExtend)
            : MathF.Min(offset, MathF.Min(f.MaxRaise, foot.MaxRaiseByKnee));
        foot.Offset = offset * f.St.Blend;

        // Tilt only while the sole is at ground level, or uphill running bends the toes up twice.
        foot.Contact = Math.Clamp(1f - (foot.AnkleModel.Y - foot.Rest) / MathF.Max(c.TiltFadeFrac * f.LegLen, 1e-3f), 0f, 1f);
        var tilt = Quaternion.Identity;
        var nModel = Vector3.Transform(foot.HitNormal, Quaternion.Inverse(f.Rot));
        if (nModel.LengthSquared() > 0.5f && nModel.Y > 0f)
        {
            nModel = Vector3.Normalize(nModel);
            var angle = MathF.Acos(Math.Clamp(nModel.Y, -1f, 1f));
            var axis = Vector3.Cross(Vector3.UnitY, nModel);
            if (angle > 0.005f && axis.LengthSquared() > 1e-8f && foot.Contact > 0f)
            {
                angle = MathF.Min(angle, c.MaxAnkleAngleDeg * MathF.PI / 180f) * foot.Contact * f.St.Blend;
                tilt = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);
                foot.TiltDeg = angle * 180f / MathF.PI;
            }
        }

        // Gathered feet yaw toward the bind forward (Hrothgar knees splay outward). Only with the sole down: yawing a
        // foot poised on its toes bends it inward.
        var gatherBlend = Math.Clamp(foot.GatherShift.Length() / MathF.Max(0.05f * f.LegLen, 1e-3f), 0f, 1f) * c.GatherForward * foot.Contact * f.St.Blend;
        var ankleDelta = tilt;
        if (gatherBlend > 0.001f)
        {
            var footFwd = Bones.Pos(in f.Bones[f.St.Chain.Side(s).Toes]) - foot.AnkleModel;
            footFwd.Y = 0f;
            if (footFwd.LengthSquared() > 1e-6f)
            {
                footFwd = Vector3.Normalize(footFwd);
                var fwd = f.St.Chain.BindForward;
                var ang = MathF.Atan2(Vector3.Cross(footFwd, fwd).Y, Vector3.Dot(footFwd, fwd));
                ankleDelta = Quaternion.Normalize(tilt * Quaternion.CreateFromAxisAngle(Vector3.UnitY, ang * gatherBlend));
                foot.YawDeg = ang * gatherBlend * 180f / MathF.PI;
            }
        }

        var eps = 0.003f * f.LegLen;
        if (MathF.Abs(foot.Offset) > eps || foot.TiltDeg > 0.1f || foot.GatherShift.LengthSquared() > eps * eps || foot.WallShift.LengthSquared() > eps * eps)
        {
            var ikTarget = foot.AnkleModel + ((foot.GatherShift + foot.WallShift) * f.St.Blend) + new Vector3(0, foot.Offset, 0);
            foot.IkTargetWorld = f.Pos + Vector3.Transform(f.Scale * ikTarget, f.Rot);
            foot.Solved = SolveLeg(f.Pose, f.St.Chain.Side(s), ikTarget, ankleDelta, f.St.Chain.BindForward, gatherBlend);
        }
    }

    // How far down the body is, which weights the tilt: the sit-down animation starts upright, and an upright body
    // tilted to the slope reads as falling over. Pinned full while a floor emote runs, or /pushups swings it every rep.
    private static float Low(in Frame f, in Snapshot snap) => f.St.FloorLoop && snap.Mode == CharacterModes.EmoteLoop
        ? 1f
        : Math.Clamp((StandingHips - f.St.HipFrac) / (StandingHips - FloorHips), 0f, 1f);

    private void TiltBody(ref Frame f, ref Snapshot snap, bool poseOk)
    {
        var c = this.Settings;
        var target = Quaternion.Identity;
        // Sitting always, lying down by choice.
        if (poseOk && snap.OnFloor && c.MaxSitTiltDeg > 0f && (c.FloorTilt || !f.St.FloorLoop))
        {
            var r = 0.5f * f.LegLen * f.Scale.Y;
            var fwd = new Vector3(MathF.Sin(f.Yaw), 0f, MathF.Cos(f.Yaw));
            var side = new Vector3(fwd.Z, 0f, -fwd.X);
            var cap = c.MaxSitTiltDeg * MathF.PI / 180f;
            // A prone body is fitted to what it lies on; a seated one has its contacts under itself, where four probes
            // around the origin already sample the right ground.
            if (f.St.FloorLoop && this.ContactTilt(ref f, fwd, side, cap, out var fitted))
            {
                target = Quaternion.Slerp(Quaternion.Identity, fitted, Low(in f, in snap));
            }
            else if (this.TryGround(f.PosAnchor + (fwd * r), in f, out _, out var ahead) && this.TryGround(f.PosAnchor - (fwd * r), in f, out _, out var behind)
                && this.TryGround(f.PosAnchor + (side * r), in f, out _, out var right) && this.TryGround(f.PosAnchor - (side * r), in f, out _, out var left))
            {
                var full = Solver.GroundTilt(ahead, behind, right, left, r, fwd, side, f.Rot, cap);
                target = Quaternion.Slerp(Quaternion.Identity, full, Low(in f, in snap));
            }
        }

        f.St.Tilt = Quaternion.Slerp(f.St.Tilt, target, Ease(f.Dt, c.SitTiltTau));
        snap.SitTiltDeg = 2f * MathF.Acos(MathF.Min(MathF.Abs(f.St.Tilt.W), 1f)) * 180f / MathF.PI;

        if (poseOk && snap.SitTiltDeg > 0.05f)
        {
            RotateBody(f.Skel, f.Pose, f.St.Tilt);
        }
    }

    private void LeanSpine(ref Frame f, ref Snapshot snap, bool active)
    {
        var c = this.Settings;
        var leanTarget = 0f;
        if (active && c.SlopeLean && f.Speed > 0.3f)
        {
            var n = (snap.Left.Hit ? snap.Left.HitNormal : Vector3.Zero) + (snap.Right.Hit ? snap.Right.HitNormal : Vector3.Zero);
            if (n.Y > 0.2f)
            {
                var uphill = -(n.X * f.VelDir.X + n.Z * f.VelDir.Z) / n.Y; // slope tangent along travel, positive uphill
                var slopeAngle = MathF.Atan(uphill);
                var lean = slopeAngle * (slopeAngle >= 0 ? c.LeanUphillGain : c.LeanDownhillGain) * Math.Clamp(f.Speed / RunSpeed, 0f, 1f);
                var cap = MathF.Abs(c.MaxLeanDeg) * MathF.PI / 180f;
                leanTarget = Math.Clamp(lean, -cap, cap);
            }
        }

        f.St.Lean = Toward(f.St.Lean, leanTarget, Ease(f.Dt, c.LeanTau));
        snap.LeanDeg = f.St.Lean * 180f / MathF.PI;
        if (!active || f.St.Chain.SpineSub.Length == 0)
        {
            return;
        }

        // The lean is additive on the animated spine, so measure the animated pitch first and use only the room left.
        // A male Hrothgar idles already well pitched forward.
        var spineDir = Bones.Pos(in f.Bones[f.St.Chain.Neck]) - Bones.Pos(in f.Bones[f.St.Chain.SpineA]);
        var pitch = MathF.Atan2(Vector3.Dot(spineDir, f.St.Chain.BindForward), spineDir.Y);
        snap.SpinePitchDeg = pitch * 180f / MathF.PI;
        var leanApplied = f.St.Lean * f.St.Blend;
        var room = (c.MaxTotalPitchDeg * MathF.PI / 180f) - pitch;
        var roomBack = pitch - (c.MinTotalPitchDeg * MathF.PI / 180f);
        if (leanApplied > room)
        {
            leanApplied = MathF.Max(0f, room);
        }
        else if (-leanApplied > roomBack)
        {
            leanApplied = -MathF.Max(0f, roomBack);
        }

        if (MathF.Abs(leanApplied) > 0.002f)
        {
            var axis = Vector3.Cross(Vector3.UnitY, f.St.Chain.BindForward);
            ApplySpineLean(f.Skel, f.Pose, in f.St.Chain, Quaternion.CreateFromAxisAngle(axis, leanApplied), Quaternion.CreateFromAxisAngle(axis, -leanApplied));
        }
    }

    // Characters have no collision with each other, so this is our own: centres a bump radius apart on the same
    // floor, closing faster than a nudge. The one moving takes the push and the one it ran into takes the opposite, so
    // running into someone and being run into both land. Every ticked character scans, not just the local player, which
    // is what lets someone else shove you; a character slower than the threshold leaves before the scan, so a crowd
    // standing still costs nothing. Two cooldowns: one between any two bumps, a longer one before the same character
    // counts again.
    private void FindBump(Character* chr, ref Frame f, bool local)
    {
        var c = this.Settings;
        var st = f.St;

        // Only you are worth scanning for while standing still: a bystander who has to be moving anyway costs nothing
        // when the crowd is idle, and that early return is the whole bound on what this walk costs in a plaza.
        var shoved = local && c.Shoved;
        if (st.BumpAge < c.BumpCooldown || (f.Speed < c.BumpMinSpeed && !shoved))
        {
            return;
        }

        var now = this.clock.Elapsed.TotalSeconds;
        this.DropStaleNeighbours(now);
        var radius = c.BumpRadiusFrac * f.LegLen * f.Scale.Y;
        var myTop = chr->Height * chr->Scale;
        var myTorso = st.Torso;
        var myLift = MathF.Max(myTorso.Y - st.LastLogical.Y, 0f);
        // Speed off the logical position, not off the shoulder: the torso swings with every stride, and differentiating
        // that reads as a metre a second of noise while standing still.
        var vel = f.VelDir * f.Speed;
        // Indexed rather than enumerated: IObjectTable.GetEnumerator returns the interface, which allocates per frame.
        for (var i = 0; i < Objects.Length; i++)
        {
            var o = Objects[i];
            // Players, and townspeople only when asked. BattleNpc is every monster, pet, egi, carbuncle, chocobo companion
            // and trust, none of which should shove or be shoved; minions and mounts are their own kinds and were never in.
            if (o == null || o.Address == 0 || o.GameObjectId == st.Id || !(o.ObjectKind == ObjectKind.Pc || (c.BumpNpcs && o.ObjectKind == ObjectKind.EventNpc)))
            {
                continue;
            }

            var theirVel = shoved ? this.TrackNeighbour(o.Address, o.GameObjectId, o.Position, now) : Vector3.Zero;

            if (o.GameObjectId == st.BumpTarget && st.BumpAge < c.BumpSameCooldown)
            {
                continue;
            }

            if (MathF.Abs(o.Position.Y - st.LastLogical.Y) > f.LegLen * f.Scale.Y)
            {
                continue;
            }

            // A quest NPC can stand in the object table with nothing drawn, or with its model switched off (run into
            // in game). The draw object's own visibility bit is not consulted: it may only mean out of frame.
            var other = (Character*)o.Address;
            if (other->DrawObject == null || (other->RenderFlags & FFXIVClientStructs.FFXIV.Client.Game.Object.VisibilityFlags.Model) != 0)
            {
                continue;
            }

            // Their size against ours, 1 when either reads nothing: a body's radius grows with its height.
            var theirTop = other->Height * other->Scale;
            var ratio = myTop > 1e-3f && theirTop > 1e-3f ? theirTop / myTop : 1f;
            var reach = radius * (1f + ratio);

            if (!this.states.TryGetValue(o.Address, out var known) || known.Id != o.GameObjectId)
            {
                known = null;
            }

            // Their shoulders when something is ticking them, otherwise ours carried over by the height ratio and set
            // above their own feet - which is the same answer to within the lean, and costs no pose walk.
            var theirTorso = known != null && known.Torso != Vector3.Zero
                ? known.Torso
                : o.Position with { Y = o.Position.Y + (myLift * ratio) };

            // Shoulder against shoulder rather than floor against floor: on stairs the body a step up is still within
            // reach, while one on the balcony above is a torso away and out of it.
            var to = theirTorso - myTorso;
            if (MathF.Abs(to.Y) > f.LegLen * f.Scale.Y)
            {
                continue;
            }

            to.Y = 0f;
            var d2 = to.LengthSquared();
            if (d2 > reach * reach || d2 < 1e-6f)
            {
                continue;
            }

            // Their speed counts as well as ours, so being run into while standing still lands exactly as running into
            // them does. Gated on the whole relative speed and merely on the gap shrinking, not on the speed along the
            // line between them: measured where the bodies first touch, that component is near zero for anything but a
            // head-on hit, so shoulder-to-shoulder passes at a full run were dropped.
            var dir = to / MathF.Sqrt(d2);
            var rel = vel - theirVel;
            rel.Y = 0f;
            var closing = rel.Length();
            if (Vector3.Dot(rel, dir) <= 0f || closing < c.BumpMinSpeed)
            {
                continue;
            }

            // Sitting or lying, nothing stands at shoulder height to run into. Mode covers the seated emotes; the hips
            // cover a floor emote once the character is being ticked.
            if (other->Mode == CharacterModes.InPositionLoop || (known != null && known.HipFrac < FloorHips))
            {
                continue;
            }

            // A body under two fifths of the other's height hits it below the hips and leaves its torso alone; from
            // seven tenths up it hits in full. So a Lalafell shoves a Roegadyn's knees and takes the whole shove
            // itself, while everyone else moves a Hrothgar fully: with the line at half height the tallest races
            // barely flinched against anyone (seen in game).
            // A bump landing while the last still plays rises from where that one stands, so the angle never drops
            // to zero and pops back up. The direction does snap, as a second hit from another side would.
            var rise = MathF.Max(c.BumpRiseSeconds, 1e-3f);
            st.BumpTarget = o.GameObjectId;
            st.BumpAge = Envelope(st.BumpAge, rise, c.BumpTau) * rise;
            st.BumpPush = -dir * Reaches(ratio);
            // The closing speed rather than our own, so a sprinter running into someone standing still knocks them
            // round as hard as running into them would have.
            st.BumpBody = Math.Clamp(closing / RunSpeed, 0f, 1f);
            if (known != null)
            {
                known.BumpAge = Envelope(known.BumpAge, rise, c.BumpTau) * rise;
                known.BumpPush = dir * Reaches(1f / ratio);
                known.BumpBody = Math.Clamp(known.Speed / RunSpeed, 0f, 1f);
            }

            // Only bumps you are in: two strangers brushing past each other across the plaza is not something to make
            // a noise about, and with a crowd being ticked it was most of them. Your own scan covers both directions,
            // since being run into is found from here too.
            if (local)
            {
                this.WantGrunt((nint)chr, st.Id);
                this.WantGrunt(o.Address, o.GameObjectId);
            }

            return;
        }

        static float Reaches(float ratio) => Math.Clamp((ratio - 0.4f) / 0.3f, 0f, 1f);
    }

    // Timed rather than counted in frames, so a body we looked away from for a while still reads a true speed. A step
    // no mount could have travelled is a teleport or a zone load, and reads as standing rather than as a charge.
    private Vector3 TrackNeighbour(nint addr, ulong id, Vector3 pos, double now)
    {
        ref var n = ref CollectionsMarshal.GetValueRefOrAddDefault(this.neighbours, addr, out var existed);
        var dt = (float)(now - n.Seen);
        // The allocator reuses addresses, so a changed spawn id means this is someone else and the old position is not theirs.
        var vel = existed && n.Id == id && dt > 1e-4f ? (pos - n.Pos) / dt : Vector3.Zero;
        n.Id = id;
        n.Pos = pos;
        n.Seen = now;
        return float.IsFinite(vel.X + vel.Y + vel.Z) && vel.LengthSquared() <= WarpSpeed * WarpSpeed ? vel : Vector3.Zero;
    }

    private void DropStaleNeighbours(double now)
    {
        foreach (var (addr, n) in this.neighbours)
        {
            if (now - n.Seen > 1.0)
            {
                this.neighbours.Remove(addr);
            }
        }
    }

    // A straight rise, then a release that lingers at the peak before it falls: a Gaussian, where a plain exponential
    // is half gone by the first frame after the peak and read as weak in game.
    private static float Envelope(float age, float rise, float tau)
    {
        rise = MathF.Max(rise, 1e-3f);
        var fall = (age - rise) / MathF.Max(tau, 1e-3f);
        return age < rise ? age / rise : MathF.Exp(-fall * fall);
    }

    // A shove from the front or behind pitches the torso; one from the side mostly turns it, the shoulder that was hit
    // swinging back so the chest faces the other body, with a little sideways lean. The lean was the whole sideways
    // reaction at first and read as swaying on bumped bystanders, who are always hit side-on by someone running past.
    // The twist is full from a fifth of a turn off centre and fades to nothing head-on. The head goes with the
    // shoulders: the slope lean holds it level, a shove does not.
    private void ShoveTorso(ref Frame f, ref Snapshot snap, bool active)
    {
        var c = this.Settings;
        var st = f.St;
        st.BumpAge += f.Dt;
        var envelope = Envelope(st.BumpAge, c.BumpRiseSeconds, c.BumpTau);
        var angle = c.Bump && active ? c.BumpMaxDeg * MathF.PI / 180f * envelope * st.Blend * st.BumpPush.Length() : 0f;
        snap.BumpDeg = angle * 180f / MathF.PI;
        if (angle < 0.002f || st.Chain.SpineSub.Length == 0)
        {
            return;
        }

        var push = Vector3.Transform(st.BumpPush, Quaternion.Inverse(f.Rot));
        var len = push.Length();
        if (len < 1e-4f)
        {
            return;
        }

        var fwd = st.Chain.BindForward;
        var side = Vector3.Cross(Vector3.UnitY, fwd);
        var fore = Vector3.Dot(push, fwd) / len;
        var across = Vector3.Dot(push, side) / len;
        var pitch = Quaternion.CreateFromAxisAngle(side, angle * fore);
        var lean = Quaternion.CreateFromAxisAngle(fwd, -angle * across * BumpSideLean);
        var twistAngle = Math.Clamp(-3f * across, -1f, 1f) * angle;
        var twist = Quaternion.CreateFromAxisAngle(Vector3.UnitY, twistAngle);
        // The twist goes innermost so the tip lands exactly along the push rather than swung round by the yaw.
        var turn = lean * pitch * twist;

        // Read here rather than at the turn below because the head is held against this too: RotateBody carries the
        // neck with everything else, so a counter applied before it has to undo both.
        var bodyYaw = twistAngle * st.BumpBody * c.BumpBodyTurn;
        var yawing = MathF.Abs(bodyYaw) >= 0.002f;
        var q = yawing ? Quaternion.CreateFromAxisAngle(Vector3.UnitY, bodyYaw) : Quaternion.Identity;

        // The head keeps the orientation the animation gave it, so the character goes on looking where it was looking
        // while the shoulders and hips are carried out from under it. The spine reaches the neck having accumulated
        // exactly `turn`, so undoing that and the rigid yaw holds the head still. A half counter was tried back when
        // nothing but the spine moved and read as weak, hence a knob rather than a constant.
        var hold = Math.Clamp(c.BumpHeadHold, 0f, 1f);
        var neck = hold > 0.001f
            ? Quaternion.Slerp(Quaternion.Identity, Quaternion.Inverse(q) * Quaternion.Inverse(turn), hold)
            : Quaternion.Identity;
        ApplySpineLean(f.Skel, f.Pose, in st.Chain, turn, neck);

        // At speed the whole body is knocked round as well, hips and all, while a standing body keeps the hit in its
        // shoulders. After the torso, so the torso axes are the animation's own; the rigid turn then carries torso,
        // head and partials together. The legs keep the stride: each ankle goes back where the animation had it, its
        // orientation turned back and the knee bending the way it did, so the hips go with the pelvis and the legs
        // re-bend to reach. Letting the feet swing round with the body read as the body sliding (seen in game).
        // A shove moves you. Without this a body that was standing still took the hit entirely in its spine, hips and
        // feet nailed to the floor, which read as bending at the waist rather than being shoved (seen in game); the
        // one that was running looked right only because the turn above gave it the leg re-solve as a side effect.
        // The knees are not posed: the hips are carried off the feet and the legs below bend because they must reach.
        // Model space, so a fraction of the bind leg length needs no scale factor.
        var shove = push / len * (c.BumpShoveFrac * f.LegLen * envelope * st.Blend * st.BumpPush.Length());
        shove.Y = 0f;
        if (!yawing && shove.LengthSquared() < 1e-8f)
        {
            return;
        }

        Span<Vector3> ankle = stackalloc Vector3[2];
        Span<Vector3> bend = stackalloc Vector3[2];
        for (var s = 0; s < 2; s++)
        {
            var leg = st.Chain.Side(s);
            ankle[s] = Bones.Pos(in f.Bones[leg.Ankle]);
            bend[s] = Bones.Pos(in f.Bones[leg.Knee]) - Bones.Pos(in f.Bones[leg.Hip]);
        }

        if (yawing)
        {
            RotateBody(f.Skel, f.Pose, q);
        }

        if (shove.LengthSquared() >= 1e-8f && float.IsFinite(shove.X + shove.Z))
        {
            TranslateBody(f.Skel, f.Pose, shove);
        }

        var back = Quaternion.Inverse(q);
        for (var s = 0; s < 2; s++)
        {
            SolveLeg(f.Pose, st.Chain.Side(s), ankle[s], back, bend[s], 1f);
        }
    }
}
