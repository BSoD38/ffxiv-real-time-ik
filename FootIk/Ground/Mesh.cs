using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Lumina.Data.Files;
using Lumina.Models.Models;

namespace FootIk;

// Experimental: the ground height read off the visible level geometry. Collision still finds the ground and gates
// everything; the render mesh only moves a hit collision already made, and only within a band of it. Within that band
// the surface nearest the hit wins, a part that carries a collider (or the terrain) only on a tie: giving those priority
// took the foot down to the terrain under a raised collider-less floor wherever the two came close. A staircase whose
// ramp collision lives on a neighbouring part with no render mesh of its own is the only surface there and wins.
// Skipping collider-less parts outright lost those stairs.
public sealed unsafe partial class Plugin
{
    private const float MeshCell = 1f; // metres: a property of the level, not of the character
    private const float MeshMaxSpan = 64f; // metres: a triangle wider than this is bucketed on a grid this coarse instead of the 1 m one
    private const float MeshMaxWide = 4096f; // metres: bounds the coarse-grid inserts per triangle; nothing walkable has come close
    private const float MeshMaxSphere = 2500f; // metres: real floor parts have carried spheres of 2428 m; this only keeps out the sky

    // Cell inserts within one part, 25x what a building has needed; soft, a build already queued still finishes. No
    // budget over every part held: one was tried, checked at queue time against what had been built so far, and the
    // first scan after enabling queues everything in range against zero, so it only ever refused the parts walked onto
    // later, and with the floor under you unread the refine sank the foot to the terrain beneath. A whole zone read at
    // once came to 4.6 M triangles and the client carried it.
    private const int MeshMaxInserts = 500_000;

    // One placed object: its world-space triangles, three vertices each, bucketed on an XZ grid. Built once on the
    // worker when it enters the radius and dropped when it leaves, so moving never rebuilds what is already there.
    // Built is set on the main thread only, after the worker has handed the entry back, and the render thread reads it
    // first; a giant tree was a single 16 ms build, which no per-frame budget could split.
    private sealed class MeshEntry
    {
        public nint Key;
        public string Path = string.Empty;
        public Vector3 Pos;
        public Quaternion Rot;
        public Vector3 Scale;
        public bool Solid;
        public int Stamp;
        public bool Built;
        public int Kept;
        public bool Queued;
        public Vector3[] Tris = [];
        public float MinX, MaxX, MinZ, MaxZ;
        public Dictionary<long, List<int>> Cells = [];
        public Dictionary<long, List<int>> Wide = [];
    }

    private struct Candidate
    {
        public float Dist = float.MaxValue;
        public MeshEntry? Entry;
        public int Tri;
        public float Y;

        public Candidate()
        {
        }

        public void Offer(float dist, MeshEntry entry, int tri, float y)
        {
            if (dist < this.Dist)
            {
                this.Dist = dist;
                this.Entry = entry;
                this.Tri = tri;
                this.Y = y;
            }
        }
    }

    // Local-space triangles per model path; null when the file could not be read. Touched by the worker only, and
    // there is never more than one worker. Kept because the same stair model is placed hundreds of times.
    private readonly Dictionary<string, Vector3[]?> models = new(StringComparer.Ordinal);
    private readonly Dictionary<nint, MeshEntry> entries = [];
    private readonly ConcurrentQueue<MeshEntry> toBuild = new();
    private readonly ConcurrentQueue<MeshEntry> built = new();
    private Task? worker;
    private volatile bool meshStop;
    private volatile bool meshFlush;
    private int triangles;
    private int stamp;
    private uint meshTerritory;
    private double lastScan;

    // What the Performance tab shows: how much is held, and the reason it switched itself off if it did.
    public string MeshStatus { get; private set; } = "off";

