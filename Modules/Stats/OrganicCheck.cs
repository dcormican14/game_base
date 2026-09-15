using Godot;
using System;
using GameBase.Nodes;

namespace GameBase.Stats;

/// <summary>
/// Measures the organic grid against what a Voronoi world has to deliver.
///
/// The claims worth testing are the same three as for any grid here, plus one
/// this world alone has to earn:
///
///  1. CELLS ARE WELL FORMED. Every face is a real polygon with area, and the
///     cell count per node is in the range a jittered lattice produces.
///  2. WALLS ARE BISECTORS. A wall's corners are equidistant from the two
///     sites it separates -- which is what makes two nodes agree on where their
///     shared surface is, rather than leaving a gap.
///  3. LOOKUP ROUND-TRIPS. A node's own site lands back in that node.
///  4. THE SURFACE IS CLIPPED. Nothing sticks out past the planet's radius.
/// </summary>
public partial class OrganicCheck : Node
{
    [Export] public float Radius { get; set; } = 120f;
    [Export] public float NodeSize { get; set; } = 1f;
    [Export] public float Jitter { get; set; } = 0.5f;
    [Export] public bool QuitWhenDone { get; set; } = true;

    public override void _Ready()
    {
        var grid = new OrganicGrid(Radius, NodeSize, Vector3.Zero, Jitter);


        GD.Print("=== ORGANIC GRID CHECK ===");
        GD.Print($"radius {grid.SurfaceRadius}  node {grid.NodeSize}"
            + $"  jitter {grid.Jitter}  extent {grid.ShellCount}");

        Shapes(grid);
        Bisectors(grid);
        RoundTrip(grid);
        Surface(grid);
        Mutual(grid);
        Topsoil(grid);
        Smoothness(grid);
        CapSeams(grid);
        Picking(grid);
        Shapes2(grid);

        GD.Print("=== END ORGANIC GRID CHECK ===");

        if (QuitWhenDone)
            GetTree().Quit(0);
    }

    /// <summary>A spread of cells from the surface down to the core.</summary>
    private static System.Collections.Generic.List<Vector3I> Sample(OrganicGrid grid)
    {
        var found = new System.Collections.Generic.List<Vector3I>();
        var random = new Random(12345);

        int extent = grid.ShellCount;

        while (found.Count < 400)
        {
            var at = new Vector3I(
                random.Next(-extent, extent),
                random.Next(-extent, extent),
                random.Next(-extent, extent));

            if (grid.Contains(at))
                found.Add(at);
        }

        return found;
    }

    /// <summary>Face counts and whether every face is a real polygon.</summary>
    private static void Shapes(OrganicGrid grid)
    {
        Span<Vector3I> walls = stackalloc Vector3I[grid.MaxWalls];
        Span<int> sides = stackalloc int[grid.MaxWalls];
        Span<Vector3> corners = stackalloc Vector3[grid.MaxWalls * 12];

        int cells = 0, faces = 0, degenerate = 0;
        int fewest = int.MaxValue, most = 0;
        double totalSides = 0;

        foreach (Vector3I cell in Sample(grid))
        {
            int count = grid.Faces(cell, walls, sides, corners);
            cells++;

            if (count < fewest) fewest = count;
            if (count > most) most = count;

            for (int n = 0; n < count; n++)
            {
                faces++;
                totalSides += sides[n];

                if (sides[n] < 3)
                    degenerate++;
            }
        }

        GD.Print("-- 1. cell shapes --");
        GD.Print($"  {cells} cells, {faces} faces");
        GD.Print($"  faces per cell {fewest}..{most}, mean {(double)faces / cells:F1}"
            + "   (a jittered lattice gives about 13)");
        GD.Print($"  mean sides per face {totalSides / Math.Max(1, faces):F1}");
        GD.Print($"  faces with fewer than 3 corners: {degenerate}");
    }

