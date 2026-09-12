using System;
using System.Numerics;

namespace FootIk;

public struct Xf
{
    public Vector3 T;
    public Quaternion R;
    public Vector3 S;

    public readonly bool IsFinite =>
        float.IsFinite(this.T.X) && float.IsFinite(this.T.Y) && float.IsFinite(this.T.Z) &&
        float.IsFinite(this.R.X) && float.IsFinite(this.R.Y) && float.IsFinite(this.R.Z) && float.IsFinite(this.R.W) &&
        float.IsFinite(this.S.X) && float.IsFinite(this.S.Y) && float.IsFinite(this.S.Z);
}

public static class Solver
{
    // Havok parent * local, scale included: bones carry non-uniform scale (CustomizePlus writes it).
    public static Xf Compose(in Xf p, in Xf l) => new()
    {
        T = p.T + Vector3.Transform(p.S * l.T, p.R),
        R = Quaternion.Normalize(p.R * l.R),
        S = p.S * l.S,
    };

    public static Xf Relative(in Xf p, in Xf c)
    {
        var inv = Quaternion.Inverse(p.R);
        return new Xf
        {
            T = Vector3.Transform(c.T - p.T, inv) / p.S,
            R = Quaternion.Normalize(inv * c.R),
            S = c.S / p.S,
        };
    }

    public static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        from = Vector3.Normalize(from);
        to = Vector3.Normalize(to);
        var d = Vector3.Dot(from, to);
        if (d < -0.9999f)
        {
            var axis = Vector3.Cross(Vector3.UnitX, from);
            if (axis.LengthSquared() < 1e-6f)
            {
                axis = Vector3.Cross(Vector3.UnitY, from);
            }

            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }

