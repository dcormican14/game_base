using System;
using System.Collections.Generic;
using Godot;
using GameBase.Nodes;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Asks whether a SHEAR PLANE per cell can be made to agree between neighbours.
///
/// The proposed construction: a surface node is clipped by one extra plane,
/// built from the average of the nearby nodes' heights, keeping the half that
/// holds its own site. That is attractive because it adds a plane to the hull
/// rather than moving corners or varying a cut -- so it cannot push a corner
/// off a bisector and cannot delete a wall, which is how the last four attempts
/// failed.
///
/// It has one way to fail, and the grid's own comments record it happening to a
/// TANGENT plane: if two neighbours build different planes, each crosses their
/// shared wall along a different line, their caps stop at different heights,
/// and the sliver between them is a crack. Measured then at 2421 of 3352
/// corners.
///
/// So this measures, on the real grid and before anything is built: over pairs
/// of adjacent surface cells, how far apart are the two planes where it matters
/// -- on the wall they share?
/// </summary>
public partial class ShearProbe : Node
{
    [Export] public int WaitFrames { get; set; } = 900;
    [Export] public int Pairs { get; set; } = 400;
    [Export] public bool QuitWhenDone { get; set; } = true;

    private NodeWorld _world;
    private ChunkStreamer _streamer;
    private int _frames;
    private bool _done;

    public override void _Ready()
    {
        Node scene = GetTree().CurrentScene;
        _world = NodeSearch.FindByType<NodeWorld>(scene);
        _streamer = NodeSearch.FindByType<ChunkStreamer>(scene);
    }

    public override void _Process(double delta)
    {
        if (_done) return;
        _frames++;
        bool ready = _streamer == null || _streamer.IsReady;
        if (!ready && _frames < WaitFrames) return;
        _done = true;
        Run();
        if (QuitWhenDone) GetTree().Quit(0);
    }

    /// <summary>
    /// The shear plane for one cell: the average height and the average up of
    /// the solid nodes around it.
    ///
    /// Returns false when the cell has too few solid neighbours to define one.
    /// </summary>
    private bool PlaneFor(OrganicGrid grid, Vector3I cell,
        out Vector3 normal, out float offset)
    {
        normal = Vector3.Zero;
        offset = 0f;

        Vector3 centre = Vector3.Zero;
        Vector3 up = Vector3.Zero;
        int found = 0;

        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++)
        {
            var at = new Vector3I(cell.X + dx, cell.Y + dy, cell.Z + dz);

            if (!grid.Contains(at) || !_world.HasNode(at))
                continue;

            Vector3 site = grid.CentreOf(at);

            centre += site;
            up += site.Normalized();
            found++;
        }

        if (found < 3)
            return false;

        centre /= found;

        if (up.LengthSquared() < 0.000001f)
            return false;

        normal = up.Normalized();
        offset = centre.Dot(normal);

