using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;

namespace FootIk;

public sealed unsafe partial class Plugin
{
    // Guards before every dereference: an access violation is not catchable, so a bad pointer must never be reached.
    private static bool ResolvePose(Character* chr, ref LegChain chain, ref Snapshot snap, out hkaPose* pose, out Skeleton* skel)
    {
        pose = null;
        skel = null;
        var draw = chr->DrawObject;
        if (draw == null || draw->GetObjectType() != ObjectType.CharacterBase)
        {
            return false;
        }

        skel = ((CharacterBase*)draw)->Skeleton;
        if (skel == null || skel->PartialSkeletonCount == 0)
        {
            return false;
        }

        pose = skel->PartialSkeletons[0].GetHavokPose(0);
        if (pose == null || pose->Skeleton == null)
        {
            return false;
        }

        var hs = pose->Skeleton;
        snap.HasPose = true;
        var boneCount = hs->Bones.Length;
        if (pose->ModelPose.Length != boneCount || hs->ParentIndices.Length != boneCount)
        {
            return false;
        }

        if (!chain.Matches(hs))
        {
            chain = LegChain.Resolve(hs);
        }

        snap.ChainResolved = chain.Resolved;
        if (!chain.Resolved)
        {
            return false;
        }

        if (pose->ModelInSync == 0)
        {
            pose->SyncModelSpace();
        }

        return true;
    }

    // The ankle keeps its animated orientation plus ankleTilt; the rest of the leg follows the hip and knee.
    private static bool SolveLeg(hkaPose* pose, in LegSide side, Vector3 target, Quaternion ankleTilt, Vector3 poleHint, float poleBlend)
    {
        var sub = side.Subtree;
        var bones = pose->ModelPose.Data;
        Span<Xf> old = stackalloc Xf[sub.Length];
        Span<Xf> nw = stackalloc Xf[sub.Length];
        for (var i = 0; i < sub.Length; i++)
        {
            old[i] = Bones.Read(in bones[sub[i]]);
        }

        var a = old[0].T;
        var b = old[side.KneeSlot].T;
        var d = old[side.AnkleSlot].T;
        var footForward = old[side.ToesSlot].T - d;
        if (!Solver.TwoBone(a, b, d, footForward, target, poleHint, poleBlend, out var qA, out var qB, out _))
        {
            return false;
        }

        nw[0] = old[0];
        nw[0].R = Quaternion.Normalize(qA * old[0].R);
        for (var i = 1; i < sub.Length; i++)
        {
            var j = side.ParentSlot[i];
            nw[i] = Solver.Compose(in nw[j], Solver.Relative(in old[j], in old[i]));
            if (i == side.KneeSlot)
            {
                nw[i].R = Quaternion.Normalize(qB * nw[i].R);
            }
            else if (i == side.AnkleSlot)
            {
                nw[i].R = Quaternion.Normalize(ankleTilt * old[i].R);
            }
        }

        for (var i = 0; i < sub.Length; i++)
        {
            if (!nw[i].IsFinite)
            {
                return false;
            }
        }

        for (var i = 0; i < sub.Length; i++)
        {
            Bones.Write(ref bones[sub[i]], in nw[i]);
        }

        pose->LocalInSync = 0;
        return true;
    }

    // Turns the whole model-space pose about the character origin. Face and hair partials were attached to body bones
    // before this hook ran; a rigid turn about the origin moves an anchor and its partial the same way.
    private static void RotateBody(Skeleton* skel, hkaPose* pose, Quaternion q)
    {
        for (var p = 0; p < skel->PartialSkeletonCount; p++)
        {
            var pp = p == 0 ? pose : skel->PartialSkeletons[p].GetHavokPose(0);
            if (pp == null || pp->Skeleton == null || pp->ModelPose.Length != pp->Skeleton->Bones.Length)
            {
                continue;
            }

            if (pp->ModelInSync == 0)
            {
                pp->SyncModelSpace();
            }

            var pb = pp->ModelPose.Data;
            // Never n_root, bone 0 of the body: the game reads it back to see how far the animation has carried the
            // character, so turning it moves the character and the camera re-anchors. Only the body's, though: bone 0 of
            // a face or hair partial fastens it to the head, and leaving it behind tears the head apart (seen in game).
            for (var k = p == 0 ? 1 : 0; k < pp->ModelPose.Length; k++)
            {
                var x = Bones.Read(in pb[k]);
                x.T = Vector3.Transform(x.T, q);
                x.R = Quaternion.Normalize(q * x.R);
                if (x.IsFinite)
                {
                    Bones.Write(ref pb[k], in x);
                }
            }

            pp->LocalInSync = 0;
        }
    }