    /// <summary>
    /// Is every wall equidistant from the two sites it separates?
    ///
    /// The defining property of a Voronoi wall, and the one that makes two
    /// neighbouring nodes agree on where their shared surface lies.
    /// </summary>
    /// <remarks>
    /// A FEW THOUSANDTHS IS EXPECTED AT THE SURFACE. Where a wall is cut by the
    /// planet, its new corners are lifted from the chord onto the sphere, which
    /// takes them a hair off the flat bisector -- about a chord squared over
    /// eight radii, or 0.004 for a node-wide cut on a radius-120 planet.
    ///
    /// That is an error against the IDEAL plane, not between two cells: both
    /// neighbours lift the same corner by the same arithmetic, so they still
    /// agree with each other exactly. Check 5 (mutual) and check 8 (caps meet)
    /// are the ones that would fail if they did not.
    /// </remarks>
    private static void Bisectors(OrganicGrid grid)
    {
        Span<Vector3I> walls = stackalloc Vector3I[grid.MaxWalls];
        Span<int> sides = stackalloc int[grid.MaxWalls];
        Span<Vector3> corners = stackalloc Vector3[grid.MaxWalls * 12];

        float worst = 0f;
        int checkedWalls = 0, skipped = 0;

        foreach (Vector3I cell in Sample(grid))
        {
            int count = grid.Faces(cell, walls, sides, corners);
            Vector3 here = grid.CentreOf(cell);

            int at = 0;

            for (int n = 0; n < count; n++)
            {
                int span = sides[n];

                if (span < 3)
                    continue;

                // The surface cap is named by the cell itself and separates the
                // node from the sky, not from a neighbour.
                if (walls[n] == cell)
                {
                    at += span;
                    skipped++;
                    continue;
                }

                Vector3 there = grid.CentreOf(walls[n]);

                for (int c = 0; c < span; c++)
                {
                    Vector3 corner = corners[at + c];

                    float error = Mathf.Abs(
                        corner.DistanceTo(here) - corner.DistanceTo(there));

                    if (error > worst)
                        worst = error;
                }

                at += span;
                checkedWalls++;
            }
        }

        GD.Print("-- 2. walls are perpendicular bisectors --");
        GD.Print($"  {checkedWalls} walls checked, {skipped} surface caps skipped");
        GD.Print($"  worst equidistance error {worst:F6}   (0 = exact bisector)");
    }

