using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace FootIk;

public sealed unsafe partial class Plugin
{
    private const int Steps = 20;
    private const int MaxDirs = 16;
    private const int MaxSpots = (MaxDirs * Steps) + 1;

    // Where the feet stand, chosen together: rays fan out from each ankle, every standable sample is a candidate, and
    // the cheapest pair a stance apart and uncrossed wins. Searched from the ankles, not the origin: the collision
    // capsule is far wider than a guardrail, so the character can stand with its position off the support entirely.
    private void GatherFeet(ref Frame f, ref Snapshot snap, scoped Span<Vector3> desired)
    {
        var c = this.Settings;
        desired.Clear();
        // Not while the gate is closed: the game flags a fall as jumping, and targets on the ledge just left would drag
        // the feet back to it. Not during emotes either: a dance stance gathered onto a rail reads wrong.
        if (!c.GatherFeet || f.Dirs == 0 || !snap.Gate || snap.Mode != CharacterModes.Normal)
        {
            snap.Left.GatherBlock = Block.Off;
            snap.Right.GatherBlock = Block.Off;
            f.St.Latched[0] = false;
            f.St.Latched[1] = false;
            return;
        }

        // A foot over a drop past Edge drop gathers, unless it is on an incline, where that is just where a slope puts a
        // leading foot. A stride hangs one foot, never both, so two hanging feet gather whatever the ground below leans,
        // and so does a foot over nothing. Ground within Edge drop is stood on, a stair tread included. `OnIncline`
        // cannot tell a slope from a ledge, so it is asked only in motion; standing at an edge, the hit triangles decide.
        var oneLow = snap.Left.OverEdge != snap.Right.OverEdge;
        var incline = oneLow && (Sloped(in snap.Left) || Sloped(in snap.Right) || (!f.Still && this.OnIncline(in f)));
        Span<bool> needs = stackalloc bool[2];
        needs[0] = !snap.Left.Hit || (snap.Left.OverEdge && !incline);
        needs[1] = !snap.Right.Hit || (snap.Right.OverEdge && !incline);
        if (!needs[0] && !needs[1])
        {
            snap.Left.GatherBlock = Block.Settled;
            snap.Right.GatherBlock = Block.Settled;
            f.St.Latched[0] = false;
            f.St.Latched[1] = false;
            return;
        }

        var minSep = 2f * c.MinStanceFrac * f.LegLen * f.Scale.Y;
        var footLen = this.FootLength(in snap, in f) * f.Scale.Y;
        // Crossing is judged against the character's own right, not the animated stance: a turn swings the ankles past
        // each other, and a rule tied to them re-plants the feet several times in one turn.
        var side = Vector3.Transform(f.St.Chain.BindSide, f.Rot);
        var fwd = Vector3.Transform(f.St.Chain.BindForward, f.Rot);

        Span<Vector3> spots = stackalloc Vector3[2 * MaxSpots];
        Span<float> costs = stackalloc float[2 * MaxSpots];
        Span<int> lines = stackalloc int[2 * MaxSpots];
        Span<int> count = stackalloc int[2];
        Span<bool> held = stackalloc bool[2];
        Span<bool> searched = stackalloc bool[2];
        Span<bool> level = stackalloc bool[2];
        held[0] = this.Hold(ref f, 0);
        held[1] = this.Hold(ref f, 1);
        // A held pair re-plants only once a turn has crossed the legs, and a little past that so a stance in line along
        // a beam does not flicker between the two ways round; or once the boots would clearly overlap.
        if (held[0] && held[1])
        {
            var heldSep = new Vector3(f.St.LatchTarget[1].X - f.St.LatchTarget[0].X, 0f, f.St.LatchTarget[1].Z - f.St.LatchTarget[0].Z);
            var lat = Vector3.Dot(heldSep, side);
            if (lat < -0.3f * minSep || (lat < 0.7f * minSep && MathF.Abs(Vector3.Dot(heldSep, fwd)) < 0.7f * footLen))
            {
                held[0] = false;
                held[1] = false;
            }
        }

        // Where the body would stand for the stance to look centred, and where each foot stays if it does not search,
        // so a searching foot can aim opposite the one that stays: a pair centred here needs no body move onto it.
        var centre = f.PosAnchor + Vector3.Transform(f.Scale * f.St.Chain.BindMid, f.Rot);
        Span<Vector3> keptAt = stackalloc Vector3[2];
        keptAt[0] = held[0] ? f.St.LatchTarget[0] : snap.Left.AnkleWorld;
        keptAt[1] = held[1] ? f.St.LatchTarget[1] : snap.Right.AnkleWorld;

        for (var s = 0; s < 2; s++)
        {
            ref var foot = ref Foot(ref snap, s);
            var at = s * MaxSpots;
            if (held[s] || !needs[s])
            {
                spots[at] = keptAt[s];
                costs[at] = 0f;
                lines[at] = -1;
                count[s] = 1;
            }
            else
            {
                // Level ground first; only a foot over a void with none in reach may take lower ground it could step down to.
                var aim = held[1 - s] || !needs[1 - s] ? (2f * centre) - keptAt[1 - s] : centre;
                level[s] = true;
                count[s] = this.Sample(ref f, s, in foot, aim, true, false, spots.Slice(at, MaxSpots), costs.Slice(at, MaxSpots), lines.Slice(at, MaxSpots));
                if (count[s] == 0 && foot.OverEdge)
                {
                    level[s] = false;
                    count[s] = this.Sample(ref f, s, in foot, aim, false, false, spots.Slice(at, MaxSpots), costs.Slice(at, MaxSpots), lines.Slice(at, MaxSpots));
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
                    var oi = s == 0 ? iR : iL;
                    var aim = oi >= 0 ? (2f * centre) - spots[((1 - s) * MaxSpots) + oi] : centre;
                    level[s] = true;
                    count[s] += this.Sample(ref f, s, in foot, aim, true, !needs[s], spots.Slice(at, MaxSpots - 1), costs.Slice(at, MaxSpots - 1), lines.Slice(at, MaxSpots - 1));
                }
            }

            found = Solver.Pick(spots[..count[0]], costs[..count[0]], spots.Slice(MaxSpots, count[1]), costs.Slice(MaxSpots, count[1]), minSep, footLen, side, fwd, out iL, out iR, out apart);
        }

        Span<Vector3> target = stackalloc Vector3[2];
        Span<bool> free = stackalloc bool[2];
        Span<bool> place = stackalloc bool[2];
        Span<bool> narrow = stackalloc bool[2];
        for (var s = 0; s < 2; s++)
        {
            ref var foot = ref Foot(ref snap, s);
            var i = s == 0 ? iL : iR;
            if (!found || i < 0)
            {
                foot.GatherBlock = needs[s] ? Block.NoEdge : Block.Settled;
                f.St.Latched[s] = false;
                continue;
            }

            var kept = i == 0 && (held[s] || !needs[s]);
            var at = (s * MaxSpots) + i;
            target[s] = spots[at];
            // A kept spot carries no line, so it carries the answer it was latched with instead; recentring needs a real
            // line, and a negative one would search along the character's facing from behind the ankle.
            narrow[s] = kept ? f.St.LatchNarrow[s] : lines[at] >= 0;
            if (lines[at] >= 0 && this.Recentre(foot.AnkleWorld, lines[at], in f, level[s], out var mid))
            {
                target[s] = mid;
            }

            // A foot standing on its own ground is not gathered; it still anchors the other's spacing.
            free[s] = !kept;
            place[s] = held[s] || !kept;
            if (!place[s])
            {
                f.St.Latched[s] = false;
            }
        }

        // Not in platforming mode: with one foot staying put, narrowing moves only the other and walks the pair off centre.
        if (!c.GatherToPosition)
        {
            this.Tighten(target, free, level, minSep, footLen, side, fwd, in f);
        }

        // A stance no body could take is a sign the inputs are wrong, not a pose to strike: the body would be carried
        // metres along with it. The comparison is written so a NaN fails it too.
        Span<Vector3> stand = stackalloc Vector3[2];
        stand[0] = found && iL >= 0 ? target[0] : snap.Left.AnkleWorld;
        stand[1] = found && iR >= 0 ? target[1] : snap.Right.AnkleWorld;
        var legWorld = f.LegLen * f.Scale.Y;
        var spread = new Vector3(stand[1].X - stand[0].X, 0f, stand[1].Z - stand[0].Z).Length();
        var shift = new Vector3(((stand[0].X + stand[1].X) * 0.5f) - centre.X, 0f, ((stand[0].Z + stand[1].Z) * 0.5f) - centre.Z).Length();
        if (!(spread <= c.MaxStanceFrac * legWorld && shift <= c.MaxBodyShiftFrac * legWorld))
        {
            snap.Left.GatherBlock = Block.TooFar;
            snap.Right.GatherBlock = Block.TooFar;
            f.St.Latched[0] = false;
            f.St.Latched[1] = false;
            return;
        }

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
                f.St.Latched[s] = false;
                continue;
            }