    // lean is radians, forward positive: a third on each spine bone, the neck counter-rotated so the head stays level.
    private static void ApplySpineLean(Skeleton* skel, hkaPose* pose, in LegChain chain, float lean)
    {
        var sub = chain.SpineSub;
        var ps = chain.SpineParentSlot;
        var axis = Vector3.Cross(Vector3.UnitY, chain.BindForward);
        // The subtree is the whole upper body, sized by game data: an unbounded stackalloc risks a stack overflow,
        // which is not catchable, and an empty one indexes out of bounds below.
        if (sub.Length is 0 or > 256 || axis.LengthSquared() < 1e-8f)
        {
            return;
        }

        axis = Vector3.Normalize(axis);
        var bones = pose->ModelPose.Data;
        Span<Xf> old = stackalloc Xf[sub.Length];
        Span<Xf> nw = stackalloc Xf[sub.Length];
        for (var i = 0; i < sub.Length; i++)
        {
            old[i] = Bones.Read(in bones[sub[i]]);
        }

        var third = Quaternion.CreateFromAxisAngle(axis, lean / 3f);
        var counter = Quaternion.CreateFromAxisAngle(axis, -lean);
        nw[0] = old[0];
        nw[0].R = Quaternion.Normalize(third * old[0].R);
        for (var i = 1; i < sub.Length; i++)
        {
            nw[i] = Solver.Compose(in nw[ps[i]], Solver.Relative(in old[ps[i]], in old[i]));
            if (i == chain.SpineBSlot || i == chain.SpineCSlot)
            {
                nw[i].R = Quaternion.Normalize(third * nw[i].R);
            }
            else if (i == chain.NeckSlot)
            {
                nw[i].R = Quaternion.Normalize(counter * nw[i].R);
            }
        }

        for (var i = 0; i < sub.Length; i++)
        {
            if (!nw[i].IsFinite)
            {
                return;
            }
        }

        for (var i = 0; i < sub.Length; i++)
        {
            Bones.Write(ref bones[sub[i]], in nw[i]);
        }

        pose->LocalInSync = 0;

        // Face and hair partials were connected to body bones before this hook ran, so re-derive each from how its
        // anchor moved, or the face stays where the head was.
        for (var p = 1; p < skel->PartialSkeletonCount; p++)
        {
            ref var part = ref skel->PartialSkeletons[p];
            var anchor = (int)part.ConnectedParentBoneIndex;
            if (anchor < 0 || anchor >= chain.SpineSlotOf.Length)
            {
                continue;
            }

            var j = chain.SpineSlotOf[anchor];
            var pp = part.GetHavokPose(0);
            if (j < 0 || pp == null || pp->Skeleton == null || pp->ModelPose.Length != pp->Skeleton->Bones.Length)
            {
                continue;
            }

            if (pp->ModelInSync == 0)
            {
                pp->SyncModelSpace();
            }

            var pb = pp->ModelPose.Data;
            for (var k = 0; k < pp->ModelPose.Length; k++)
            {
                var x = Solver.Compose(in nw[j], Solver.Relative(in old[j], Bones.Read(in pb[k])));
                if (x.IsFinite)
                {
                    Bones.Write(ref pb[k], in x);
                }
            }

            pp->LocalInSync = 0;
        }
    }
}
