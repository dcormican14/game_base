using Godot;
using System;
using System.Collections.Generic;
using GameBase.Nodes;

namespace GameBase.Stats;

/// <summary>
/// Temporary: does the crystal interlock still tile in (u,v,shell) space,
/// and what happens at face seams?
/// </summary>
public partial class SeamCheck : Node
{
    public override void _Ready()
    {
        const float R = 800f, N = 1f;
        var grid = new SphereGrid(R, N, Vector3.Zero);
        var raw = new RawNode(1337, terrain: new TerrainField(1337));
        var rng = new Random(5);

        GD.Print($"[seam] baseResolution={grid.BaseResolution}");

        // A patch WELL INSIDE one face: the control. The interlock should tile
        // here exactly as it does on a Cartesian lattice, because within a
        // face (u,v,shell) IS a Cartesian lattice.
        Interior(grid, raw, rng);

        // A patch STRADDLING a face seam: the case in question.
        Seam(grid, raw);

        // Is the neighbour mapping ONE-TO-ONE across a seam? Two different
        // cells stepping the same way must not land on the same cell, or the
        // grid folds and geometry is generated twice for one place.
        {
            int res2 = CubedSphere.ResolutionAt(0, 800f, 1f);
            var landed = new Dictionary<Vector3I, Vector3I>();
            int collisions = 0, tested = 0;

            for (int v = 0; v < res2; v += 7)
            {
                Vector3I edge = grid.Pack(NodeOrientation.PosY, res2 - 1, v, 0);
                if (!grid.Neighbour(edge, 1, 0, 0, out Vector3I over)) continue;
                tested++;

                if (landed.TryGetValue(over, out Vector3I prev))
                {
                    collisions++;
                    if (collisions <= 3)
                        GD.Print($"[seam]   COLLISION {prev} and {edge} both -> {over}");
                }
                else landed[over] = edge;
            }

            GD.Print($"[seam] one-to-one across the +Y/+X seam: {tested} edge cells, "
                   + $"{landed.Count} distinct targets, {collisions} collisions");
        }

        GetTree().Quit();
    }

    /// <summary>Overlaps among a solid block of cells inside one face.</summary>
    private static void Interior(SphereGrid grid, RawNode raw, Random rng)
    {
        int res = CubedSphere.ResolutionAt(0, 800f, 1f);
        int u0 = res / 2, v0 = res / 2;

        var owner = new Dictionary<(int, int, int), Vector3I>();
        long overlaps = 0, cells = 0;

        for (int du = 0; du < 24; du++)
        for (int dv = 0; dv < 24; dv++)
        for (int ds = 0; ds < 8; ds++)
        {
            Vector3I cell = grid.Pack(NodeOrientation.PosY, u0 + du, v0 + dv, ds);
            cells++;
            Claim(raw, cell, owner, ref overlaps);
        }

        GD.Print($"[seam] INTERIOR: {cells} cells, {overlaps} overlapping sub-cells");
    }

    /// <summary>
    /// Overlaps among a block of cells that genuinely straddles a face seam.
    ///
    /// Built by taking every cell whose CENTRE lies inside a small solid angle
    /// centred on the seam, so both faces are represented and nothing is
    /// visited twice -- which is what a real chunk of terrain at a seam looks
    /// like.
    /// </summary>
    private static void Seam(SphereGrid grid, RawNode raw)
    {
        SeamAt(grid, raw, new Vector3(1f, 1f, 0f).Normalized(), "edge +Y/+X");
        SeamAt(grid, raw, new Vector3(1f, 1f, 1f).Normalized(), "CORNER +X/+Y/+Z");
        SeamAt(grid, raw, new Vector3(-1f, 1f, -1f).Normalized(), "CORNER -X/+Y/-Z");
    }

    private static void SeamAt(SphereGrid grid, RawNode raw, Vector3 seam, string name)
    {

        var owner = new Dictionary<(int, int, int), Vector3I>();
        var cellsSeen = new HashSet<Vector3I>();
        var faces = new Dictionary<int, int>();
        var pairs = new Dictionary<string, long>();
        long overlaps = 0;

        // Sample the solid angle densely enough that every cell in it is found.
        for (int a = -40; a <= 40; a++)
        for (int b = -40; b <= 40; b++)
        for (int shell = 0; shell < 6; shell++)
        {
            // Two directions spanning the seam.
            Vector3 t1 = seam.Cross(Vector3.Up);
            if (t1.LengthSquared() < 0.01f) t1 = seam.Cross(Vector3.Right);
            t1 = t1.Normalized();
            Vector3 t2 = seam.Cross(t1).Normalized();

            Vector3 dir = (seam + t1 * (a * 0.0004f) + t2 * (b * 0.0004f)).Normalized();
            float radius = CubedSphere.RadiusOf(shell, 800f, 1f) - 0.5f;

            Vector3I cell = grid.CellAt(dir * radius);
            if (!cellsSeen.Add(cell)) continue;

            grid.Unpack(cell, out int f, out _, out _, out _);
            faces.TryGetValue(f, out int seen);
            faces[f] = seen + 1;

            ClaimTracked(raw, cell, owner, ref overlaps, pairs, grid);
        }

        var fl = new List<string>();
        foreach (var kv in faces) fl.Add($"{kv.Key}:{kv.Value}");
        GD.Print($"[seam] {name}: {cellsSeen.Count} distinct cells "
               + $"across faces [{string.Join(" ", fl)}], {overlaps} overlapping sub-cells");

        foreach (var kv in pairs)
            GD.Print($"[seam]   {kv.Key}: {kv.Value}");
    }

    private static void Claim(RawNode raw, Vector3I cell,
        Dictionary<(int, int, int), Vector3I> owner, ref long overlaps)
    {
        int[] cs = raw.OccupiedCells(raw.ShapeAt(cell));
        for (int i = 0; i < cs.Length; i += 3)
        {
            var key = (cell.X * 4 + cs[i], cell.Y * 4 + cs[i + 1], cell.Z * 4 + cs[i + 2]);
            if (owner.ContainsKey(key)) overlaps++;
            else owner[key] = cell;
        }
    }

    private static void ClaimTracked(RawNode raw, Vector3I cell,
        Dictionary<(int, int, int), Vector3I> owner, ref long overlaps,
        Dictionary<string, long> pairs, SphereGrid grid)
    {
        int[] cs = raw.OccupiedCells(raw.ShapeAt(cell));
        for (int i = 0; i < cs.Length; i += 3)
        {
            var key = (cell.X * 4 + cs[i], cell.Y * 4 + cs[i + 1], cell.Z * 4 + cs[i + 2]);
            if (owner.TryGetValue(key, out Vector3I prev))
            {
                overlaps++;
                grid.Unpack(prev, out int fa, out _, out _, out _);
                grid.Unpack(cell, out int fb, out _, out _, out _);
                string p = fa == fb ? $"same face ({fa})" : $"across seam ({fa}<->{fb})";
                pairs.TryGetValue(p, out long n);
                pairs[p] = n + 1;
            }
            else owner[key] = cell;
        }
    }
}