            if (free[s])
            {
                f.St.LatchTarget[s] = target[s];
                f.St.LatchNarrow[s] = narrow[s];
                f.St.Latched[s] = true;
            }

            foot.GatherBlock = apart ? Block.None : Block.Squeezed;
            var targetModel = Vector3.Transform(new Vector3(target[s].X - f.PosAnchor.X, 0f, target[s].Z - f.PosAnchor.Z), Quaternion.Inverse(f.Rot)) / f.Scale;
            foot.HitPoint = support.Point;
            foot.Material = support.Material;
            foot.GroundModelY = (GroundAt(in support, target[s].X, target[s].Z, out foot.HitNormal) - f.OriginY) / f.Scale.Y;
            // A sole across a rail rests level: on a rounded or diamond rail the facet under one point is not what it stands on.
            if (narrow[s])
            {
                foot.HitNormal = Vector3.UnitY;
            }

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
        var dir = f.VelDir == Vector3.Zero ? Vector3.Transform(f.St.Chain.BindForward, f.Rot) : f.VelDir;
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

    // Candidates lie on a fan of lines, so on a thin support the cheapest fitting pair can be much wider than it needs
    // to be. Both spots stand on the support, so on anything straight the line between them does too: draw them in.
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

    // While stationary, hold the world target chosen earlier so a stop animation or a turn on the spot is absorbed by
    // the IK instead of dragging the target. Judged from the hip: a turn swings the ankle through a wide arc.
    private bool Hold(ref Frame f, int s)
    {
        if (!f.Still || !f.St.Latched[s])
        {
            return false;
        }

        var hipWorld = f.PosAnchor + Vector3.Transform(f.Scale * Bones.Pos(in f.Bones[f.St.Chain.Side(s).Hip]), f.Rot);
        var reach = new Vector3(f.St.LatchTarget[s].X - hipWorld.X, 0f, f.St.LatchTarget[s].Z - hipWorld.Z).Length();
        return reach < f.Reach && this.TrySupport(f.St.LatchTarget[s], in f, false, out _);
    }

