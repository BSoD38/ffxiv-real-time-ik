using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace FootIk;

public sealed unsafe partial class Plugin
{
    // Layer 1, any non-zero material. The 0x4000 filter that the ClientStructs wrapper applies is blind to standable
    // placed objects: a tree stump read material 0x2005.
    // Seen in game: shallow water surface 0x380D against a standable tree stump 0x2005. Only these two bits set it apart,
    // so they are the best guess at what marks water; the see-through below stays harmless if a floor carries them too.
    private const ulong WaterMaterialBits = 0x1800;

    private bool Raycast(Vector3 origin, out RaycastHit hit, float maxDist) => this.Raycast(origin, -Vector3.UnitY, out hit, maxDist);

    private bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit, float maxDist)
    {
        hit = default;
        var fw = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        if (fw == null || fw->BGCollisionModule == null)
        {
            return false;
        }

        var flags = stackalloc int[] { -1, -1, 0, 0 };
        var o = origin;
        var d = direction;
        var local = default(RaycastHit);
        var ok = fw->BGCollisionModule->RaycastMaterialFilter(&local, &o, &d, maxDist, 1, flags);

        // Shallow water has a walkable-looking surface above the bed the character actually wades on. Look through it;
        // when nothing lies beneath it was a floor after all.
        if (ok && (local.Material & WaterMaterialBits) == WaterMaterialBits && local.Distance + 0.02f < maxDist)
        {
            var skip = local.Distance + 0.01f;
            var o2 = origin + (direction * skip);
            var below = default(RaycastHit);
            if (fw->BGCollisionModule->RaycastMaterialFilter(&below, &o2, &d, maxDist - skip, 1, flags))
            {
                below.Distance += skip;
                local = below;
            }
        }

        hit = local;
        return ok;
    }

    // The boot's extent around the ankle, in world units: it reaches further forward than back.
    private readonly struct FootBox(float ahead, float behind, float halfWidth, float height)
    {
        public readonly float Ahead = ahead;
        public readonly float Behind = behind;
        public readonly float HalfWidth = halfWidth;
        public readonly float Height = height;
    }

    private enum Ground
    {
        None,
        TooLow,
        TooHigh, // a ledge the foot cannot be placed on, and cannot pass through either
        Standable,
    }

    private bool TryGround(Vector3 at, in Frame f, out RaycastHit hit, out float y)
    {
        y = 0f;
        if (!this.Raycast(new Vector3(at.X, f.ProbeTop, at.Z), out hit, f.MaxDist))
        {
            return false;
        }

        y = GroundAt(in hit, at.X, at.Z, out _);
        return true;
    }

    // What is under (at.X, at.Z), relative to the ground under the body.
    private Ground ProbeSupport(Vector3 at, in Frame f, out RaycastHit hit) => this.ProbeSupport(at, in f, out hit, out _);

    private Ground ProbeSupport(Vector3 at, in Frame f, out RaycastHit hit, out float gy)
    {
        gy = 0f;
        if (!this.TryGround(at, in f, out hit, out var y))
        {
            return Ground.None;
        }

        gy = ((y - f.OriginY) / f.Scale.Y) - f.BaseY;
        return gy > f.MaxStep ? Ground.TooHigh
            : gy < -f.MaxStepDown ? Ground.TooLow
            : Ground.Standable;
    }

    private bool TrySupport(Vector3 at, in Frame f, out RaycastHit hit) => this.ProbeSupport(at, in f, out hit) == Ground.Standable;

    // With `level`, only ground at the level the character stands on, not lower ground it could step down to.
    private bool TrySupport(Vector3 at, in Frame f, bool level, out RaycastHit hit) => this.ProbeSupport(at, in f, out hit, out var gy) == Ground.Standable && (!level || MathF.Abs(gy) <= f.StepTol);

    // The smallest horizontal move that takes the boot out of whatever wall it overlaps. Feelers reach from the ankle,
    // at boot height, to each edge of the box; one cut short by something steep means the boot is inside it by the
    // remainder. Rising ground is not a wall.
    private Vector3 WallPush(Vector3 at, Vector3 forward, in FootBox box, float groundY, in Frame f)
    {
        var right = new Vector3(forward.Z, 0f, -forward.X);
        var eye = new Vector3(at.X, groundY + box.Height, at.Z);
        var push = Vector3.Zero;
        Span<Vector2> arms = [new(0f, box.Ahead), new(0f, -box.Behind), new(box.HalfWidth, 0f), new(-box.HalfWidth, 0f)];
        foreach (var arm in arms)
        {
            var dir = (right * arm.X) + (forward * arm.Y);
            var len = dir.Length();
            if (len < 1e-4f)
            {
                continue;
            }

            dir /= len;
            if (this.Raycast(eye, dir, out var hit, len) && SurfaceUp(in hit) < 0.5f)
            {
                Vector3 hp = hit.Point;
                var reach = new Vector3(hp.X - eye.X, 0f, hp.Z - eye.Z).Length();
                // A hit reported past the feeler's own length is not an overlap; counting it would flip the push toward
                // the obstacle.
                if (reach < len)
                {
                    push -= dir * (len - reach);
                }
            }
        }

        return push;
    }

    // Height of the hit surface's plane at (x, z). The hit's Normal field reads as zero in game; the triangle is reliable.
    private static float GroundAt(in RaycastHit hit, float x, float z, out Vector3 normal)
    {
        Vector3 v1 = hit.V1, v2 = hit.V2, v3 = hit.V3, hp = hit.Point;
        return Solver.PlaneHeight(v1, v2, v3, hp, x, z, out normal);
    }

    // Upward component of the surface struck: near zero on a wall, near one on a floor.
    private static float SurfaceUp(in RaycastHit hit)
    {
        GroundAt(in hit, hit.Point.X, hit.Point.Z, out var normal);
        return normal.Y;
    }

    private static float MoveToward(float v, float target, float step) => v < target ? MathF.Min(v + step, target) : MathF.Max(v - step, target);

    // A non-finite target is dropped rather than eased in: once inside, NaN never leaves an accumulator.
    private static float Toward(float v, float target, float k) => float.IsFinite(target) ? v + ((target - v) * k) : v;

    private static Vector3 Toward(Vector3 v, Vector3 target, float k) => float.IsFinite(target.X + target.Y + target.Z) ? v + ((target - v) * k) : v;

    private static float Ease(float dt, float tau) => 1f - MathF.Exp(-dt / MathF.Max(tau, 1e-3f));
}
