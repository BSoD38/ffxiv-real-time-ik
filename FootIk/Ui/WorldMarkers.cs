using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FootIk;

internal sealed partial class Overlay
{
    // The world markers stay on ImGui: they are drawn over the scene rather than in a window, and the game's own
    // node tree has nothing that draws a line between two screen points.
    public void DrawWorldDots()
    {
        ref var s = ref this.Owner.Snap;
        var c = this.Owner.Settings;
        if (!s.HasPose || (!c.ShowMarkers && !c.ShowRuler))
        {
            return;
        }

        var dl = ImGui.GetBackgroundDrawList();
        if (c.ShowMarkers)
        {
            DrawFootDots(dl, in s.Left);
            DrawFootDots(dl, in s.Right);
        }

        if (c.ShowRuler)
        {
            DrawRuler(dl, in s);
        }
    }

    // One metre along the character's right on the ground under the body and one metre up from it, a tick every ten centimetres.
    private static void DrawRuler(ImDrawListPtr dl, in Snapshot s)
    {
        var side = new Vector3(MathF.Cos(s.Yaw), 0f, -MathF.Sin(s.Yaw));
        DrawRuler(dl, s.Ground, side, Vector3.UnitY);
        DrawRuler(dl, s.Ground, Vector3.UnitY, side);
    }

    private static void DrawRuler(ImDrawListPtr dl, Vector3 from, Vector3 along, Vector3 tick)
    {
        if (!Plugin.GameGui.WorldToScreen(from, out var a) || !Plugin.GameGui.WorldToScreen(from + along, out var b))
        {
            return;
        }

        dl.AddLine(a, b, 0xFFFFFFFF, 2f);
        for (var i = 0; i <= 10; i++)
        {
            var p = from + (along * (0.1f * i));
            var t = tick * (i % 5 == 0 ? 0.06f : 0.03f);
            if (Plugin.GameGui.WorldToScreen(p, out var p0) && Plugin.GameGui.WorldToScreen(p + t, out var p1))
            {
                dl.AddLine(p0, p1, 0xFFFFFFFF, 2f);
            }
        }

        dl.AddText(b + new Vector2(6f, -8f), 0xFFFFFFFF, "1 m");
    }

    private static void DrawFootDots(ImDrawListPtr dl, in FootSnapshot f)
    {
        if (Plugin.GameGui.WorldToScreen(f.AnkleWorld, out var a))
        {
            dl.AddCircleFilled(a, 5f, 0xFF00FF00);
        }

        if (f.Solved && Plugin.GameGui.WorldToScreen(f.IkTargetWorld, out var t))
        {
            dl.AddCircle(t, 6f, 0xFF00FFFF, 0, 2f);
        }

        if (f.Hit && Plugin.GameGui.WorldToScreen(f.HitPoint, out var g) && Plugin.GameGui.WorldToScreen(f.HitPoint + f.HitNormal * 0.25f, out var n))
        {
            dl.AddLine(g, n, 0xFFFFFF00, 2f);
        }
    }
}