    // UiBuilder.Draw: the main thread, like the render detour. Only the layout scan and the hand-back run here; the
    // file reads and the bucketing run on the worker.
    private void UpdateMesh()
    {
        if (!this.Settings.MeshRefine)
        {
            if (this.entries.Count > 0)
            {
                this.entries.Clear();
                this.toBuild.Clear();
                this.triangles = 0;

                // A fault turns the setting off itself, and its message is the only thing that says why: keep it.
                this.MeshStatus = this.MeshStatus.StartsWith("error", StringComparison.Ordinal) ? this.MeshStatus : "off";
            }

            return;
        }

        var lp = Objects.LocalPlayer;
        if (lp == null)
        {
            return;
        }

        try
        {
            var now = this.clock.Elapsed.TotalSeconds;
            if (now - this.lastScan >= 1.0)
            {
                this.lastScan = now;
                // The old zone's parts go now rather than on the sweep below: Track does not compare the model path, so
                // a freed address reused at the same placement would keep the old zone's triangles.
                if (ClientState.TerritoryType != this.meshTerritory)
                {
                    this.meshTerritory = ClientState.TerritoryType;
                    this.meshFlush = true;
                    this.entries.Clear();
                    this.toBuild.Clear();
                    this.triangles = 0;
                }

                this.ScanMesh(lp.Position);
            }

            this.Commit();
            if (!this.toBuild.IsEmpty && (this.worker == null || this.worker.IsCompleted))
            {
                this.worker = Task.Run(this.BuildLoop);
            }

            this.MeshStatus = $"{this.entries.Count} objects, {this.toBuild.Count} to build, {this.triangles} triangles";
        }
        catch (Exception ex)
        {
            // Managed faults only. A bad pointer in the layout walk never arrives here, because an access violation is
            // not catchable, so every hop of that walk is null-checked instead and this is not what keeps it up.
            // Switches itself off and says why; the setting is not saved, so it is back on next load.
            this.Settings.MeshRefine = false;
            this.MeshStatus = $"error: {ex.Message}";
            Log.Error(ex, "FootIk: mesh scan failed");
        }
    }

    // Marks every object in range with this scan's stamp, queueing the new ones, and drops the rest.
    private void ScanMesh(Vector3 origin)
    {
        var world = LayoutWorld.Instance();
        var layout = world == null ? null : world->ActiveLayout;
        if (layout == null)
        {
            return;
        }

        var radius = this.Settings.MeshRadius;
        this.stamp++;
        var key = InstanceType.BgPart;
        if (layout->InstancesByType.TryGetValuePointer(in key, out var inner) && inner != null && inner->Value != null)
        {
            foreach (var pair in *inner->Value)
            {
                var inst = (BgPartsLayoutInstance*)pair.Item2.Value;
                if (inst == null)
                {
                    continue;
                }

                var gfx = inst->GraphicsObject;
                if (gfx == null || gfx->ModelResourceHandle == null)
                {
                    continue;
                }

                Vector3 pos = gfx->Position;
                var sphere = inst->BoundingSphereSize;
                if (sphere > MeshMaxSphere || Vector3.Distance(pos, origin) > radius + sphere)
                {
                    continue;
                }

                this.Track((nint)inst, pos, gfx->Rotation, gfx->Scale, inst->Collider != null, &gfx->ModelResourceHandle->FileName);
            }
        }

        foreach (var pair in layout->Terrains)
        {
            var manager = pair.Item2.Value;
            if (manager == null || manager->GfxTerrain == null)
            {
                continue;
            }

            foreach (var plate in manager->GfxTerrain->TerrainPlates)
            {
                var p = plate.Value;
                if (p == null || p->ModelResourceHandle == null)
                {
                    continue;
                }

                Vector3 pos = p->Translation;
                if (Vector3.Distance(pos, origin) > radius + p->TileWidth)
                {
                    continue;
                }

                this.Track((nint)p, pos, Quaternion.Identity, Vector3.One, true, &p->ModelResourceHandle->FileName);
            }
        }

        foreach (var (k, e) in this.entries)
        {
            if (e.Stamp != this.stamp)
            {
                if (e.Built)
                {
                    this.triangles -= e.Kept;
                }

                this.entries.Remove(k);
            }
        }
    }

    // The address is the key and the allocator reuses addresses, so an entry whose placement moved is a different
    // object, or the same one carried somewhere: either way its world triangles are stale and it is built again.
    private void Track(nint key, Vector3 pos, Quaternion rot, Vector3 scale, bool solid, FFXIVClientStructs.STD.StdString* name)
    {
        if (!this.entries.TryGetValue(key, out var e) || e.Pos != pos || e.Rot != rot || e.Scale != scale)
        {
            if (e is { Built: true })
            {
                this.triangles -= e.Kept;
            }

            e = new MeshEntry { Key = key, Path = name->ToString(), Pos = pos, Rot = rot, Scale = scale, Solid = solid };
            this.entries[key] = e;
        }

        e.Stamp = this.stamp;

        // Queued only ever once, so the worker never rebuilds a live entry.
        if (!e.Queued)
        {
            e.Queued = true;
            this.toBuild.Enqueue(e);
        }
    }