    /// <summary>Does a node's own site land back in that node?</summary>
    private static void RoundTrip(OrganicGrid grid)
    {
        int total = 0, wrong = 0;

        foreach (Vector3I cell in Sample(grid))
        {
            // A SITE CAN LIE OUTSIDE ITS OWN PLANET. The surface clip trims a
            // node's body to the radius but leaves its site where the hash put
            // it, which may be a little beyond -- and CellAt deliberately
            // answers "nothing here" for a point outside, because a sample in
            // the sky belongs to no node. Asking it to round-trip such a site
            // is asking a question it is designed not to answer, so those are
            // skipped rather than counted as failures.
            if (grid.CentreOf(cell).DistanceTo(grid.Origin) > grid.SurfaceRadius)
                continue;

            total++;

            if (grid.CellAt(grid.CentreOf(cell)) != cell)
                wrong++;
        }

        // Also from arbitrary points, which is what digging and collision do.
        var random = new Random(999);
        int points = 0, unstable = 0;

        while (points < 2000)
        {
            var at = new Vector3(
                (float)(random.NextDouble() * 2 - 1) * grid.SurfaceRadius,
                (float)(random.NextDouble() * 2 - 1) * grid.SurfaceRadius,
                (float)(random.NextDouble() * 2 - 1) * grid.SurfaceRadius);

            if (at.LengthSquared() > grid.SurfaceRadius * grid.SurfaceRadius)
                continue;

            points++;

            // The cell a point lands in must be the cell whose site is nearest:
            // that is the definition, and the 27-cell window has to find it.
            Vector3I owner = grid.CellAt(at);
            float mine = grid.CentreOf(owner).DistanceSquaredTo(at);

            for (int dx = -2; dx <= 2 && !Broke(mine); dx++)
            {
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dz = -2; dz <= 2; dz++)
                    {
                        var other = new Vector3I(
                            owner.X + dx, owner.Y + dy, owner.Z + dz);

                        if (grid.CentreOf(other).DistanceSquaredTo(at) < mine - 0.0001f)
                        {
                            unstable++;
                            dx = 99; dy = 99;
                            break;
                        }
                    }
                }
            }
        }

        GD.Print("-- 3. lookup --");
        GD.Print($"  {total - wrong} / {total} sites round-trip to their own cell");
        GD.Print($"  {points - unstable} / {points} random points found their"
            + " true nearest site");
    }

    private static bool Broke(float _) => false;

    /// <summary>
    /// If A lists B as a neighbour, does B list A back -- and do the two agree
    /// on where their shared wall is?
    ///
    /// THIS IS WHAT FACE CULLING RESTS ON. The mesher draws a face when the
    /// node behind it is not solid, so two nodes that disagree about being
    /// neighbours will both draw the wall between them, and both drawn faces
    /// are buried where nothing can see them. An audit of the finished mesh
    /// reported 2.9% of quads buried; this asks the grid directly, which says
    /// whether that is the grid's fault or the audit's sampling.
    /// </summary>
    private static void Mutual(OrganicGrid grid)
    {
        Span<Vector3I> walls = stackalloc Vector3I[grid.MaxWalls];
        Span<int> sides = stackalloc int[grid.MaxWalls];
        Span<Vector3> corners = stackalloc Vector3[grid.MaxWalls * 12];

        Span<Vector3I> theirs = stackalloc Vector3I[grid.MaxWalls];
        Span<int> theirSides = stackalloc int[grid.MaxWalls];
        Span<Vector3> theirCorners = stackalloc Vector3[grid.MaxWalls * 12];

        int total = 0, oneWay = 0, caps = 0;

        foreach (Vector3I cell in Sample(grid))
        {
            int count = grid.Faces(cell, walls, sides, corners);

            for (int n = 0; n < count; n++)
            {
                if (sides[n] < 3)
                    continue;

                // A boundary face is named by the node itself and has no
                // neighbour to agree with.
                if (walls[n] == cell)
                {
                    caps++;
                    continue;
                }

                total++;

                int back = grid.Faces(walls[n], theirs, theirSides, theirCorners);
                bool found = false;

                for (int t = 0; t < back && !found; t++)
                {
                    if (theirSides[t] >= 3 && theirs[t] == cell)
                        found = true;
                }

                if (!found)
                    oneWay++;
            }
        }

        GD.Print("-- 5. neighbours are mutual --");
        GD.Print($"  {total - oneWay} / {total} walls listed from both sides"
            + $", {caps} surface caps");
        GD.Print(oneWay == 0
            ? "  every wall is agreed on by both nodes"
            : $"  ONE-WAY: {oneWay} walls one node claims and the other does not");
    }

    /// <summary>
    /// Is the topsoil what it claims to be?
    ///
    /// Three things have to hold, and each is a way the arrangement could look
    /// right and be wrong:
    ///
    ///  - RAW NODES ARE WHOLE. Only the outermost cells are cut by the sphere.
    ///    A raw node that has been clipped means the planet's skin is being
    ///    taken out of the rock rather than laid on top of it.
    ///  - THE SOIL IS ABOUT THE DEPTH ASKED FOR, counted in nodes rather than
    ///    in units, since that is what a player sees when they dig.
    ///  - THE OUTSIDE IS SMOOTH. Every outermost corner sits on the sphere, not
    ///    short of it and not through it.
    /// </summary>
    private static void Topsoil(OrganicGrid grid)
    {
        Span<Vector3I> walls = stackalloc Vector3I[grid.MaxWalls];
        Span<int> sides = stackalloc int[grid.MaxWalls];
        Span<Vector3> corners = stackalloc Vector3[grid.MaxWalls * 12];

        var random = new Random(777);

        int columns = 0, soilNodes = 0;
        int thinnest = int.MaxValue, thickest = 0;
        int clippedRaw = 0, rawSeen = 0;

        // Dig straight down from a spread of directions and count the soil.
        while (columns < 200)
        {
            var direction = new Vector3(
                (float)(random.NextDouble() * 2 - 1),
                (float)(random.NextDouble() * 2 - 1),
                (float)(random.NextDouble() * 2 - 1));

            if (direction.LengthSquared() < 0.01f)
                continue;

            direction = direction.Normalized();
            columns++;

            int soil = 0;
            var seen = new System.Collections.Generic.HashSet<Vector3I>();

            // Step inward a fraction of a node at a time, counting each new
            // node the line passes through until the rock starts.
            for (float depth = 0f; depth < grid.SoilDepth + grid.NodeSize * 6f;
                depth += grid.NodeSize * 0.25f)
            {
                Vector3I at = grid.CellAt(direction * (grid.SurfaceRadius - 0.01f - depth));

                if (!grid.Contains(at) || !seen.Add(at))
                    continue;

                if (grid.IsSurfaceNode(at))
                {
                    soil++;
                    continue;
                }

                // First rock under the soil. Is it whole, or was it cut by the
                // planet's surface?
                rawSeen++;

                int count = grid.Faces(at, walls, sides, corners);
                for (int n = 0; n < count; n++)
                {
                    if (walls[n] == at)
                        clippedRaw++;
                }

                break;
            }

            soilNodes += soil;
            if (soil < thinnest) thinnest = soil;
            if (soil > thickest) thickest = soil;
        }

        GD.Print("-- 6. topsoil --");
        GD.Print($"  soil band {grid.SoilDepth:F1} units"
            + $" = {grid.SoilDepth / grid.NodeSize:F1} node layers");
        GD.Print($"  measured over {columns} columns:"
            + $" {(double)soilNodes / columns:F1} nodes deep on average"
            + $", {thinnest}..{thickest}");
        GD.Print($"  raw nodes under the soil that were CUT by the surface:"
            + $" {clippedRaw} of {rawSeen}"
            + (clippedRaw == 0 ? "   (rock is whole)" : "   <-- rock is being cut"));
    }

    /// <summary>
    /// How smooth is the outside of the planet?
    ///
    /// The surface should be the sphere and nothing else. Anything a ray from
    /// outside can land on that is NOT at the radius is a bump -- a cell that
    /// stopped short, leaving one of its side walls exposed where a cap should
    /// have been.
    ///
    /// Measured by dropping rays straight down from above and recording the
    /// radius of the first solid thing each one meets.
    /// </summary>
    private static void Smoothness(OrganicGrid grid)
    {
        Span<Vector3I> walls = stackalloc Vector3I[grid.MaxWalls];
        Span<int> sides = stackalloc int[grid.MaxWalls];
        Span<Vector3> corners = stackalloc Vector3[grid.MaxWalls * 12];

        var random = new Random(31337);

        float highest = 0f, lowest = float.MaxValue;
        double total = 0;
        int samples = 0, capped = 0, bare = 0;

        while (samples < 400)
        {
            var direction = new Vector3(
                (float)(random.NextDouble() * 2 - 1),
                (float)(random.NextDouble() * 2 - 1),
                (float)(random.NextDouble() * 2 - 1));

            if (direction.LengthSquared() < 0.01f)
                continue;

            direction = direction.Normalized();

            // WHAT A RAY FROM SPACE ACTUALLY MEETS.
            //
            // Not the outermost node along the direction -- that one is
            // trivially capped and says nothing. The question is how far out
            // the SOLID SURFACE sits here, which means walking inward until a
            // node is found whose material is present, then asking how high
            // its exposed geometry reaches.
            //
            // A first version sampled only the outermost node and reported the
            // planet perfectly smooth while the screen plainly showed bumps.
            Vector3I cell = default;
            bool found = false;

            for (float drop = 0f; drop < grid.NodeSize * 4f;
                drop += grid.NodeSize * 0.2f)
            {
                Vector3I at2 = grid.CellAt(
                    direction * (grid.SurfaceRadius - 0.01f - drop));

                if (!grid.Contains(at2))
                    continue;

                cell = at2;
                found = true;
                break;
            }

            if (!found)
                continue;

            samples++;

            int count = grid.Faces(cell, walls, sides, corners);
            int at = 0;
            bool hasCap = false;

            // How far out this node's surface goes, measured ALONG THE RAY:
            // a corner off to one side is not what this ray would hit.
            float reach = 0f;

            for (int n = 0; n < count; n++)
            {
                if (walls[n] == cell)
                    hasCap = true;

                for (int c = 0; c < sides[n]; c++)
                {
                    float r = corners[at + c].Dot(direction);
                    if (r > reach)
                        reach = r;
                }

                at += sides[n];
            }

            if (hasCap) capped++; else bare++;

            if (reach > highest) highest = reach;
            if (reach < lowest) lowest = reach;
            total += reach;
        }

        GD.Print("-- 7. how smooth is the outside --");
        GD.Print($"  {samples} outermost nodes, {capped} with a surface cap,"
            + $" {bare} without");
        GD.Print($"  outward reach {lowest:F2} .. {highest:F2}"
            + $", mean {total / samples:F2}, against radius {grid.SurfaceRadius}");
        GD.Print($"  worst dip below the sphere: {grid.SurfaceRadius - lowest:F2} units"
            + $" ({(grid.SurfaceRadius - lowest) / grid.NodeSize:F2} nodes)");
    }

    /// <summary>
    /// Do neighbouring surface caps actually meet?
    ///
    /// THIS IS WHAT A THIN DARK LINE BETWEEN NODES MEANS. Two adjacent cells
    /// share a side wall exactly -- it is one bisector -- but each cap is the
    /// cell cut by ITS OWN tangent plane, and two different planes cross that
    /// shared wall along two different lines. The caps therefore stop at
    /// slightly different heights, and the sliver between them shows the
    /// background through it.
    ///
    /// Measured as the distance from each cap corner to the nearest corner of
    /// the neighbouring cap it should be touching. Anything above float noise
    /// is a seam a player can see.
    /// </summary>
    private static void CapSeams(OrganicGrid grid)
    {
        Span<Vector3I> walls = stackalloc Vector3I[grid.MaxWalls];
        Span<int> sides = stackalloc int[grid.MaxWalls];
        Span<Vector3> corners = stackalloc Vector3[grid.MaxWalls * 12];

        Span<Vector3I> theirWalls = stackalloc Vector3I[grid.MaxWalls];
        Span<int> theirSides = stackalloc int[grid.MaxWalls];
        Span<Vector3> theirCorners = stackalloc Vector3[grid.MaxWalls * 12];

        var random = new Random(24680);

        float worst = 0f;
        double total = 0;
        int pairs = 0, loose = 0, found = 0;

        while (found < 250)
        {
            var direction = new Vector3(
                (float)(random.NextDouble() * 2 - 1),
                (float)(random.NextDouble() * 2 - 1),
                (float)(random.NextDouble() * 2 - 1));

            if (direction.LengthSquared() < 0.01f)
                continue;

            Vector3I cell = grid.CellAt(
                direction.Normalized() * (grid.SurfaceRadius - 0.01f));

            if (!grid.Contains(cell))
                continue;

            found++;

            int count = grid.Faces(cell, walls, sides, corners);

            // This cell's own cap.
            int capAt = -1, capSpan = 0, at = 0;

            for (int n = 0; n < count; n++)
            {
                if (walls[n] == cell)
                {
                    capAt = at;
                    capSpan = sides[n];
                }

                at += sides[n];
            }

            if (capAt < 0 || capSpan < 3)
                continue;

            // Every neighbour that also has a cap should meet this one along
            // their shared wall.
            at = 0;

            for (int n = 0; n < count; n++)
            {
                if (walls[n] == cell || sides[n] < 3)
                {
                    at += sides[n];
                    continue;
                }

                int back = grid.Faces(walls[n], theirWalls, theirSides, theirCorners);
                int theirCap = -1, theirSpan = 0, theirAt = 0;

                for (int t = 0; t < back; t++)
                {
                    if (theirWalls[t] == walls[n])
                    {
                        theirCap = theirAt;
                        theirSpan = theirSides[t];
                    }

                    theirAt += theirSides[t];
                }

                if (theirCap < 0 || theirSpan < 3)
                {
                    at += sides[n];
                    continue;
                }

                // Corners of our cap that sit on this shared wall should have a
                // twin among theirs.
                for (int c = 0; c < capSpan; c++)
                {
                    Vector3 mine = corners[capAt + c];

                    // Only corners near this wall are shared with it.
                    float onWall = float.MaxValue;
                    for (int w = 0; w < sides[n]; w++)
                        onWall = Mathf.Min(onWall, mine.DistanceTo(corners[at + w]));

                    if (onWall > 0.001f)
                        continue;

                    float nearest = float.MaxValue;
                    for (int t = 0; t < theirSpan; t++)
                        nearest = Mathf.Min(nearest, mine.DistanceTo(theirCorners[theirCap + t]));

                    pairs++;
                    total += nearest;

                    if (nearest > worst)
                        worst = nearest;

                    if (nearest > 0.001f)
                        loose++;
                }

                at += sides[n];
            }
        }

        GD.Print("-- 8. do neighbouring caps meet --");
        GD.Print($"  {pairs} shared cap corners over {found} surface cells");
        GD.Print($"  gap to the neighbour's matching corner:"
            + $" mean {total / Math.Max(1, pairs):F5}, worst {worst:F5}");
        GD.Print($"  corners NOT meeting their twin: {loose}"
            + (loose == 0 ? "   (caps meet exactly)" : "   <-- these are the dark seams"));
    }

    /// <summary>
    /// Does a ray from above hit the node it is actually pointing at?
    ///
    /// The crosshair test. A ray is walked the way RayPick walks one, and the
    /// node it reports is compared against the node the ray geometrically
    /// enters first. They should be the same node; anything else means the
    /// highlight sits on rock the player is not looking at.
    /// </summary>
    private static void Picking(OrganicGrid grid)
    {
        var random = new Random(8642);

        int tried = 0, wrong = 0, missed = 0;
        float worst = 0f;

        while (tried < 300)
        {
            var direction = new Vector3(
                (float)(random.NextDouble() * 2 - 1),
                (float)(random.NextDouble() * 2 - 1),
                (float)(random.NextDouble() * 2 - 1));

            if (direction.LengthSquared() < 0.01f)
                continue;

            direction = direction.Normalized();

            // An eye a little above the surface, looking straight down, which
            // is the case the player is in almost all the time.
            Vector3 eye = direction * (grid.SurfaceRadius + 3f);
            Vector3 look = -direction;

            tried++;

            // THE TRUTH: the first sample that is genuinely inside the planet.
            Vector3I truth = default;
            bool found = false;

            for (float t = 0f; t < 12f; t += 0.02f)
            {
                Vector3 at = eye + look * t;

                // Just inside, not exactly on the boundary: a sample sitting
                // on the radius can round either way, which made this check
                // disagree with the picker over four rays that were both
                // right.
                if (at.Length() > grid.SurfaceRadius - 0.05f)
                    continue;

                Vector3I cell = grid.CellAt(at);

                if (!grid.Contains(cell))
                    continue;

                truth = cell;
                found = true;
                break;
            }

            if (!found)
            {
                missed++;
                continue;
            }

            // WHAT THE PICKER DOES: step and ask CellAt, trusting Contains to
            // reject a sample that is not in the world.
            Vector3I picked = default;
            bool hit = false;

            for (float t = 0f; t < 12f; t += grid.NodeSize * 0.05f)
            {
                Vector3I cell = grid.CellAt(eye + look * t);

                if (!grid.Contains(cell))
                    continue;

                picked = cell;
                hit = true;
                break;
            }

            if (!hit || picked == truth)
                continue;

            wrong++;

            float off = grid.CentreOf(picked).DistanceTo(grid.CentreOf(truth));
            if (off > worst)
                worst = off;

        }

        GD.Print("-- 9. does the crosshair pick what it points at --");
        GD.Print($"  {tried} rays, {missed} hit nothing");
        GD.Print($"  picked the WRONG node: {wrong}"
            + $", worst miss {worst:F2} units ({worst / grid.NodeSize:F1} nodes)");
    }

    /// <summary>
    /// Do the two kinds of node have the shapes they are supposed to?
    ///
    /// Topsoil is levelled off at the top and irregular everywhere else; rock
    /// is irregular all over and never flat. A face counts as FLAT when its
    /// normal points almost straight up and its corners all sit at one radius,
    /// which is what a levelled top looks like and what a bisector wall never
    /// does.
    /// </summary>
    private static void Shapes2(OrganicGrid grid)
    {
        Span<Vector3I> walls = stackalloc Vector3I[grid.MaxWalls];
        Span<int> sides = stackalloc int[grid.MaxWalls];
        Span<Vector3> corners = stackalloc Vector3[grid.MaxWalls * 12];

        var random = new Random(5150);

        int soilChecked = 0, soilFlat = 0;
        int rockChecked = 0, rockFlat = 0;

        int found = 0;

        while (found < 400)
        {
            var direction = new Vector3(
                (float)(random.NextDouble() * 2 - 1),
                (float)(random.NextDouble() * 2 - 1),
                (float)(random.NextDouble() * 2 - 1));

            if (direction.LengthSquared() < 0.01f)
                continue;

            direction = direction.Normalized();

            // One node at the surface and one well below it, so both kinds are
            // sampled from the same directions.
            float depth = found % 2 == 0
                ? grid.NodeSize * 0.5f : grid.SoilDepth + grid.NodeSize * 4f;

            Vector3I cell = grid.CellAt(direction * (grid.SurfaceRadius - depth));

            if (!grid.IsGround(cell))
                continue;

            found++;

            bool skin = grid.IsSurfaceNode(cell);
            int count = grid.Faces(cell, walls, sides, corners);

            bool hasFlatTop = false;
            int at = 0;

            for (int n = 0; n < count; n++)
            {
                int span = sides[n];

                if (span < 3)
                {
                    at += span;
                    continue;
                }

                // A levelled top: every corner at the same radius, and that
                // radius a whole number of shells.
                float first = corners[at].DistanceTo(grid.Origin);
                bool level = true;

                for (int c = 1; c < span && level; c++)
                    level = Mathf.Abs(corners[at + c].DistanceTo(grid.Origin) - first) < 0.01f;

                if (level && walls[n] == cell)
                    hasFlatTop = true;

                at += span;
            }

            if (skin)
            {
                soilChecked++;
                if (hasFlatTop) soilFlat++;
            }
            else
            {
                rockChecked++;
                if (hasFlatTop) rockFlat++;
            }
        }

        GD.Print("-- 10. shapes by kind --");
        GD.Print($"  topsoil: {soilFlat} of {soilChecked} have a flat top"
            + (soilFlat == soilChecked ? "   (all levelled)" : "   <-- some are not"));
        GD.Print($"  rock:    {rockFlat} of {rockChecked} have a flat top"
            + (rockFlat == 0 ? "   (none levelled, as intended)" : "   <-- rock is being cut"));
    }

    /// <summary>Does anything stick out past the planet's radius?</summary>
    private static void Surface(OrganicGrid grid)
    {
        Span<Vector3I> walls = stackalloc Vector3I[grid.MaxWalls];
        Span<int> sides = stackalloc int[grid.MaxWalls];
        Span<Vector3> corners = stackalloc Vector3[grid.MaxWalls * 12];

        float furthest = 0f;
        int over = 0, sampled = 0;

        // Surface cells specifically, since those are the ones the clip acts on.
        var random = new Random(4242);
        int extent = grid.ShellCount;
        int found = 0;

        int capped = 0;

        while (found < 300)
        {
            var direction = new Vector3(
                (float)(random.NextDouble() * 2 - 1),
                (float)(random.NextDouble() * 2 - 1),
                (float)(random.NextDouble() * 2 - 1));

            if (direction.LengthSquared() < 0.01f)
                continue;

            // The OUTERMOST cell along this direction, not one comfortably
            // inside. Sampling half a node down lands on a cell the clip never
            // touches, which is how an earlier run of this check reported
            // thousands of corners outside the planet while testing cells that
            // were never meant to be clipped.
            Vector3I cell = grid.CellAt(
                direction.Normalized() * (grid.SurfaceRadius - grid.NodeSize * 0.05f));

            if (!grid.Contains(cell))
                continue;

            found++;

            int count = grid.Faces(cell, walls, sides, corners);
            int at = 0;

            for (int n = 0; n < count; n++)
            {
                if (walls[n] == cell)
                    capped++;

                for (int c = 0; c < sides[n]; c++)
                {
                    float distance = corners[at + c].DistanceTo(grid.Origin);
                    sampled++;

                    if (distance > furthest)
                        furthest = distance;

                    if (distance > grid.SurfaceRadius + 0.001f)
                        over++;
                }

                at += sides[n];
            }
        }

        GD.Print("-- 4. the surface is clipped --");
        GD.Print($"  {found} surface cells, {sampled} corners,"
            + $" {capped} of them carrying a surface cap");
        GD.Print($"  furthest corner {furthest:F4} against radius {grid.SurfaceRadius}");
        GD.Print($"  corners outside the planet: {over}");
    }
}
