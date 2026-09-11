using System;
using System.Numerics;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;

namespace FootIk;

internal static class Bones
{
    public static Vector3 Pos(in hkQsTransformf q) => new(q.Translation.X, q.Translation.Y, q.Translation.Z);

    public static Xf Read(in hkQsTransformf q) => new()
    {
        T = Pos(in q),
        R = new Quaternion(q.Rotation.X, q.Rotation.Y, q.Rotation.Z, q.Rotation.W),
        S = new Vector3(q.Scale.X, q.Scale.Y, q.Scale.Z),
    };

    public static void Write(ref hkQsTransformf q, in Xf x)
    {
        q.Translation.X = x.T.X;
        q.Translation.Y = x.T.Y;
        q.Translation.Z = x.T.Z;
        q.Rotation.X = x.R.X;
        q.Rotation.Y = x.R.Y;
        q.Rotation.Z = x.R.Z;
        q.Rotation.W = x.R.W;
        q.Scale.X = x.S.X;
        q.Scale.Y = x.S.Y;
        q.Scale.Z = x.S.Z;
    }
}

public struct LegSide
{
    public int Hip, Knee, Ankle, Toes;
    public int[] Subtree;    // hip first, then every descendant in ascending bone index (Havok: parent < child)
    public int[] ParentSlot; // slot of each subtree bone's parent within Subtree
    public int KneeSlot, AnkleSlot, ToesSlot;
    public float RestBind;   // ankle model-space Y in the bind pose, where the sole sits on the origin plane

    public readonly bool Resolved => this.Hip >= 0 && this.Knee >= 0 && this.Ankle >= 0 && this.Toes >= 0 && this.Subtree is { Length: > 3 };
}

public unsafe struct LegChain
{
    public nint Skeleton;
    public int BoneCount;
    public float LegLength; // bind-pose hip→knee→ankle length, averaged over both sides, model units
    public Vector3 BindForward; // horizontal direction the feet point in the bind pose (model space)
    public Vector3 BindSide;    // horizontal direction from the left ankle to the right in the bind pose (model space)
    public Vector3 BindMid;     // horizontal midpoint of the ankles in the bind pose (model space)
    public int SpineA, SpineB, SpineC, Neck; // j_sebo_a/b/c, j_kubi; -1 if absent
    public int[] SpineSub;        // j_sebo_a and every descendant (whole upper body), ascending
    public int[] SpineParentSlot;
    public int[] SpineSlotOf;     // bone index -> slot in SpineSub, -1 outside it
    public int SpineBSlot, SpineCSlot, NeckSlot;
    public LegSide Left;
    public LegSide Right;

    public readonly bool Resolved => this.Skeleton != 0 && this.Left.Resolved && this.Right.Resolved && this.LegLength > 1e-3f;

    public readonly LegSide Side(int s) => s == 0 ? this.Left : this.Right;

    // Keyed on pointer AND bone count: the allocator reuses skeleton addresses across gear and race swaps.
    public readonly bool Matches(hkaSkeleton* hs) => this.Skeleton == (nint)hs && this.BoneCount == hs->Bones.Length;