        return true;
    }

    private void Run()
    {
        GD.Print("=== SHEAR PROBE ===");
        GD.Print($"  shear fired on {OrganicGrid.ShearedCount} of {OrganicGrid.ShearTried} cells built");

        // WHAT DOES A SURFACE CELL'S NEIGHBOURHOOD ACTUALLY LOOK LIKE?
        // Asked from the test, where the world is fully built, rather than
        // from inside the mesher where it may not be.
        if (_world?.Grid is OrganicGrid g0)
        {
            var rng0 = new Random(5);
            int shown = 0;

            for (int i = 0; i < 40000 && shown < 5; i++)
            {
                Vector3 d = new Vector3(
                    (float)(rng0.NextDouble() * 2 - 1),
                    (float)(rng0.NextDouble() * 2 - 1),
                    (float)(rng0.NextDouble() * 2 - 1));

                if (d.LengthSquared() < 0.01f) continue;
                d = d.Normalized();

                Vector3I c = g0.CellAt(d * (g0.SurfaceRadius - g0.NodeSize * 0.25f));
                if (!g0.Contains(c) || !_world.HasNode(c)) continue;

                float r = g0.CentreOf(c).Length();

                int higher = 0, higherEmpty = 0, higherOffGrid = 0;

                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dy == 0 && dz == 0) continue;

                    var at = new Vector3I(c.X + dx, c.Y + dy, c.Z + dz);

                    if (g0.CentreOf(at).Length() <= r) continue;

                    higher++;

                    if (!g0.Contains(at)) { higherOffGrid++; continue; }
                    if (!_world.HasNode(at)) higherEmpty++;
                }

                GD.Print($"  cell {c} r={r:F2}: {higher} neighbours higher, "
                    + $"{higherEmpty} empty on-grid, {higherOffGrid} off-grid (sky)");

                shown++;
            }
        }

        if (_world?.Grid is not OrganicGrid grid)
        {
            GD.Print("not the organic planet");
            return;
        }

        var rng = new Random(1234);
        float node = grid.NodeSize;
        float radius = grid.SurfaceRadius;

        int pairs = 0, cracked = 0;
        double worst = 0, total = 0;

        // How far apart do the two planes sit, measured ON the shared wall?
        for (int i = 0; i < Pairs * 200 && pairs < Pairs; i++)
        {
            Vector3 dir = new Vector3(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1));

            if (dir.LengthSquared() < 0.01f) continue;
            dir = dir.Normalized();

            Vector3I a = grid.CellAt(dir * (radius - node * 0.25f));

            if (!grid.Contains(a) || !_world.HasNode(a)) continue;

            // A neighbour across one of its walls: the wall they actually share.
            int walls = grid.WallCount(a);
            if (walls == 0) continue;

            if (!grid.WallNeighbour(a, rng.Next(walls), out Vector3I b)) continue;
            if (!grid.Contains(b) || !_world.HasNode(b)) continue;

            if (!PlaneFor(grid, a, out Vector3 na, out float oa)) continue;
            if (!PlaneFor(grid, b, out Vector3 nb, out float ob)) continue;

            pairs++;

            // Sample the shared wall: the bisector between the two sites.
            Vector3 pa = grid.CentreOf(a);
            Vector3 pb = grid.CentreOf(b);

            Vector3 mid = (pa + pb) * 0.5f;
            Vector3 wall = (pb - pa).Normalized();

            Vector3 u = wall.Cross(Vector3.Right);
            if (u.LengthSquared() < 0.01f) u = wall.Cross(Vector3.Up);
            u = u.Normalized();

            Vector3 v = wall.Cross(u);

            double localWorst = 0;

            for (int s = 0; s < 16; s++)
            {
                float su = (float)(rng.NextDouble() - 0.5) * node;
                float sv = (float)(rng.NextDouble() - 0.5) * node;

                Vector3 point = mid + u * su + v * sv;

                // How far above each cell's plane does this point sit? If the
                // two disagree, that is exactly the height their caps differ by.
                float da = point.Dot(na) - oa;
                float db = point.Dot(nb) - ob;

                double gap = Math.Abs(da - db);

                if (gap > localWorst) localWorst = gap;
            }

            total += localWorst;
            if (localWorst > worst) worst = localWorst;

            // A twentieth of a node: below this the two caps meet within the
            // width of a seam you could not see.
            if (localWorst > node * 0.05f) cracked++;
        }

        if (pairs == 0) { GD.Print("  no adjacent surface pairs found"); return; }

        GD.Print("-- rule A: each cell averages its OWN 3x3x3 neighbourhood --");
        GD.Print($"  adjacent surface pairs:   {pairs}");
        GD.Print($"  mean disagreement on the shared wall: {total / pairs:F4} units");
        GD.Print($"  worst:                    {worst:F4} units  (node is {node})");
        GD.Print($"  pairs that would CRACK (>{node * 0.05f:F2}): {cracked}"
            + $"  ({100.0 * cracked / pairs:F1}%)");

        GD.Print(cracked * 20 < pairs
            ? "  => the planes agree closely enough to try"
            : "  => the planes disagree; this would crack like the tangent plane did");

        // RULE B: the plane comes from a SMOOTHED FIELD sampled at the cell's
        // own site, rather than from an average over its own neighbour list.
        //
        // The difference is the whole question. Averaging a NEIGHBOURHOOD makes
        // the plane depend on which cells happen to be in that cell's list, and
        // two neighbours have different lists -- so they disagree, which is what
        // rule A measures at 91.5%. A field evaluated AT A POINT has no list: it
        // is a function of position, so two cells evaluating it at their own
        // sites get answers that vary smoothly rather than jumping.
        //
        // The plane is then: normal = the field's gradient direction, offset =
        // the field's value at the site. Neighbouring sites are a node apart, so
        // the planes differ by roughly the field's slope over a node -- which is
        // small wherever the ground is not a cliff.
        GD.Print("-- rule B: a smooth height field sampled at each site --");
        SmoothRule(grid);

        // RULE C: HEIGHT FROM THE SITE, ORIENTATION FROM THE NEIGHBOURS.
        //
        // The offsets were the whole problem -- 0.66 units apart because each
        // cell averaged a different SET of heights. Pinning the plane through
        // the cell's own site removes the set from the offset entirely: the
        // height is a property of the address, exact and needing no agreement.
        // Only the normal is still averaged, and the normals already agreed to
        // 1.0 degrees.
        //
        // Two planes through DIFFERENT points cannot coincide, so they will
        // still differ where they cross the shared wall. The question this
        // measures is by how much: a degree of tilt over half a node is a much
        // smaller number than two-thirds of a unit.
        GD.Print("-- rule C: plane through the site, tilted by the neighbours --");
        SiteRule(grid);

        // WHERE DOES THE DISAGREEMENT COME FROM? Two candidates: the planes
        // have different NORMALS (each cell's own outward direction differs),
        // or different OFFSETS (each averages a different set of heights).
        // Splitting them says whether the rule can be repaired or not.
        GD.Print("-- what is actually disagreeing --");
        Split(grid);

        GD.Print("=== END SHEAR PROBE ===");
    }

    /// <summary>
    /// The plane for rule C: through the cell's own site, normal from the
    /// average outward direction of its solid neighbours.
    /// </summary>
    private bool SitePlane(OrganicGrid grid, Vector3I cell,
        out Vector3 normal, out float offset)
    {
        normal = Vector3.Zero;
        offset = 0f;

        Vector3 up = Vector3.Zero;
        int found = 0;

        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++)
        {
            var at = new Vector3I(cell.X + dx, cell.Y + dy, cell.Z + dz);

            if (!grid.Contains(at) || !_world.HasNode(at))
                continue;

            up += grid.CentreOf(at).Normalized();
            found++;
        }

        if (found < 3 || up.LengthSquared() < 0.000001f)
            return false;

        normal = up.Normalized();

        // THROUGH THE SITE. No average, no set, no agreement needed.
        offset = grid.CentreOf(cell).Dot(normal);

        return true;
    }

    private void SiteRule(OrganicGrid grid)
    {
        var rng = new Random(1234);
        float node = grid.NodeSize;
        float radius = grid.SurfaceRadius;

        int pairs = 0, cracked = 0;
        double total = 0, worst = 0;

        for (int i = 0; i < Pairs * 200 && pairs < Pairs; i++)
        {
            Vector3 dir = new Vector3(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1));

            if (dir.LengthSquared() < 0.01f) continue;
            dir = dir.Normalized();

            Vector3I a = grid.CellAt(dir * (radius - node * 0.25f));
            if (!grid.Contains(a) || !_world.HasNode(a)) continue;

            int walls = grid.WallCount(a);
            if (walls == 0) continue;

            if (!grid.WallNeighbour(a, rng.Next(walls), out Vector3I b)) continue;
            if (!grid.Contains(b) || !_world.HasNode(b)) continue;

            if (!SitePlane(grid, a, out Vector3 na, out float oa)) continue;
            if (!SitePlane(grid, b, out Vector3 nb, out float ob)) continue;

            pairs++;

            Vector3 pa = grid.CentreOf(a);
            Vector3 pb = grid.CentreOf(b);

            Vector3 mid = (pa + pb) * 0.5f;
            Vector3 wall = (pb - pa).Normalized();

            Vector3 u = wall.Cross(Vector3.Right);
            if (u.LengthSquared() < 0.01f) u = wall.Cross(Vector3.Up);
            u = u.Normalized();

            Vector3 v = wall.Cross(u);

            double localWorst = 0;

            // Sampled over the part of the wall a cell actually reaches.
            for (int s = 0; s < 16; s++)
            {
                float su = (float)(rng.NextDouble() - 0.5) * node;
                float sv = (float)(rng.NextDouble() - 0.5) * node;

                Vector3 point = mid + u * su + v * sv;

                double gap = Math.Abs((point.Dot(na) - oa) - (point.Dot(nb) - ob));

                if (gap > localWorst) localWorst = gap;
            }

            total += localWorst;
            if (localWorst > worst) worst = localWorst;
            if (localWorst > node * 0.05f) cracked++;
        }

        if (pairs == 0) { GD.Print("  no pairs"); return; }

        GD.Print($"  adjacent surface pairs:   {pairs}");
        GD.Print($"  mean disagreement on the shared wall: {total / pairs:F4} units");
        GD.Print($"  worst:                    {worst:F4} units  (node is {node})");
        GD.Print($"  pairs that would CRACK (>{node * 0.05f:F2}): {cracked}"
            + $"  ({100.0 * cracked / pairs:F1}%)");

        // WHY. Two planes pinned through DIFFERENT points cannot coincide, and
        // neighbouring sites sit at different radii by construction -- the
        // jitter moves them in and out. So the gap is roughly that radial
        // difference, whatever the tilt does.
        SiteSpread(grid);
    }

    /// <summary>How far apart in RADIUS are two neighbouring sites?</summary>
    private void SiteSpread(OrganicGrid grid)
    {
        var rng = new Random(1234);
        float node = grid.NodeSize;
        float radius = grid.SurfaceRadius;

        int pairs = 0;
        double total = 0, worst = 0;

        for (int i = 0; i < Pairs * 200 && pairs < Pairs; i++)
        {
            Vector3 dir = new Vector3(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1));

            if (dir.LengthSquared() < 0.01f) continue;
            dir = dir.Normalized();

            Vector3I a = grid.CellAt(dir * (radius - node * 0.25f));
            if (!grid.Contains(a) || !_world.HasNode(a)) continue;

            int walls = grid.WallCount(a);
            if (walls == 0) continue;

            if (!grid.WallNeighbour(a, rng.Next(walls), out Vector3I b)) continue;
            if (!grid.Contains(b) || !_world.HasNode(b)) continue;

            pairs++;

            double gap = Math.Abs(
                grid.CentreOf(a).Length() - grid.CentreOf(b).Length());

            total += gap;
            if (gap > worst) worst = gap;
        }

        if (pairs == 0) return;

        GD.Print($"  -- and neighbouring SITES differ in radius by: "
            + $"mean {total / pairs:F4}, worst {worst:F4}");
        GD.Print("     which is the floor on any plane pinned through the site");
    }

    private void Split(OrganicGrid grid)
    {
        var rng = new Random(1234);
        float node = grid.NodeSize;
        float radius = grid.SurfaceRadius;

        int pairs = 0;
        double tiltTotal = 0, tiltWorst = 0;
        double offTotal = 0, offWorst = 0;

        for (int i = 0; i < Pairs * 200 && pairs < Pairs; i++)
        {
            Vector3 dir = new Vector3(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1));

            if (dir.LengthSquared() < 0.01f) continue;
            dir = dir.Normalized();

            Vector3I a = grid.CellAt(dir * (radius - node * 0.25f));
            if (!grid.Contains(a) || !_world.HasNode(a)) continue;

            int walls = grid.WallCount(a);
            if (walls == 0) continue;

            if (!grid.WallNeighbour(a, rng.Next(walls), out Vector3I b)) continue;
            if (!grid.Contains(b) || !_world.HasNode(b)) continue;

            if (!PlaneFor(grid, a, out Vector3 na, out float oa)) continue;
            if (!PlaneFor(grid, b, out Vector3 nb, out float ob)) continue;

            pairs++;

            // How far apart are the normals, in degrees?
            float dot = Mathf.Clamp(na.Dot(nb), -1f, 1f);
            double tilt = Mathf.RadToDeg(Mathf.Acos(dot));

            tiltTotal += tilt;
            if (tilt > tiltWorst) tiltWorst = tilt;

            // And the offsets, in units -- compared at the midpoint so the
            // normals' own difference does not contaminate it.
            Vector3 mid = (grid.CentreOf(a) + grid.CentreOf(b)) * 0.5f;

            double off = Math.Abs((mid.Dot(na) - oa) - (mid.Dot(nb) - ob));

            offTotal += off;
            if (off > offWorst) offWorst = off;
        }

        if (pairs == 0) { GD.Print("  no pairs"); return; }

        GD.Print($"  normals differ by:  mean {tiltTotal / pairs:F2} deg, "
            + $"worst {tiltWorst:F2} deg");
        GD.Print($"  offsets differ by:  mean {offTotal / pairs:F4} units, "
            + $"worst {offWorst:F4} units");
        GD.Print($"  for scale, the working surface meets its twins to 0.00366 units");

        // IS IT THE SET, OR THE JITTER?
        //
        // If two neighbours averaged the SAME set of cells they would get the
        // same plane by construction. The union of both neighbourhoods is such
        // a set -- symmetric, because A-union-B is the same whichever cell asks.
        // If that removes the disagreement, the rule is repairable; if the
        // sites' own jitter still moves the plane, it is not.
        GD.Print("-- would a SHARED set of neighbours agree? --");
        Shared(grid);
    }

    /// <summary>
    /// The plane from a set of cells both neighbours can agree on: the union of
    /// their two neighbourhoods.
    /// </summary>
    private bool PlaneOver(OrganicGrid grid, HashSet<Vector3I> cells,
        out Vector3 normal, out float offset)
    {
        normal = Vector3.Zero;
        offset = 0f;

        Vector3 centre = Vector3.Zero;
        Vector3 up = Vector3.Zero;
        int found = 0;

        foreach (Vector3I at in cells)
        {
            if (!grid.Contains(at) || !_world.HasNode(at))
                continue;

            Vector3 site = grid.CentreOf(at);
            centre += site;
            up += site.Normalized();
            found++;
        }

        if (found < 3 || up.LengthSquared() < 0.000001f)
            return false;

        centre /= found;
        normal = up.Normalized();
        offset = centre.Dot(normal);

        return true;
    }

    private void Shared(OrganicGrid grid)
    {
        var rng = new Random(1234);
        float node = grid.NodeSize;
        float radius = grid.SurfaceRadius;

        int pairs = 0, cracked = 0;
        double total = 0, worst = 0;

        for (int i = 0; i < Pairs * 200 && pairs < Pairs; i++)
        {
            Vector3 dir = new Vector3(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1));

            if (dir.LengthSquared() < 0.01f) continue;
            dir = dir.Normalized();

            Vector3I a = grid.CellAt(dir * (radius - node * 0.25f));
            if (!grid.Contains(a) || !_world.HasNode(a)) continue;

            int walls = grid.WallCount(a);
            if (walls == 0) continue;

            if (!grid.WallNeighbour(a, rng.Next(walls), out Vector3I b)) continue;
            if (!grid.Contains(b) || !_world.HasNode(b)) continue;

            // THE UNION, which both cells compute identically.
            var union = new HashSet<Vector3I>();

            foreach (Vector3I seed in new[] { a, b })
            {
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                    union.Add(new Vector3I(seed.X + dx, seed.Y + dy, seed.Z + dz));
            }

            if (!PlaneOver(grid, union, out Vector3 n, out float o)) continue;

            pairs++;

            // Both cells now hold the SAME plane, so the only thing that can
            // differ is which side of it each keeps -- measured as the gap at
            // the midpoint of their shared wall, which is zero by construction.
            Vector3 mid = (grid.CentreOf(a) + grid.CentreOf(b)) * 0.5f;

            double gap = Math.Abs((mid.Dot(n) - o) - (mid.Dot(n) - o));

            total += gap;
            if (gap > worst) worst = gap;
            if (gap > node * 0.05f) cracked++;
        }

        if (pairs == 0) { GD.Print("  no pairs"); return; }

        GD.Print($"  pairs: {pairs}, disagreement mean {total / pairs:F5}, "
            + $"worst {worst:F5}");
        GD.Print("  (zero by construction -- but a shared set means the plane is");
        GD.Print("   a property of the PAIR, and a cell has many neighbours, so");
        GD.Print("   it would need a different plane for each. That is not one");
        GD.Print("   shear plane per node.)");
    }

    /// <summary>
    /// The height of the ground at a point: a distance-weighted average over
    /// the solid nodes near it.
    ///
    /// A FUNCTION OF THE POINT, with no reference to any cell. That is the
    /// property rule A lacks.
    /// </summary>
    private float HeightAt(OrganicGrid grid, Vector3 point)
    {
        float node = grid.NodeSize;
        float reach = node * 2.2f;

        Vector3 local = point;

        var home = new Vector3I(
            Mathf.RoundToInt(local.X / node),
            Mathf.RoundToInt(local.Y / node),
            Mathf.RoundToInt(local.Z / node));

        float sum = 0, weight = 0;

        for (int dx = -3; dx <= 3; dx++)
        for (int dy = -3; dy <= 3; dy++)
        for (int dz = -3; dz <= 3; dz++)
        {
            var at = new Vector3I(home.X + dx, home.Y + dy, home.Z + dz);

            if (!grid.Contains(at) || !_world.HasNode(at))
                continue;

            Vector3 site = grid.CentreOf(at);
            float d = site.DistanceTo(point);

            if (d >= reach)
                continue;

            float w = 1f - d / reach;
            w *= w;

            sum += site.Length() * w;
            weight += w;
        }

        return weight > 0.0001f ? sum / weight : point.Length();
    }

    private void SmoothRule(OrganicGrid grid)
    {
        var rng = new Random(1234);
        float node = grid.NodeSize;
        float radius = grid.SurfaceRadius;

        int pairs = 0, cracked = 0;
        double worst = 0, total = 0;

        for (int i = 0; i < Pairs * 200 && pairs < Pairs; i++)
        {
            Vector3 dir = new Vector3(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1));

            if (dir.LengthSquared() < 0.01f) continue;
            dir = dir.Normalized();

            Vector3I a = grid.CellAt(dir * (radius - node * 0.25f));
            if (!grid.Contains(a) || !_world.HasNode(a)) continue;

            int walls = grid.WallCount(a);
            if (walls == 0) continue;

            if (!grid.WallNeighbour(a, rng.Next(walls), out Vector3I b)) continue;
            if (!grid.Contains(b) || !_world.HasNode(b)) continue;

            Vector3 pa = grid.CentreOf(a);
            Vector3 pb = grid.CentreOf(b);

            pairs++;

            Vector3 mid = (pa + pb) * 0.5f;
            Vector3 wall = (pb - pa).Normalized();

            Vector3 u = wall.Cross(Vector3.Right);
            if (u.LengthSquared() < 0.01f) u = wall.Cross(Vector3.Up);
            u = u.Normalized();

            Vector3 v = wall.Cross(u);

            double localWorst = 0;

            for (int s = 0; s < 16; s++)
            {
                float su = (float)(rng.NextDouble() - 0.5) * node;
                float sv = (float)(rng.NextDouble() - 0.5) * node;

                Vector3 point = mid + u * su + v * sv;

                // Each cell's plane: through its own site's height, normal
                // along its own site's outward direction.
                float ha = HeightAt(grid, pa);
                float hb = HeightAt(grid, pb);

                Vector3 na = pa.Normalized();
                Vector3 nb2 = pb.Normalized();

                float da = point.Dot(na) - ha;
                float db = point.Dot(nb2) - hb;

                double gap = Math.Abs(da - db);

                if (gap > localWorst) localWorst = gap;
            }

            total += localWorst;
            if (localWorst > worst) worst = localWorst;
            if (localWorst > node * 0.05f) cracked++;
        }

        if (pairs == 0) { GD.Print("  no pairs"); return; }

        GD.Print($"  adjacent surface pairs:   {pairs}");
        GD.Print($"  mean disagreement on the shared wall: {total / pairs:F4} units");
        GD.Print($"  worst:                    {worst:F4} units");
        GD.Print($"  pairs that would CRACK (>{node * 0.05f:F2}): {cracked}"
            + $"  ({100.0 * cracked / pairs:F1}%)");
    }
}