    // Lines are numbered direction * 64 + sample, so one int names a spot.
    private Vector3 SearchDir(int line, in Frame f)
    {
        var ang = f.Yaw + (MathF.Tau * (line / 64) / f.Dirs);
        return new Vector3(MathF.Sin(ang), 0f, MathF.Cos(ang));
    }

    // Standable samples along lines fanned out from the ankle, each cut where ground too high to walk through begins.
    // Cost is the move plus a reach penalty beside an edge; platforming mode costs distance from `aim`, no penalty.
    // Ties favour the previous target, or a moving pattern hops. `levelOnly`: the character's own level only;
    // `ankleOk`: where the ankle already is counts as ground.
    private int Sample(ref Frame f, int s, in FootSnapshot foot, Vector3 aim, bool levelOnly, bool ankleOk, scoped Span<Vector3> spots, scoped Span<float> costs, scoped Span<int> lines)
    {
        var dirs = f.Dirs;
        var toPos = this.Settings.GatherToPosition;
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
                var cost = toPos
                    ? new Vector3(spot.X - aim.X, 0f, spot.Z - aim.Z).LengthSquared()
                    : step * k * step * k;
                if (!toPos && (!ok[k - 1] || !ok[k + 1]))
                {
                    cost += f.Reach * f.Reach;
                }

                if (f.St.Latched[s])
                {
                    cost += new Vector3(spot.X - f.St.LatchTarget[s].X, 0f, spot.Z - f.St.LatchTarget[s].Z).LengthSquared();
                }

                spots[n] = spot;
                costs[n] = cost;
                var edgeNear = !ok[k - 1] || !ok[Math.Max(k - 2, 0)];
                var edgeFar = !ok[k + 1] || !ok[Math.Min(k + 2, Steps + 1)];
                lines[n] = edgeNear && edgeFar ? (d * 64) + k : -1;
                n++;
            }
        }

        return n;
    }

    // A sample with an edge within two steps on each side along its line stands on something about a boot wide or
    // narrower, a guardrail for one, and may sit at its very edge or on one flank of a rounded rail. Find both edges
    // and stand in the middle.
    private bool Recentre(Vector3 ankle, int line, in Frame f, bool level, out Vector3 mid)
    {
        var dir = this.SearchDir(line, in f);
        var step = f.Reach / Steps;
        var k = line % 64;
        var lo = this.TrySupport(ankle + (dir * (step * (k - 1))), in f, level, out _) ? k - 1 : k;
        var hi = this.TrySupport(ankle + (dir * (step * (k + 1))), in f, level, out _) ? k + 1 : k;
        var near = this.Edge(ankle, dir, step * (lo - 1), step * lo, in f, level);
        var far = this.Edge(ankle, dir, step * (hi + 1), step * hi, in f, level);
        mid = ankle + (dir * ((near + far) * 0.5f));
        return this.TrySupport(mid, in f, level, out _);
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
}
