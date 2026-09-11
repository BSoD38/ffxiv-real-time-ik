using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;

namespace FootIk;

// All model-space heights here are relative to the character origin. During an emote, step thresholds and the probe
// window are judged from the ground under the body instead (Frame.BaseY): root motion can carry it far from the origin.
// Never while walking: on a guardrail the ground under the body is the deck below, and the origin is the level.
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
        public float Yaw;
        public hkQsTransformf* Bones;
        public hkaPose* Pose;
        public Skeleton* Skel;
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

    private readonly ConditionFlag[] gateFlags =
    [
        ConditionFlag.Jumping, ConditionFlag.Mounted, ConditionFlag.Swimming, ConditionFlag.Diving,
        ConditionFlag.InFlight, ConditionFlag.WatchingCutscene, ConditionFlag.BetweenAreas,
    ];

    private void Tick()
    {
        var c = this.Settings;
        var now = this.clock.Elapsed.TotalSeconds;
        var rawDt = now - this.lastTick;
        var dt = (float)Math.Clamp(rawDt, 0, 0.1);
        this.lastTick = now;

        var snap = default(Snapshot);
        var lp = Objects.LocalPlayer;
        if (lp == null)
        {
            this.blend = 0;
            this.smoothDrop = 0;
            this.written = 0f;
            this.bodyShift = default;
            this.pelvisForMove = default;
            this.localAddress = 0;
            this.ClearGather();
            this.Snap = snap;
            return;
        }

        var chr = (Character*)lp.Address;
        var fresh = this.localAddress != lp.Address;
        this.localAddress = lp.Address;
        var mode = chr->Mode;
        snap.Mode = mode;
        snap.ModeParam = chr->ModeParam;
        snap.IsJumping = chr->IsJumping();
        snap.GPose = ClientState.IsGPosing;
        snap.Conditions = Condition.Any(this.gateFlags);
        var clear = c.Enabled && !snap.Conditions && !snap.GPose;
        // EmoteLoop is every standing loop (dances, /lean, /playdead). Sitting on the ground, on a chair and sleeping are
        // InPositionLoop with the EmoteMode row in ModeParam: 1 ground, 2 chair, 3 sleep. A chair is furniture, not ground.
        snap.Gate = clear && !snap.IsJumping && (mode == CharacterModes.Normal || (c.Emotes && mode == CharacterModes.EmoteLoop));
        snap.Sitting = clear && c.Emotes && mode == CharacterModes.InPositionLoop && chr->ModeParam is 1 or 3;

        // A zero component in the skeleton scale turns every model-space conversion below into infinity, and smoothDrop
        // and bodyShift hold a NaN forever once one reaches them.
        var poseOk = ResolvePose(chr, ref this.chain, ref snap, out var pose, out var skel)
            && skel->Transform.Scale.X > 1e-4f && skel->Transform.Scale.Y > 1e-4f && skel->Transform.Scale.Z > 1e-4f;
        var f = new Frame { Dt = dt, Pose = pose, Skel = skel };
        f.LegLen = poseOk ? this.chain.LegLength : 0f;
        f.MaxDrop = c.MaxDropFrac * f.LegLen;
        f.MaxRaise = c.MaxRaiseFrac * f.LegLen;
        f.MaxStep = c.MaxStepFrac * f.LegLen;
        f.MaxStepDown = c.MaxStepDownFrac * f.LegLen;
        f.StepTol = 0.15f * f.LegLen; // a stair tread, not a curb or a rail
        f.LiftThreshold = MathF.Max(c.LiftThresholdFrac * f.LegLen, 1e-3f);
        snap.LegLength = f.LegLen;
        this.blend = MoveToward(this.blend, snap.Gate && poseOk ? 1f : 0f, c.BlendSeconds > 0 ? dt / c.BlendSeconds : 1f);
        snap.Blend = this.blend;
        var active = poseOk && (snap.Gate || this.blend > 0f);

        // Speed comes from the logical position: gathering latches while stationary, the lean needs the travel direction.
        // Over the unclamped interval: a load hitch must not read as a sprint and drop every hold.
        Vector3 logicalNow = chr->GameObject.Position;
        var moved = logicalNow - this.lastLogical;
        f.Speed = !fresh && rawDt > 1e-4 ? moved.Length() / (float)rawDt : 0f;
        f.VelDir = f.Speed > 0.05f ? Vector3.Normalize(moved) : Vector3.Zero;
        this.lastLogical = logicalNow;
        f.Still = f.Speed < c.StillSpeed;
        snap.Speed = f.Speed;

        var rawDrop = 0f;
        var body = new BodyRange { Lo = float.MinValue, Hi = float.MaxValue, GatherWant = float.MaxValue };
        var bodyDesired = Vector3.Zero;
        if (poseOk)
        {
            this.ReadTransform(chr, ref f);
        }

        if (active)
        {
            // Only an emote can carry the body away from the origin. While walking, the logical position is the level the
            // character stands on; the ground under the hips may be a deck a metre below the rail it is walking along.
            if (mode != CharacterModes.Normal)
            {
                this.ProbeBase(ref f);
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

            // The body follows the ground under it in full; only the feet's spread around that is shared between the legs.
            var want = f.BaseY + (body.WantWeight > 0f ? (body.WantSum / body.WantWeight - f.BaseY) * c.LegBalance : 0f);
            if (body.GatherWant < float.MaxValue)
            {
                want = MathF.Max(want, body.GatherWant);
            }

            // Reach wins over knee comfort when the two cannot both be satisfied: a foot left hanging above the ground
            // reads worse than a knee folded past its cap.
            var hi = MathF.Min(body.Hi, f.BaseY + (c.MaxPelvisRaiseFrac * f.LegLen));
            var lo = MathF.Min(MathF.Max(body.Lo, f.BaseY - f.MaxDrop), hi);
            rawDrop = Math.Clamp(want, lo, hi);
        }
        else
        {
            this.ClearGather();
        }

        this.smoothDrop = Toward(this.smoothDrop, rawDrop, Ease(dt, c.PelvisTau));
        var applied = this.smoothDrop * this.blend;
        snap.RawDrop = rawDrop;
        snap.SmoothDrop = this.smoothDrop;
        snap.Applied = applied;
        this.bodyShift = Toward(this.bodyShift, bodyDesired, Ease(dt, bodyDesired.LengthSquared() > this.bodyShift.LengthSquared() ? c.GatherTauIn : c.GatherTauOut));
        snap.BodyShift = this.bodyShift;
        // Blended like the drop is: without that factor, closing the gate leaves the body shifted sideways for good.
        var bodyWorld = active ? Vector3.Transform(f.Scale * this.bodyShift, f.Rot) * this.blend : Vector3.Zero;
        this.ApplyPelvis(chr, new Vector3(bodyWorld.X, applied, bodyWorld.Z));

        if (active && this.blend > 0)
        {
            for (var s = 0; s < 2; s++)
            {
                ref var foot = ref Foot(ref snap, s);
                this.PlaceFoot(ref f, s, ref foot, applied);
            }
        }

        this.LeanSpine(ref f, ref snap, active);
        this.TiltBody(ref f, ref snap, poseOk);
        this.Snap = snap;
    }

    private static ref FootSnapshot Foot(ref Snapshot snap, int s)
    {
        if (s == 0)
        {
            return ref snap.Left;
        }

        return ref snap.Right;
    }

    private void ClearGather()
    {
        Array.Clear(this.gatherShift);
        Array.Clear(this.wallShift);
        Array.Clear(this.latched);
    }

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
        f.PosAnchor = new Vector3(logical.X, f.Pos.Y - this.written, logical.Z);
        f.OriginY = f.PosAnchor.Y;
        f.Bones = f.Pose->ModelPose.Data;

        f.RayUp = c.RayUpFrac * f.LegLen * f.Scale.Y;
        f.RayDown = c.RayDownFrac * f.LegLen * f.Scale.Y;
        f.MaxDist = f.RayUp + f.RayDown;
        f.ProbeTop = f.OriginY + f.RayUp;
        // Gather search radius, not tied to the stance: a smaller minimum stance must bring the feet closer, not shorten
        // the search. Wide enough for the far ankle of a character standing at the very edge of its collision capsule.
        f.Reach = 0.85f * f.LegLen * f.Scale.Y;
    }

    // The ground under the body itself, the baseline for every step threshold and for the probe window. An animation
    // that carries the root far from the origin, a dance travelling up a slope, stands the feet on ground far above or
    // below the logical position; measured from there every foot read as a step too tall, or an edge.
    private void ProbeBase(ref Frame f)
    {
        var hips = (Bones.Pos(in f.Bones[this.chain.Left.Hip]) + Bones.Pos(in f.Bones[this.chain.Right.Hip])) * 0.5f;
        var at = f.PosAnchor + Vector3.Transform(f.Scale * new Vector3(hips.X, 0f, hips.Z), f.Rot);
        if (!this.TryGround(at, in f, out _, out var y))
        {
            // Nothing in the window means the body has climbed past it or hangs over a void. Look again from as high as
            // the character needs for headroom, no higher, so a bridge overhead is never taken for the floor.
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

    // Three probes along the sole, cast from character height so a foot against a step riser does not see the step top.
    // Taking the median keeps a foot on the tread it mostly stands on, and lets a toe over the next step clip the riser
    // as it does in the base game, instead of floating the whole foot.
    private void ProbeFoot(ref Frame f, int s, ref FootSnapshot foot)
    {
        var side = this.chain.Side(s);
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
        foot.BelowLevel = foot.GroundModelY - f.BaseY < -f.StepTol;
    }

    private const int Steps = 20;
    private const int MaxDirs = 16;
    private const int MaxSpots = (MaxDirs * Steps) + 1;

    private int Dirs => 4 * Math.Clamp(this.Settings.GatherPrecision, 1, MaxDirs / 4);

    // Where the feet stand, chosen together: rays fan out from each ankle, every standable sample is a candidate, and
    // the pair that moves the feet least while a stance apart and uncrossed wins, so side by side or one behind the
    // other falls out of the ground's shape. Searched from the ankles, not the origin: the collision capsule is far wider
    // than a guardrail, so the character can stand with its position off the support entirely.
    private void GatherFeet(ref Frame f, ref Snapshot snap, scoped Span<Vector3> desired)
    {
        var c = this.Settings;
        desired.Clear();
        // Not while the gate is closed either: the game flags a fall as jumping, and a character in the air has nothing to
        // gather onto. Picking targets on the ledge it just left would drag the feet, and the body with them, back to it
        // for as long as the blend takes to fade. Not during emotes either: a dance stance gathered onto a rail reads wrong.
        if (!c.GatherFeet || !snap.Gate || snap.Mode != CharacterModes.Normal)
        {
            this.latched[0] = false;
            this.latched[1] = false;
            return;
        }

        // A foot over ground a tread or more below the character is over a drop, unless the character is on an incline and
        // that is simply where the slope or the stair puts a leading foot: beside a rail or a curb it moves onto the level
        // support like a foot over a void; on a hill it reaches for the ground as before. The exemption covers a foot far
        // over a drop too: running downhill the leading foot swings out over ground well below any threshold, and gathering
        // it back uphill every stride reads as a stumble. Only a foot over nothing at all gathers regardless.
        var low = snap.Left.OverEdge || snap.Left.BelowLevel || snap.Right.OverEdge || snap.Right.BelowLevel;
        var incline = low && (Sloped(in snap.Left) || Sloped(in snap.Right) || this.OnIncline(in f));
        Span<bool> needs = stackalloc bool[2];
        needs[0] = !snap.Left.Hit || ((snap.Left.OverEdge || snap.Left.BelowLevel) && !incline);
        needs[1] = !snap.Right.Hit || ((snap.Right.OverEdge || snap.Right.BelowLevel) && !incline);
        if (!needs[0] && !needs[1])
        {
            this.latched[0] = false;
            this.latched[1] = false;
            return;
        }

        var minSep = 2f * c.MinStanceFrac * f.LegLen * f.Scale.Y;
        var footLen = this.FootLength(in snap, in f) * f.Scale.Y;
        // Crossing is judged against the character's own right, not the animated stance: a turning animation swings the
        // ankles past each other, and a rule tied to them re-plants the feet several times in one turn.
        var side = Vector3.Transform(this.chain.BindSide, f.Rot);
        var fwd = Vector3.Transform(this.chain.BindForward, f.Rot);

        Span<Vector3> spots = stackalloc Vector3[2 * MaxSpots];
        Span<float> costs = stackalloc float[2 * MaxSpots];
        Span<int> lines = stackalloc int[2 * MaxSpots];
        Span<int> count = stackalloc int[2];
        Span<bool> held = stackalloc bool[2];
        Span<bool> searched = stackalloc bool[2];
        Span<bool> level = stackalloc bool[2];
        held[0] = this.Hold(ref f, 0);
        held[1] = this.Hold(ref f, 1);
        // A pair held through a turn re-plants only once the turn has crossed the legs, and a little past that so a stance
        // facing along a beam, where the feet stand in line, does not flicker between the two ways round; or once the
        // boots would clearly overlap.
        if (held[0] && held[1])
        {
            var heldSep = new Vector3(this.latchTarget[1].X - this.latchTarget[0].X, 0f, this.latchTarget[1].Z - this.latchTarget[0].Z);
            var lat = Vector3.Dot(heldSep, side);
            if (lat < -0.3f * minSep || (lat < 0.7f * minSep && MathF.Abs(Vector3.Dot(heldSep, fwd)) < 0.7f * footLen))
            {
                held[0] = false;
                held[1] = false;
            }
        }

        for (var s = 0; s < 2; s++)
        {
            ref var foot = ref Foot(ref snap, s);
            var at = s * MaxSpots;
            if (held[s] || !needs[s])
            {
                spots[at] = held[s] ? this.latchTarget[s] : foot.AnkleWorld;
                costs[at] = 0f;
                lines[at] = -1;
                count[s] = 1;
            }
            else
            {
                // Level ground first; only a foot over a void with none in reach may take lower ground it could step down to.
                level[s] = true;
                count[s] = this.Sample(ref f, s, in foot, true, false, spots.Slice(at, MaxSpots), costs.Slice(at, MaxSpots), lines.Slice(at, MaxSpots));
                if (count[s] == 0 && foot.OverEdge)
                {
                    level[s] = false;
                    count[s] = this.Sample(ref f, s, in foot, false, false, spots.Slice(at, MaxSpots), costs.Slice(at, MaxSpots), lines.Slice(at, MaxSpots));
                }

                searched[s] = true;
            }
        }

        var found = Solver.Pick(spots[..count[0]], costs[..count[0]], spots.Slice(MaxSpots, count[1]), costs.Slice(MaxSpots, count[1]), minSep, footLen, side, fwd, out var iL, out var iR, out var apart);
        // Nothing a stance apart from a foot that kept its spot: let it move too, its own spot still free.
        if (found && !apart && (!searched[0] || !searched[1]))
        {
            for (var s = 0; s < 2; s++)
            {
                if (!searched[s])
                {
                    ref var foot = ref Foot(ref snap, s);
                    var at = (s * MaxSpots) + 1;
                    level[s] = true;
                    count[s] += this.Sample(ref f, s, in foot, true, !needs[s], spots.Slice(at, MaxSpots - 1), costs.Slice(at, MaxSpots - 1), lines.Slice(at, MaxSpots - 1));
                }
            }

            found = Solver.Pick(spots[..count[0]], costs[..count[0]], spots.Slice(MaxSpots, count[1]), costs.Slice(MaxSpots, count[1]), minSep, footLen, side, fwd, out iL, out iR, out apart);
        }

        Span<Vector3> target = stackalloc Vector3[2];
        Span<bool> free = stackalloc bool[2];
        Span<bool> place = stackalloc bool[2];
        for (var s = 0; s < 2; s++)
        {
            ref var foot = ref Foot(ref snap, s);
            var i = s == 0 ? iL : iR;
            if (!found || i < 0)
            {
                foot.GatherBlock = needs[s] ? Block.NoEdge : Block.None;
                this.latched[s] = false;
                continue;
            }

            var kept = i == 0 && (held[s] || !needs[s]);
            var at = (s * MaxSpots) + i;
            target[s] = spots[at];
            if (lines[at] >= 0 && this.Recentre(foot.AnkleWorld, lines[at], in f, level[s], out var mid, out _))
            {
                target[s] = mid;
            }

            // A foot standing on its own ground is not gathered; it still anchors the other's spacing.
            free[s] = !kept;
            place[s] = held[s] || !kept;
            if (!place[s])
            {
                this.latched[s] = false;
            }
        }

        this.Tighten(target, free, level, minSep, footLen, side, fwd, in f);

        for (var s = 0; s < 2; s++)
        {
            if (!place[s])
            {
                continue;
            }

            ref var foot = ref Foot(ref snap, s);
            if (!this.TrySupport(target[s], in f, level[s], out var support))
            {
                foot.GatherBlock = Block.NoEdge;
                this.latched[s] = false;
                continue;
            }

            if (free[s])
            {
                this.latchTarget[s] = target[s];
                this.latched[s] = true;
            }

            foot.GatherBlock = apart ? Block.None : Block.Squeezed;
            var targetModel = Vector3.Transform(new Vector3(target[s].X - f.PosAnchor.X, 0f, target[s].Z - f.PosAnchor.Z), Quaternion.Inverse(f.Rot)) / f.Scale;
            foot.HitPoint = support.Point;
            foot.Material = support.Material;
            foot.GroundModelY = (GroundAt(in support, target[s].X, target[s].Z, out foot.HitNormal) - f.OriginY) / f.Scale.Y;
            foot.Hit = true;
            foot.OverEdge = false;
            foot.Gathered = true;
            desired[s] = new Vector3(targetModel.X - foot.AnkleModel.X, 0f, targetModel.Z - foot.AnkleModel.Z);
        }
    }

    // The hit triangle leans more than about ten degrees: a hill, a ramp, or a staircase whose collision is one.
    private static bool Sloped(in FootSnapshot foot) => foot.Hit && foot.HitNormal.Y < 0.98f;

    // Up or down a stair or a slope, the ground half a leg ahead of the character and half a leg behind differ by more
    // than a tread. Along a rail or a curb they match, whether both land on the rail or both on the deck below it.
    private bool OnIncline(in Frame f)
    {
        var dir = f.VelDir == Vector3.Zero ? Vector3.Transform(this.chain.BindForward, f.Rot) : f.VelDir;
        dir.Y = 0f;
        if (dir.LengthSquared() < 1e-6f)
        {
            return false;
        }

        dir = Vector3.Normalize(dir) * (0.5f * f.LegLen * f.Scale.Y);
        return this.TryGround(f.PosAnchor + dir, in f, out _, out var ahead)
            && this.TryGround(f.PosAnchor - dir, in f, out _, out var behind)
            && MathF.Abs(ahead - behind) > f.StepTol * f.Scale.Y;
    }

    // Candidates lie on a fan of lines, so on a thin support far from the ankle they are spaced out along it and the
    // cheapest fitting pair can be much wider than it needs to be. Both spots stand on the support, and on anything
    // straight so does the line between them: draw the movable feet together along it to the width that just fits.
    private void Tighten(scoped Span<Vector3> target, scoped ReadOnlySpan<bool> free, scoped ReadOnlySpan<bool> level, float minSep, float footLen, Vector3 side, Vector3 fwd, in Frame f)
    {
        if (!free[0] && !free[1])
        {
            return;
        }

        var sep = new Vector3(target[1].X - target[0].X, 0f, target[1].Z - target[0].Z);
        var dist = sep.Length();
        if (dist < 1e-4f)
        {
            return;
        }

        var u = sep / dist;
        var bySide = MathF.Abs(Vector3.Dot(u, side));
        var byFwd = MathF.Abs(Vector3.Dot(u, fwd));
        var need = MathF.Min(bySide > 1e-3f ? minSep / bySide : float.MaxValue, byFwd > 1e-3f ? footLen / byFwd : float.MaxValue);
        var excess = dist - need;
        if (excess <= 0.01f * f.LegLen * f.Scale.Y)
        {
            return;
        }

        var share = free[0] && free[1] ? 0.5f : 1f;
        for (var half = 1f; half >= 0.25f; half *= 0.5f)
        {
            var move = excess * share * half;
            var l = free[0] ? target[0] + (u * move) : target[0];
            var r = free[1] ? target[1] - (u * move) : target[1];
            if ((!free[0] || this.TrySupport(l, in f, level[0], out _)) && (!free[1] || this.TrySupport(r, in f, level[1], out _)))
            {
                target[0] = l;
                target[1] = r;
                return;
            }
        }
    }

    // While stationary, hold the world target chosen earlier so the stop animation's stance change, or a turn on the
    // spot, is absorbed by the IK instead of dragging the target. A turning character swings each ankle through a wide
    // arc, so the hold is judged from the hip, which barely moves.
    private bool Hold(ref Frame f, int s)
    {
        if (!f.Still || !this.latched[s])
        {
            return false;
        }

        var hipWorld = f.PosAnchor + Vector3.Transform(f.Scale * Bones.Pos(in f.Bones[this.chain.Side(s).Hip]), f.Rot);
        var reach = new Vector3(this.latchTarget[s].X - hipWorld.X, 0f, this.latchTarget[s].Z - hipWorld.Z).Length();
        return reach < f.Reach && this.TrySupport(this.latchTarget[s], in f, out _);
    }

    // Lines are numbered direction * 64 + sample, so one int names a spot.
    private Vector3 SearchDir(int line, in Frame f)
    {
        var ang = f.Yaw + (MathF.Tau * (line / 64) / this.Dirs);
        return new Vector3(MathF.Sin(ang), 0f, MathF.Cos(ang));
    }

    // Standable samples along lines fanned out from the ankle, each line cut where ground too high to walk through
    // begins. A sample beside an edge costs a whole reach extra, so a foot settles one step in wherever the support is
    // wide enough; one with an edge on both sides is marked for recentring. Costs also favour the previous target, so a
    // moving pattern does not hop between equally good spots. With `levelOnly`, only ground at the level the character
    // stands on counts, for a foot that has lower ground to fall back on; `ankleOk` says whether where the ankle already
    // is counts as ground.
    private int Sample(ref Frame f, int s, in FootSnapshot foot, bool levelOnly, bool ankleOk, scoped Span<Vector3> spots, scoped Span<float> costs, scoped Span<int> lines)
    {
        var dirs = this.Dirs;
        var step = f.Reach / Steps;
        Span<bool> ok = stackalloc bool[Steps + 2];
        var n = 0;
        for (var d = 0; d < dirs; d++)
        {
            var dir = this.SearchDir(d * 64, in f);
            ok.Clear();
            ok[0] = ankleOk;
            ok[Steps + 1] = true;
            var last = Steps;
            for (var k = 1; k <= Steps; k++)
            {
                var ground = this.ProbeSupport(foot.AnkleWorld + (dir * (step * k)), in f, out _, out var gy);
                if (ground == Ground.TooHigh)
                {
                    last = k - 1;
                    break;
                }

                ok[k] = ground == Ground.Standable && (!levelOnly || MathF.Abs(gy) <= f.StepTol);
            }

            for (var k = 1; k <= last; k++)
            {
                if (!ok[k])
                {
                    continue;
                }

                var spot = foot.AnkleWorld + (dir * (step * k));
                var cost = step * k * step * k;
                if (!ok[k - 1] || !ok[k + 1])
                {
                    cost += f.Reach * f.Reach;
                }

                if (this.latched[s])
                {
                    cost += new Vector3(spot.X - this.latchTarget[s].X, 0f, spot.Z - this.latchTarget[s].Z).LengthSquared();
                }

                spots[n] = spot;
                costs[n] = cost;
                lines[n] = !ok[k - 1] && !ok[k + 1] ? (d * 64) + k : -1;
                n++;
            }
        }

        return n;
    }

    // A sample with no standable neighbour on its line stands on something narrower than the spacing, a guardrail for
    // one, and may sit at its very edge. Find both edges and stand in the middle.
    private bool Recentre(Vector3 ankle, int line, in Frame f, bool level, out Vector3 mid, out RaycastHit hit)
    {
        var dir = this.SearchDir(line, in f);
        var step = f.Reach / Steps;
        var k = line % 64;
        var near = this.Edge(ankle, dir, step * (k - 1), step * k, in f, level);
        var far = this.Edge(ankle, dir, step * (k + 1), step * k, in f, level);
        mid = ankle + (dir * ((near + far) * 0.5f));
        return this.TrySupport(mid, in f, level, out hit);
    }

    // Where standable ground begins along dir, between `off` (not standable) and `on` (standable).
    private float Edge(Vector3 from, Vector3 dir, float off, float on, in Frame f, bool level)
    {
        for (var i = 0; i < 3; i++)
        {
            var m = (off + on) * 0.5f;
            if (this.TrySupport(from + (dir * m), in f, level, out _))
            {
                on = m;
            }
            else
            {
                off = m;
            }
        }

        return (off + on) * 0.5f;
    }

    private void ArrangeStance(ref Frame f, ref Snapshot snap, scoped Span<Vector3> desired, out Vector3 bodyDesired)
    {
        var c = this.Settings;
        bodyDesired = Vector3.Zero;
        if (!snap.Left.Gathered && !snap.Right.Gathered)
        {
            return;
        }

        // The pair was picked apart, but the wall push and recentring since may have closed it: this has the final say.
        // Boots side by side need a stance between them, one behind the other a boot length; take the smaller move,
        // uncrossing first if it comes to lengthwise.
        var minSep = 2f * c.MinStanceFrac * f.LegLen;
        var footLen = this.FootLength(in snap, in f);
        var side = this.chain.BindSide;
        var fwd = this.chain.BindForward;
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

        if (this.moveHook == null)
        {
            return;
        }

        // Stand over the feet: the body moves so the bind-pose stance centre sits over the midpoint of the final targets.
        // Judged from the bind pose rather than the animated ankles so that neither idle sway nor a stepping turn moves
        // the body, and a target latched mid-step does not leave it standing off to one side. A planted foot's residual
        // then cancels the body move so it stays put in the world; a gathered foot keeps only the stance change.
        var mid = (desired[0] + desired[1]) * 0.5f;
        var ankleMid = (snap.Left.AnkleModel + snap.Right.AnkleModel) * 0.5f;
        bodyDesired = mid + new Vector3(ankleMid.X, 0f, ankleMid.Z) - this.chain.BindMid;
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

    // Keeps the boot out of walls wherever it stands, gathering or not. Runs before the stance rule so that rule keeps
    // the final say: a wall pushing one foot toward the other must not leave them stacked. The box is tested where the
    // foot will actually be, body and gather shifts included, and the push is recomputed from the animation every frame
    // rather than accumulated, so it cannot drift. Weighted by planted so a foot lifting off near a wall lets go smoothly.
    private void PushFromWalls(ref Frame f, int s, ref FootSnapshot foot)
    {
        var c = this.Settings;
        ref var wall = ref this.wallShift[s];

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
                var box = new FootBox(toeLen + half, half, half, foot.Rest * f.Scale.Y * 0.6f);
                var placed = foot.AnkleWorld + Vector3.Transform(f.Scale * (this.gatherShift[s] + this.bodyShift) * this.blend, f.Rot);
                var groundY = f.OriginY + (foot.GroundModelY * f.Scale.Y);
                var pushWorld = this.WallPush(placed, facing, in box, groundY, in f);
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
        var side = this.chain.Side(s);
        ref var shift = ref this.gatherShift[s];
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

        // Terrain and the animation's own lift are kept apart: the pelvis follows terrain only, so a run's flight phase
        // keeps its bounce instead of being pulled to the floor.
        foot.Delta = foot.GroundModelY + foot.Rest - foot.AnkleModel.Y;
        if (foot.Planted > 0)
        {
            // Where the terrain under this foot asks the body to sit. Averaged over the planted feet, this shares a
            // height difference between the legs instead of leaving one straight and folding the other.
            body.WantSum += foot.GroundModelY * foot.Planted;
            body.WantWeight += foot.Planted;

            // The body cannot rise past the point where this leg can no longer reach its ground, nor sink past the point
            // where this knee folds tighter than allowed. A partly weighted foot only constrains in proportion.
            var reachLimit = foot.MaxExtend + foot.GroundModelY;
            var kneeLimit = foot.GroundModelY - foot.MaxRaiseByKnee;
            body.Hi = MathF.Min(body.Hi, reachLimit < 0 ? reachLimit * foot.Planted : reachLimit);
            body.Lo = MathF.Max(body.Lo, kneeLimit > 0 ? kneeLimit * foot.Planted : kneeLimit);
        }

        if (foot.Gathered)
        {
            // Stand tall on a narrow support: spend part of the legs' remaining extension on height. Part of the leg with the
            // least to spare: a preference must never drive the other leg to its limit, where a foot on tiptoe beside a
            // flat one would leave the flat foot straight-legged and floating.
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

        // Shifting by the terrain height under the foot, minus what the pelvis already took, keeps the clearance the
        // animation gave it over flat ground.
        var slopeTan = foot.HitNormal.Y > 0.2f ? MathF.Sqrt(MathF.Max(0f, 1f - foot.HitNormal.Y * foot.HitNormal.Y)) / foot.HitNormal.Y : 0f;
        var offset = foot.GroundModelY - applied + MathF.Max(0f, foot.Rest - foot.AnkleModel.Y) + c.SlopeLiftFrac * f.LegLen * slopeTan;
        offset = offset < 0
            ? MathF.Max(offset * foot.Planted, -foot.MaxExtend)
            : MathF.Min(offset, MathF.Min(f.MaxRaise, foot.MaxRaiseByKnee));
        foot.Offset = offset * this.blend;

        // Tilt only while the sole is at ground level: a heel-striking or lifting foot keeps the animation's own pitch,
        // otherwise uphill running bends the toes up twice.
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
                angle = MathF.Min(angle, c.MaxAnkleAngleDeg * MathF.PI / 180f) * foot.Contact * this.blend;
                tilt = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);
                foot.TiltDeg = angle * 180f / MathF.PI;
            }
        }

        // Gathered feet yaw toward the bind forward: Hrothgar knees splay outward, which reads wrong once the feet are
        // together. Only with the sole down: yawing a foot poised on its toes bends it inward.
        var gatherBlend = Math.Clamp(foot.GatherShift.Length() / MathF.Max(0.05f * f.LegLen, 1e-3f), 0f, 1f) * c.GatherForward * foot.Contact * this.blend;
        var ankleDelta = tilt;
        if (gatherBlend > 0.001f)
        {
            var footFwd = Bones.Pos(in f.Bones[this.chain.Side(s).Toes]) - foot.AnkleModel;
            footFwd.Y = 0f;
            if (footFwd.LengthSquared() > 1e-6f)
            {
                footFwd = Vector3.Normalize(footFwd);
                var fwd = this.chain.BindForward;
                var ang = MathF.Atan2(Vector3.Cross(footFwd, fwd).Y, Vector3.Dot(footFwd, fwd));
                ankleDelta = Quaternion.Normalize(tilt * Quaternion.CreateFromAxisAngle(Vector3.UnitY, ang * gatherBlend));
                foot.YawDeg = ang * gatherBlend * 180f / MathF.PI;
            }
        }

        var eps = 0.003f * f.LegLen;
        if (MathF.Abs(foot.Offset) > eps || foot.TiltDeg > 0.1f || foot.GatherShift.LengthSquared() > eps * eps || foot.WallShift.LengthSquared() > eps * eps)
        {
            var ikTarget = foot.AnkleModel + ((foot.GatherShift + foot.WallShift) * this.blend) + new Vector3(0, foot.Offset, 0);
            foot.IkTargetWorld = f.Pos + Vector3.Transform(f.Scale * ikTarget, f.Rot);
            foot.Solved = SolveLeg(f.Pose, this.chain.Side(s), ikTarget, ankleDelta, this.chain.BindForward, gatherBlend);
        }
    }

    // Seated on the ground the body rests on the slope instead of standing on it: the whole pose turns about the origin
    // so its up matches the ground plane, read from four probes half a leg length out. Weighted by how far down the body
    // is, since the sit-down animation starts upright and an upright body tilted to the slope reads as falling over.
    private void TiltBody(ref Frame f, ref Snapshot snap, bool poseOk)
    {
        var c = this.Settings;
        var target = Quaternion.Identity;
        if (poseOk && snap.Sitting && c.MaxSitTiltDeg > 0f)
        {
            var r = 0.5f * f.LegLen * f.Scale.Y;
            var fwd = new Vector3(MathF.Sin(f.Yaw), 0f, MathF.Cos(f.Yaw));
            var side = new Vector3(fwd.Z, 0f, -fwd.X);
            if (this.TryGround(f.PosAnchor + (fwd * r), in f, out _, out var ahead) && this.TryGround(f.PosAnchor - (fwd * r), in f, out _, out var behind)
                && this.TryGround(f.PosAnchor + (side * r), in f, out _, out var right) && this.TryGround(f.PosAnchor - (side * r), in f, out _, out var left))
            {
                var hipY = (Bones.Pos(in f.Bones[this.chain.Left.Hip]).Y + Bones.Pos(in f.Bones[this.chain.Right.Hip]).Y) * 0.5f;
                var low = Math.Clamp((0.5f * f.LegLen - hipY) / (0.25f * f.LegLen), 0f, 1f);
                var full = Solver.GroundTilt(ahead, behind, right, left, r, fwd, side, f.Rot, c.MaxSitTiltDeg * MathF.PI / 180f);
                target = Quaternion.Slerp(Quaternion.Identity, full, low);
            }
        }

        this.tilt = Quaternion.Slerp(this.tilt, target, Ease(f.Dt, c.SitTiltTau));
        snap.SitTiltDeg = 2f * MathF.Acos(MathF.Min(MathF.Abs(this.tilt.W), 1f)) * 180f / MathF.PI;
        if (poseOk && snap.SitTiltDeg > 0.05f)
        {
            RotateBody(f.Skel, f.Pose, this.tilt);
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
                var lean = slopeAngle * (slopeAngle >= 0 ? c.LeanUphillGain : c.LeanDownhillGain) * Math.Clamp(f.Speed / 6f, 0f, 1f);
                var cap = MathF.Abs(c.MaxLeanDeg) * MathF.PI / 180f;
                leanTarget = Math.Clamp(lean, -cap, cap);
            }
        }

        this.lean = Toward(this.lean, leanTarget, Ease(f.Dt, c.LeanTau));
        snap.LeanDeg = this.lean * 180f / MathF.PI;
        if (!active || this.chain.SpineSub.Length == 0)
        {
            return;
        }

        // The lean is additive on the animated spine, so measure the animated pitch first and use only the room left
        // between the floor and the cap. A male Hrothgar idles already well pitched forward; an upright race leaning back
        // downhill must not end up arched past vertical.
        var spineDir = Bones.Pos(in f.Bones[this.chain.Neck]) - Bones.Pos(in f.Bones[this.chain.SpineA]);
        var pitch = MathF.Atan2(Vector3.Dot(spineDir, this.chain.BindForward), spineDir.Y);
        snap.SpinePitchDeg = pitch * 180f / MathF.PI;
        var leanApplied = this.lean * this.blend;
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
            this.ApplySpineLean(f.Skel, f.Pose, leanApplied);
        }
    }
}