    public static LegChain Resolve(hkaSkeleton* hs)
    {
        var chain = new LegChain
        {
            Skeleton = (nint)hs,
            BoneCount = hs->Bones.Length,
            SpineA = -1,
            SpineB = -1,
            SpineC = -1,
            Neck = -1,
            SpineSub = [],
            SpineParentSlot = [],
            SpineSlotOf = [],
            Left = new LegSide { Hip = -1, Knee = -1, Ankle = -1, Toes = -1 },
            Right = new LegSide { Hip = -1, Knee = -1, Ankle = -1, Toes = -1 },
        };

        for (var i = 0; i < chain.BoneCount; i++)
        {
            var name = hs->Bones.Data[i].Name.String;
            if (name is null)
            {
                continue;
            }

            switch (name)
            {
                case "j_sebo_a": chain.SpineA = i; continue;
                case "j_sebo_b": chain.SpineB = i; continue;
                case "j_sebo_c": chain.SpineC = i; continue;
                case "j_kubi": chain.Neck = i; continue;
            }

            if (name.Length != 9 || !name.StartsWith("j_asi_", StringComparison.Ordinal))
            {
                continue;
            }

            ref var side = ref (name[8] == 'l' ? ref chain.Left : ref chain.Right);
            switch (name[6])
            {
                case 'a': side.Hip = i; break;
                case 'b': side.Knee = i; break;
                case 'd': side.Ankle = i; break;
                case 'e': side.Toes = i; break;
            }
        }

        BuildSubtree(hs, ref chain.Left);
        BuildSubtree(hs, ref chain.Right);
        if (chain.SpineA >= 0 && chain.SpineB >= 0 && chain.SpineC >= 0 && chain.Neck >= 0)
        {
            BuildSubtree(hs, chain.SpineA, out chain.SpineSub, out chain.SpineParentSlot, out chain.SpineSlotOf);
            chain.SpineBSlot = chain.SpineSlotOf[chain.SpineB];
            chain.SpineCSlot = chain.SpineSlotOf[chain.SpineC];
            chain.NeckSlot = chain.SpineSlotOf[chain.Neck];
            if (chain.SpineBSlot < 0 || chain.SpineCSlot < 0 || chain.NeckSlot < 0)
            {
                chain.SpineSub = [];
            }
        }

        chain.Left.RestBind = BindModel(hs, chain.Left.Ankle).Y;
        chain.Right.RestBind = BindModel(hs, chain.Right.Ankle).Y;
        chain.LegLength = (BindLegLength(hs, in chain.Left) + BindLegLength(hs, in chain.Right)) * 0.5f;
        var fwd = (BindModel(hs, chain.Left.Toes) - BindModel(hs, chain.Left.Ankle)) + (BindModel(hs, chain.Right.Toes) - BindModel(hs, chain.Right.Ankle));
        fwd.Y = 0f;
        chain.BindForward = fwd.LengthSquared() > 1e-8f ? Vector3.Normalize(fwd) : Vector3.UnitZ;
        var across = BindModel(hs, chain.Right.Ankle) - BindModel(hs, chain.Left.Ankle);
        across.Y = 0f;
        chain.BindSide = across.LengthSquared() > 1e-8f ? Vector3.Normalize(across) : Vector3.UnitX;
        var mid = (BindModel(hs, chain.Left.Ankle) + BindModel(hs, chain.Right.Ankle)) * 0.5f;
        chain.BindMid = new Vector3(mid.X, 0f, mid.Z);
        return chain;
    }

    private static float BindLegLength(hkaSkeleton* hs, in LegSide side)
    {
        if (side.Hip < 0 || side.Knee < 0 || side.Ankle < 0)
        {
            return 0f;
        }

        var hip = BindModel(hs, side.Hip);
        var knee = BindModel(hs, side.Knee);
        var ankle = BindModel(hs, side.Ankle);
        return (knee - hip).Length() + (ankle - knee).Length();
    }

    // Model-space position in the bind pose, composed from the root down.
    private static Vector3 BindModel(hkaSkeleton* hs, int bone)
    {
        var n = hs->Bones.Length;
        if (bone < 0 || hs->ReferencePose.Length != n || hs->ParentIndices.Length != n)
        {
            return Vector3.Zero;
        }

        Span<int> path = stackalloc int[64];
        var depth = 0;
        for (var k = bone; k >= 0 && k < n && depth < path.Length; k = hs->ParentIndices.Data[k])
        {
            path[depth++] = k;
        }

        var acc = new Xf { R = Quaternion.Identity, S = Vector3.One };
        for (var i = depth - 1; i >= 0; i--)
        {
            acc = Solver.Compose(in acc, Bones.Read(in hs->ReferencePose.Data[path[i]]));
        }

        return acc.T;
    }

    private static void BuildSubtree(hkaSkeleton* hs, int root, out int[] subtree, out int[] parentSlot, out int[] slotOf)
    {
        var n = hs->Bones.Length;
        var parents = hs->ParentIndices.Data;
        slotOf = new int[n];
        Array.Fill(slotOf, -1);
        var sub = new System.Collections.Generic.List<int>(8);
        var ps = new System.Collections.Generic.List<int>(8);
        for (var k = root; k < n; k++)
        {
            var p = k == root ? -1 : parents[k];
            if (k != root && (p < 0 || p >= n || slotOf[p] < 0))
            {
                continue;
            }

            slotOf[k] = sub.Count;
            sub.Add(k);
            ps.Add(k == root ? -1 : slotOf[p]);
        }

        subtree = [.. sub];
        parentSlot = [.. ps];
    }

    private static void BuildSubtree(hkaSkeleton* hs, ref LegSide side)
    {
        if (side.Hip < 0)
        {
            return;
        }

        BuildSubtree(hs, side.Hip, out side.Subtree, out side.ParentSlot, out var slot);
        side.KneeSlot = side.Knee >= 0 ? slot[side.Knee] : -1;
        side.AnkleSlot = side.Ankle >= 0 ? slot[side.Ankle] : -1;
        side.ToesSlot = side.Toes >= 0 ? slot[side.Toes] : -1;
        if (side.KneeSlot < 0 || side.AnkleSlot < 0 || side.ToesSlot < 0)
        {
            side.Subtree = [];
        }
    }
}