    // Main thread: an entry the worker finished becomes visible to the refine, unless it was dropped or replaced meanwhile.
    private void Commit()
    {
        while (this.built.TryDequeue(out var e))
        {
            if (this.entries.TryGetValue(e.Key, out var current) && current == e)
            {
                e.Built = true;
                this.triangles += e.Kept;
            }
        }
    }

    // Worker: drains the queue and stops. Started again from the main thread whenever there is work and none running,
    // so there is never more than one.
    private void BuildLoop()
    {
        while (!this.meshStop && this.toBuild.TryDequeue(out var e))
        {
            // Dropped here rather than where the territory changed: the cache belongs to this thread, and the entries
            // the new zone queued are behind this point, so they still find it empty.
            if (this.meshFlush)
            {
                this.meshFlush = false;
                this.models.Clear();
            }

            try
            {
                this.Build(e);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "FootIk: could not build {Path}", e.Path);
            }

            this.built.Enqueue(e);
        }
    }

    private void Build(MeshEntry e)
    {
        var local = this.LoadModel(e.Path);
        if (local == null)
        {
            return;
        }

        var tris = new Vector3[local.Length];
        for (var i = 0; i < local.Length; i++)
        {
            tris[i] = e.Pos + Vector3.Transform(e.Scale * local[i], e.Rot);
        }

        // A non-finite vertex floors to int.MinValue, and a triangle kilometres wide covers millions of 1 m cells: either
        // turned the bucketing into billions of inserts and hung the client once. Wide triangles go on the coarse grid
        // instead, since real floor parts have triangles wider than 64 m, and beyond MeshMaxWide they are dropped. The
        // width bounds one triangle's inserts, not the model's, so the running count stops a mesh of wide triangles.
        e.MinX = e.MinZ = float.MaxValue;
        e.MaxX = e.MaxZ = float.MinValue;
        var cells = new Dictionary<long, List<int>>();
        var wide = new Dictionary<long, List<int>>();
        var kept = 0;
        var inserts = 0;
        for (var t = 0; t + 2 < tris.Length; t += 3)
        {
            var a = tris[t];
            var b = tris[t + 1];
            var c = tris[t + 2];
            var minX = MathF.Min(a.X, MathF.Min(b.X, c.X));
            var maxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
            var minZ = MathF.Min(a.Z, MathF.Min(b.Z, c.Z));
            var maxZ = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
            if (!float.IsFinite(minX + maxX + minZ + maxZ + a.Y + b.Y + c.Y) || maxX - minX > MeshMaxWide || maxZ - minZ > MeshMaxWide
                || inserts >= MeshMaxInserts)
            {
                continue;
            }

            kept++;
            e.MinX = MathF.Min(e.MinX, minX);
            e.MaxX = MathF.Max(e.MaxX, maxX);
            e.MinZ = MathF.Min(e.MinZ, minZ);
            e.MaxZ = MathF.Max(e.MaxZ, maxZ);
            inserts += maxX - minX > MeshMaxSpan || maxZ - minZ > MeshMaxSpan
                ? Bucket(wide, t, minX, maxX, minZ, maxZ, MeshMaxSpan)
                : Bucket(cells, t, minX, maxX, minZ, maxZ, MeshCell);
        }

        e.Tris = tris;
        e.Cells = cells;
        e.Wide = wide;
        e.Kept = kept;
    }

    private static int Bucket(Dictionary<long, List<int>> grid, int tri, float minX, float maxX, float minZ, float maxZ, float size)
    {
        var n = 0;
        var x1 = (int)MathF.Floor(maxX / size);
        var z1 = (int)MathF.Floor(maxZ / size);
        for (var x = (int)MathF.Floor(minX / size); x <= x1; x++)
        {
            for (var z = (int)MathF.Floor(minZ / size); z <= z1; z++)
            {
                if (!grid.TryGetValue(Cell(x, z), out var cell))
                {
                    grid[Cell(x, z)] = cell = [];
                }

                cell.Add(tri);
                n++;
            }
        }

        return n;
    }

    private static long Cell(int x, int z) => ((long)x << 32) ^ (uint)z;

    private static long CellAt(float x, float z, float size) => Cell((int)MathF.Floor(x / size), (int)MathF.Floor(z / size));

    // The render triangle under (x, z) on either grid whose height is nearest refY, within band and never above maxY.
    private static bool TryNearest(MeshEntry e, float x, float z, float refY, float band, float maxY, out int tri, out float y)
    {
        tri = -1;
        y = 0f;
        for (var g = 0; g < 2; g++)
        {
            var size = g == 0 ? MeshCell : MeshMaxSpan;
            if (!(g == 0 ? e.Cells : e.Wide).TryGetValue(CellAt(x, z, size), out var list))
            {
                continue;
            }

            foreach (var t in list)
            {
                if (Solver.TriangleHeight(e.Tris[t], e.Tris[t + 1], e.Tris[t + 2], x, z, out var h) && h <= maxY && MathF.Abs(h - refY) <= band)
                {
                    band = MathF.Abs(h - refY);
                    tri = t;
                    y = h;
                }
            }
        }

        return tri >= 0;
    }

    // Worker thread. Dalamud's own GetFileAsync is GetFile on a pool thread, so the read is safe off the main thread.
    private Vector3[]? LoadModel(string path)
    {
        if (this.models.TryGetValue(path, out var cached))
        {
            return cached;
        }

        Vector3[]? result = null;
        try
        {
            var file = Data.GetFile<MdlFile>(path);
            if (file != null)
            {
                var list = new List<Vector3>();
                foreach (var mesh in new Model(file, Model.ModelLod.High, 0).GetMeshesByType(Lumina.Models.Models.Mesh.MeshType.Main))
                {
                    for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
                    {
                        if (mesh.Vertices[mesh.Indices[i]].Position is { } a && mesh.Vertices[mesh.Indices[i + 1]].Position is { } b && mesh.Vertices[mesh.Indices[i + 2]].Position is { } c)
                        {
                            list.Add(new Vector3(a.X, a.Y, a.Z));
                            list.Add(new Vector3(b.X, b.Y, b.Z));
                            list.Add(new Vector3(c.X, c.Y, c.Z));
                        }
                    }
                }

                result = list.ToArray();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FootIk: could not read {Path}", path);
        }

        this.models[path] = result;
        return result;
    }

    // The worker must not touch Dalamud services after they are gone; a parse in progress is given a moment to finish.
    private void DisposeMesh()
    {
        this.meshStop = true;
        this.toBuild.Clear();
        try
        {
            this.worker?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
        }
    }

    // Swaps the collision hit's triangle for the render triangle under the same spot that is nearest it in height,
    // within the band and never above the ray's start; a collider-bearing surface wins a tie. Everything downstream
    // reads the triangle, so nothing else changes.
    private void RefineByMesh(Vector3 origin, ref RaycastHit hit)
    {
        Vector3 p = hit.Point;
        var band = this.Settings.MeshBand;
        var solid = new Candidate();
        var loose = new Candidate();
        foreach (var e in this.entries.Values)
        {
            if (e.Built && p.X >= e.MinX && p.X <= e.MaxX && p.Z >= e.MinZ && p.Z <= e.MaxZ && TryNearest(e, p.X, p.Z, p.Y, band, origin.Y, out var tri, out var y))
            {
                ref var best = ref (e.Solid ? ref solid : ref loose);
                best.Offer(MathF.Abs(y - p.Y), e, tri, y);
            }
        }

        // Nearest to the hit wins, a collider-bearing surface on a tie. Letting one win merely for being within reach took
        // the foot down to the terrain under a raised collider-less floor wherever the two came close.
        var pick = loose.Dist < solid.Dist ? loose : solid;
        if (pick.Entry == null)
        {
            return;
        }

        hit.V1 = pick.Entry.Tris[pick.Tri];
        hit.V2 = pick.Entry.Tris[pick.Tri + 1];
        hit.V3 = pick.Entry.Tris[pick.Tri + 2];
        hit.Point = new Vector3(p.X, pick.Y, p.Z);
        hit.Distance = origin.Y - pick.Y;
    }
}