        var c = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1f + d));
    }

    // Model-space rotation deltas for the hip (about a) and then the knee (about the new knee), keeping the bend
    // plane. poleHint/poleBlend pull that plane toward a hint direction. False when degenerate.
    public static bool TwoBone(Vector3 a, Vector3 b, Vector3 d, Vector3 footForward, Vector3 target, Vector3 poleHint, float poleBlend, out Quaternion qA, out Quaternion qB, out Vector3 newKnee)
    {
        qA = qB = Quaternion.Identity;
        newKnee = b;

        var l1 = (b - a).Length();
        var l2 = (d - b).Length();
        var toT = target - a;
        var dist = toT.Length();
        if (l1 < 1e-4f || l2 < 1e-4f || dist < 1e-5f)
        {
            return false;
        }

        var tdir = toT / dist;
        dist = Math.Clamp(dist, MathF.Abs(l1 - l2) + 1e-3f, 0.999f * (l1 + l2));

        var ad = d - a;
        if (ad.LengthSquared() < 1e-10f)
        {
            return false;
        }

        var adn = Vector3.Normalize(ad);
        var knee = (b - a) - Vector3.Dot(b - a, adn) * adn;
        if (knee.LengthSquared() < 1e-4f)
        {
            // Straight leg: the bend plane is undefined, so bend the way the foot points.
            knee = footForward - Vector3.Dot(footForward, adn) * adn;
            if (knee.LengthSquared() < 1e-4f)
            {
                return false;
            }
        }

        knee -= Vector3.Dot(knee, tdir) * tdir;
        if (knee.LengthSquared() < 1e-6f)
        {
            return false;
        }

        knee = Vector3.Normalize(knee);

        if (poleBlend > 0f && poleHint.LengthSquared() > 1e-8f)
        {
            var hint = poleHint - Vector3.Dot(poleHint, tdir) * tdir;
            if (hint.LengthSquared() > 1e-6f)
            {
                var blended = Vector3.Lerp(knee, Vector3.Normalize(hint), Math.Clamp(poleBlend, 0f, 1f));
                if (blended.LengthSquared() > 1e-6f)
                {
                    knee = Vector3.Normalize(blended);
                }
            }
        }

        var cosA = Math.Clamp((l1 * l1 + dist * dist - l2 * l2) / (2f * l1 * dist), -1f, 1f);
        var sinA = MathF.Sqrt(MathF.Max(0f, 1f - cosA * cosA));
        newKnee = a + tdir * (l1 * cosA) + knee * (l1 * sinA);
        var newAnkle = a + tdir * dist;

        qA = FromTo(b - a, newKnee - a);
        qB = FromTo(Vector3.Transform(d - b, qA), newAnkle - newKnee);
        return true;
    }

    // Height of the plane through a hit triangle at (x, z), plus its upward normal. A degenerate triangle reports a zero
    // normal: RaycastHit.Normal reads zero in game, so there is nothing to fall back to.
    public static float PlaneHeight(Vector3 v1, Vector3 v2, Vector3 v3, Vector3 hp, float x, float z, out Vector3 normal)
    {
        var tri = Vector3.Cross(v2 - v1, v3 - v1);
        if (tri.LengthSquared() <= 1e-12f)
        {
            normal = Vector3.Zero;
            return hp.Y;
        }

        normal = Vector3.Normalize(tri);
        if (normal.Y < 0)
        {
            normal = -normal;
        }

        return normal.Y > 0.2f ? hp.Y - (normal.X * (x - hp.X) + normal.Z * (z - hp.Z)) / normal.Y : hp.Y;
    }

    // Height of the triangle at (x, z) when that point lies inside its footprint seen from above; false outside it, and
    // for a triangle standing on edge.
    public static bool TriangleHeight(Vector3 a, Vector3 b, Vector3 c, float x, float z, out float y)
    {
        y = 0f;
        var d = ((b.Z - c.Z) * (a.X - c.X)) + ((c.X - b.X) * (a.Z - c.Z));
        if (MathF.Abs(d) < 1e-9f)
        {
            return false;
        }

        var u = (((b.Z - c.Z) * (x - c.X)) + ((c.X - b.X) * (z - c.Z))) / d;
        var v = (((c.Z - a.Z) * (x - c.X)) + ((a.X - c.X) * (z - c.Z))) / d;
        var w = 1f - u - v;
        const float slack = -1e-4f;
        if (u < slack || v < slack || w < slack)
        {
            return false;
        }

        y = (u * a.Y) + (v * b.Y) + (w * c.Y);
        return true;
    }

    // Model-space turn that stands the body's up on the ground plane through four heights a radius out from the origin,
    // capped. Identity for level ground or a plane that could not be read.
    public static Quaternion GroundTilt(float ahead, float behind, float right, float left, float radius, Vector3 fwd, Vector3 side, Quaternion rot, float cap)
    {
        var n = Vector3.UnitY - (fwd * ((ahead - behind) / (2f * radius))) - (side * ((right - left) / (2f * radius)));
        var nModel = Vector3.Transform(n, Quaternion.Inverse(rot));
        if (!float.IsFinite(nModel.X + nModel.Y + nModel.Z) || nModel.LengthSquared() < 1e-8f)
        {
            return Quaternion.Identity;
        }

        nModel = Vector3.Normalize(nModel);
        var axis = Vector3.Cross(Vector3.UnitY, nModel);
        var angle = MathF.Acos(Math.Clamp(nModel.Y, -1f, 1f));
        if (angle < 0.005f || axis.LengthSquared() < 1e-8f)
        {
            return Quaternion.Identity;
        }

        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Min(angle, cap));
    }

    // The cheapest uncrossed pair with room between the boots, a stance sideways or a boot length lengthwise; failing
    // that the widest uncrossed pair, with `apart` false. A foot with no spot at all leaves the other to choose alone.
    public static bool Pick(ReadOnlySpan<Vector3> spotsL, ReadOnlySpan<float> costsL, ReadOnlySpan<Vector3> spotsR, ReadOnlySpan<float> costsR, float minSep, float footLen, Vector3 side, Vector3 fwd, out int iL, out int iR, out bool apart)
    {
        iL = -1;
        iR = -1;
        apart = true;
        if (spotsL.IsEmpty || spotsR.IsEmpty)
        {
            iL = spotsR.IsEmpty ? Cheapest(costsL) : -1;
            iR = spotsL.IsEmpty ? Cheapest(costsR) : -1;
            return iL >= 0 || iR >= 0;
        }

        var bestCost = float.MaxValue;
        var widest = -1f;
        var wL = -1;
        var wR = -1;
        for (var i = 0; i < spotsL.Length; i++)
        {
            if (costsL[i] >= bestCost)
            {
                continue;
            }

            for (var j = 0; j < spotsR.Length; j++)
            {
                var cost = costsL[i] + costsR[j];
                if (cost >= bestCost)
                {
                    continue;
                }

                var sep = new Vector3(spotsR[j].X - spotsL[i].X, 0f, spotsR[j].Z - spotsL[i].Z);
                var lat = Vector3.Dot(sep, side);
                if (lat < 0f)
                {
                    continue;
                }

                var d2 = sep.LengthSquared();
                if (lat >= minSep || MathF.Abs(Vector3.Dot(sep, fwd)) >= footLen)
                {
                    bestCost = cost;
                    iL = i;
                    iR = j;
                }
                else if (iL < 0 && d2 > widest)
                {
                    widest = d2;
                    wL = i;
                    wR = j;
                }
            }
        }

        if (iL >= 0)
        {
            return true;
        }

        apart = false;
        iL = wL;
        iR = wR;
        return iL >= 0;
    }

    private static int Cheapest(ReadOnlySpan<float> costs)
    {
        var best = -1;
        for (var i = 0; i < costs.Length; i++)
        {
            if (best < 0 || costs[i] < costs[best])
            {
                best = i;
            }
        }

        return best;
    }

    public static string SelfTest()
    {
        var a = new Vector3(0, 0.90f, 0);
        var b = new Vector3(0, 0.45f, 0.002f);
        var d = new Vector3(0, 0.00f, 0);
        var fwd = Vector3.UnitZ;

        var target = new Vector3(0, 0.08f, 0);
        if (!TwoBone(a, b, d, fwd, target, Vector3.Zero, 0f, out var qA, out var qB, out var knee))
        {
            return "FAIL: bent-leg solve rejected";
        }

        var ankle = knee + Vector3.Transform(Vector3.Transform(d - b, qA), qB);
        if ((ankle - target).Length() > 1e-3f)
        {
            return $"FAIL: ankle {ankle} != target {target}";
        }

        if (knee.Z <= 0.01f)
        {
            return $"FAIL: knee did not bend forward ({knee})";
        }

        if (MathF.Abs((knee - a).Length() - 0.45f) > 1e-3f || MathF.Abs((ankle - knee).Length() - (d - b).Length()) > 1e-3f)
        {
            return "FAIL: bone lengths changed";
        }

        if (!TwoBone(a, b, d, fwd, new Vector3(0, -0.5f, 0), Vector3.Zero, 0f, out qA, out qB, out knee))
        {
            return "FAIL: out-of-reach solve rejected";
        }

        ankle = knee + Vector3.Transform(Vector3.Transform(d - b, qA), qB);
        if (MathF.Abs((ankle - a).Length() - 0.999f * 0.90f) > 2e-3f)
        {
            return $"FAIL: out-of-reach did not clamp ({(ankle - a).Length():F4})";
        }

        if (!TwoBone(a, b, d, fwd, target, Vector3.UnitX, 1f, out qA, out qB, out knee))
        {
            return "FAIL: pole-hint solve rejected";
        }

        ankle = knee + Vector3.Transform(Vector3.Transform(d - b, qA), qB);
        if ((ankle - target).Length() > 1e-3f || knee.X <= 0.01f)
        {
            return $"FAIL: pole hint moved the ankle or failed to steer the knee (ankle {ankle}, knee {knee})";
        }

        var tan30 = MathF.Tan(MathF.PI / 6f);
        var height = PlaneHeight(new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(0, 1f - tan30, 1), new Vector3(0, 1, 0), 0.5f, 0.5f, out var pn);
        if (MathF.Abs(height - (1f - 0.5f * tan30)) > 1e-4f || MathF.Abs(pn.Y - MathF.Cos(MathF.PI / 6f)) > 1e-4f)
        {
            return $"FAIL: plane height {height:F4}, normal {pn}";
        }

        if (PlaneHeight(Vector3.One, Vector3.One, Vector3.One, new Vector3(0, 2, 0), 5f, 5f, out var flat) != 2f || flat != Vector3.Zero)
        {
            return "FAIL: degenerate triangle did not fall back to the hit point";
        }

        // The plane y = x + 2z through three corners: inside gives its height, a point past the hypotenuse is outside.
        if (!TriangleHeight(new Vector3(0, 0, 0), new Vector3(2, 2, 0), new Vector3(0, 4, 2), 0.5f, 0.5f, out var th) || MathF.Abs(th - 1.5f) > 1e-5f
            || TriangleHeight(new Vector3(0, 0, 0), new Vector3(2, 2, 0), new Vector3(0, 4, 2), 2f, 2f, out _))
        {
            return $"FAIL: triangle height {th:F4}";
        }

        var p = new Xf { T = new Vector3(1, 2, 3), R = Quaternion.CreateFromYawPitchRoll(0.3f, 0.2f, 0.1f), S = new Vector3(1, 2, 1) };
        var c = new Xf { T = new Vector3(0.5f, 1.5f, -2), R = Quaternion.CreateFromYawPitchRoll(-0.4f, 0.1f, 0.7f), S = new Vector3(1, 1, 3) };
        var back = Compose(p, Relative(p, c));
        if ((back.T - c.T).Length() > 1e-4f || (back.S - c.S).Length() > 1e-4f || MathF.Abs(Quaternion.Dot(back.R, c.R)) < 0.9999f)
        {
            return "FAIL: Compose(Relative) is not identity";
        }

        ReadOnlySpan<Vector3> left = [new(0f, 0f, 0f), new(-0.2f, 0f, 0f)];
        ReadOnlySpan<float> leftCost = [0f, 0.04f];
        ReadOnlySpan<Vector3> right = [new(0.1f, 0f, 0f), new(0.35f, 0f, 0f)];
        ReadOnlySpan<float> rightCost = [0.01f, 0.1225f];
        if (!Pick(left, leftCost, right, rightCost, 0.28f, 0.25f, Vector3.UnitX, fwd, out var iL, out var iR, out var apart) || iL != 1 || iR != 0 || !apart)
        {
            return $"FAIL: pick chose {iL}/{iR} apart {apart}, expected 1/0 apart";
        }

        if (!Pick(left[..1], leftCost[..1], right[..1], rightCost[..1], 0.28f, 0.25f, Vector3.UnitX, fwd, out iL, out iR, out apart) || iL != 0 || iR != 0 || apart)
        {
            return "FAIL: pick did not fall back to the widest squeezed pair";
        }

        if (Pick(right[..1], rightCost[..1], left[..1], leftCost[..1], 0.28f, 0.25f, Vector3.UnitX, fwd, out _, out _, out _))
        {
            return "FAIL: pick accepted crossed feet";
        }

        if (!Pick(left, leftCost, default, default, 0.28f, 0.25f, Vector3.UnitX, fwd, out iL, out iR, out _) || iL != 0 || iR != -1)
        {
            return "FAIL: pick with one empty side did not take the cheapest of the other";
        }

        var tan10 = MathF.Tan(MathF.PI / 18f);
        var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        var face = Vector3.Transform(Vector3.UnitZ, yaw);
        var across = new Vector3(face.Z, 0f, -face.X);
        var up = Vector3.Transform(Vector3.UnitY, GroundTilt(0.5f * tan10, -0.5f * tan10, 0f, 0f, 0.5f, face, across, yaw, MathF.PI));
        if (MathF.Abs(up.Y - MathF.Cos(MathF.PI / 18f)) > 1e-4f || up.Z > -0.17f || MathF.Abs(up.X) > 1e-3f)
        {
            return $"FAIL: ground tilt facing uphill did not lean the body back ({up})";
        }

        up = Vector3.Transform(Vector3.UnitY, GroundTilt(0.5f * tan10, -0.5f * tan10, 0f, 0f, 0.5f, face, across, yaw, MathF.PI / 36f));
        if (MathF.Abs(up.Y - MathF.Cos(MathF.PI / 36f)) > 1e-4f)
        {
            return $"FAIL: ground tilt ignored the cap ({up})";
        }

        if (GroundTilt(0.3f, 0.3f, 0.3f, 0.3f, 0.5f, face, across, yaw, MathF.PI) != Quaternion.Identity)
        {
            return "FAIL: level ground tilted the body";
        }

        return "PASS";
    }
}
