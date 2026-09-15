using Godot;
using System;

namespace GameBase.Nodes;

/// <summary>
/// The even-node planet as an addressable grid: where nodes are, and how to get
/// from one to the next.
///
/// The counterpart to <see cref="QuadSphereGrid"/>, and deliberately the same
/// shape so the rest of the engine does not care which it is talking to. A cell
/// is a <see cref="Vector3I"/> read as (i, j, shell) with the base face folded
/// into i, exactly as the quad grid folds its face into u.
///
/// WHAT A NODE IS HERE
///
/// A site, and the region of space closer to it than to any other site -- its
/// Voronoi cell. That is the same thing as saying every wall is the
/// perpendicular bisector of the line to a neighbouring site, which is the
/// construction this grid exists to provide.
///
/// Because every site on a shell is at the same radius, a bisector plane
/// between two of them passes through the planet's centre. So side walls are
/// RADIAL -- great-circle arcs -- and meet the floor and ceiling at right
/// angles. A node is a hexagonal prism whose sides point at the core, which is
/// what makes the surface read as even.
///
/// The twelve icosahedral vertices are pentagonal prisms instead. There is no
/// way around that (see <see cref="IcoSphere"/>); what matters is that they are
/// twelve cells rather than a defect smeared over the whole world.
/// </summary>
public sealed class IcoSphereGrid : INodeGrid
{
    private readonly IcoSphereTables _tables;
    private readonly Vector3 _origin;
    private readonly int _stride;

    public IcoSphereGrid(float surfaceRadius, float nodeSize, Vector3 origin)
        : this(new IcoSphereTables(surfaceRadius, nodeSize), origin)
    {
    }

    public IcoSphereGrid(IcoSphereTables tables, Vector3 origin)
    {
        _tables = tables;
        _origin = origin;

        // One row of the packed address per base face, sized by the FINEST
        // shell so a face always occupies the same span of i whatever level it
        // is drawn at -- the same trick the quad grid uses to keep unpacking a
        // division by a constant.
        //
        // ROUNDED UP TO A WHOLE NUMBER OF CHUNKS. A face needs divisions + 1
        // lattice columns, which is one past a power of two and so never
        // divides by the chunk size. Leaving it at that makes every face start
        // partway into a chunk, so faces SHARE chunks, a chunk straddles a
        // fold, and the streamer touches far more chunks than there is world in
        // them -- measured at 832 resident chunks against the cube planet's
        // 216, for a comparable amount of rock.
        //
        // The quad grid states the same rule for the same reason: "a face is
        // always a whole number of chunks across and no chunk straddles a face
        // fold". The few unused columns cost nothing, because a chunk outside
        // the face's triangle is rejected before it is ever generated.
        int needed = tables.SurfaceDivisions + 1;
        int chunk = NodeChunkStore.ChunkSize;

        _stride = (needed + chunk - 1) / chunk * chunk;
    }

    public IcoSphereTables Tables => _tables;
    public Vector3 Origin => _origin;
    public float SurfaceRadius => _tables.SurfaceRadius;
    public float NodeSize => _tables.NodeSize;
    public int ShellCount => _tables.ShellCount;

    /// <summary>Span of one base face in the packed i axis.</summary>
    public int Stride => _stride;

    // ------------------------------------------------------------ addressing

    /// <summary>Packs a site address into the Vector3I the engine passes
    /// around.</summary>
    public Vector3I Pack(int face, int i, int j, int shell) =>
        new(face * _stride + i, j, shell);

    /// <summary>
    /// The one address a site is known by.
    ///
    /// A SITE ON A FACE EDGE BELONGS TO EVERY FACE THAT TOUCHES IT. Two faces
    /// share a whole edge of lattice points, and five meet at each icosahedral
    /// vertex, so <see cref="Pack"/> can name the same physical node up to five
    /// different ways. At level 4 that is 462 sites out of 2562.
    ///
    /// Left alone, that is not a cosmetic problem -- it breaks storage. The
    /// chunk store is keyed by address, so a block dug under one address would
    /// still be solid under another, a node would mesh twice, and two "adjacent"
    /// nodes could sit at zero distance from each other. This check's first run
    /// showed exactly that: a spacing of 0.0000 and a round-trip that called
    /// 76% of lookups wrong when the geometry was in fact right.
    ///
    /// So every address is reduced to the lowest-numbered face that can express
    /// it. Cheap, total, and it makes address equality mean node identity again.
    /// </summary>
    public Vector3I Canonical(Vector3I cell)
    {
        Unpack(cell, out int face, out int i, out int j, out int shell);

        if ((uint)shell >= (uint)_tables.ShellCount)
            return cell;

        int divisions = _tables.Divisions(shell);

        // Interior points belong to one face only, which is the overwhelming
        // majority of them -- so the test that skips the work comes first.
        if (i > 0 && j > 0 && i + j < divisions)
            return cell;

        Vector3 direction = IcoSphere.PointOn(face, i, j, divisions);

        for (int other = 0; other < face; other++)
        {
            if (!IcoSphere.FacesAdjacent(face, other))
                continue;

            SiteOn(other, direction, divisions, out int oi, out int oj, out float error);

            // Only an exact hit is the same site; a near miss is a different
            // node on a neighbouring face.
            if (error < 0.000001f)
                return Pack(other, oi, oj, shell);
        }

        return Pack(face, i, j, shell);
    }

    /// <summary>Recovers a site address from its packed form.</summary>
    public void Unpack(Vector3I cell, out int face, out int i, out int j, out int shell)
    {
        face = cell.X >= 0
            ? cell.X / _stride
            : (cell.X - _stride + 1) / _stride;

        i = cell.X - face * _stride;
        j = cell.Y;
        shell = cell.Z;
    }

    /// <summary>
    /// Is this an address the grid contains?
    ///
    /// A lattice point is inside its face when i and j are non-negative and
    /// their sum does not pass the edge -- the triangle inequality the flat
    /// (i, j) lattice inherits from the face it covers.
    /// </summary>
    public bool Contains(Vector3I cell)
    {
        int shell = cell.Z;
        if ((uint)shell >= (uint)_tables.ShellCount)
            return false;

        Unpack(cell, out int face, out int i, out int j, out _);

        if ((uint)face >= IcoSphere.FaceCount)
            return false;

        int divisions = _tables.Divisions(shell);
        return i >= 0 && j >= 0 && i + j <= divisions;
    }

    // -------------------------------------------------------------- geometry

    /// <summary>The unit direction of a site.</summary>
    public Vector3 DirectionOf(Vector3I cell)
    {
        Unpack(cell, out int face, out int i, out int j, out int shell);

        int clamped = Mathf.Clamp(shell, 0, _tables.ShellCount - 1);
        return IcoSphere.PointOn(
            Mathf.Clamp(face, 0, IcoSphere.FaceCount - 1),
            i, j, _tables.Divisions(clamped));
    }

    /// <summary>
    /// The world position of a node's centre: its site direction, at the middle
    /// of its shell.
    /// </summary>
    public Vector3 CentreOf(Vector3I cell)
    {
        Unpack(cell, out _, out _, out _, out int shell);

        int clamped = Mathf.Clamp(shell, 0, _tables.ShellCount - 1);
        float radius = _tables.Radius(clamped) - _tables.NodeSize * 0.5f;

        return _origin + DirectionOf(cell) * radius;
    }

    /// <summary>The direction a node calls UP: away from the centre.</summary>
    public Vector3 UpAt(Vector3I cell) => DirectionOf(cell);

    /// <summary>The outer radius of a node's shell.</summary>
    public float OuterRadius(Vector3I cell)
    {
        int shell = Mathf.Clamp(cell.Z, 0, _tables.ShellCount - 1);
        return _tables.Radius(shell);
    }

    /// <summary>The inner radius of a node's shell.</summary>
    public float InnerRadius(Vector3I cell) => OuterRadius(cell) - _tables.NodeSize;

    /// <summary>
    /// The node containing a world point.
    ///
    /// Returns a NEGATIVE shell for a point above the surface rather than
    /// clamping, for the same reason the quad grid does: a point in the sky
    /// belongs to no node, and saying so is what stops a ray from the player's
    /// eye reporting a hit before it has travelled anywhere.
    /// </summary>
    public Vector3I CellAt(Vector3 point)
    {
        Vector3 d = point - _origin;
        float distance = d.Length();

        if (distance < 0.0001f)
            return Pack(0, 0, 0, _tables.ShellCount);

        int shell = Mathf.FloorToInt(
            (_tables.SurfaceRadius - distance) / _tables.NodeSize);

        Vector3 direction = d / distance;
        int clamped = Mathf.Clamp(shell, 0, _tables.ShellCount - 1);

        SiteAt(direction, _tables.Divisions(clamped), out int face, out int i, out int j);

        // Canonical, so the node a position maps to is the same node the store
        // and the mesher know it by.
        return Canonical(Pack(face, i, j, shell));
    }

    /// <summary>
    /// The nearest lattice site to a direction, at a given subdivision.
    ///
    /// The Voronoi cell of a point on a triangular lattice is the hexagon
    /// around it, so "inside the cell" and "nearest site" are the same
    /// question -- which is why this can be a rounding rather than a search.
    /// Rounding barycentric coordinates to the lattice lands on one of three
    /// candidates; the nearest of those is the answer.
    /// </summary>
    public void SiteAt(Vector3 direction, int divisions,
        out int face, out int i, out int j)
    {
        face = IcoSphere.FaceOf(direction);
        SiteOn(face, direction, divisions, out i, out j, out float error);

        // A DIRECTION NEAR AN EDGE MAY BELONG TO THE NEIGHBOURING FACE.
        //
        // FaceOf picks the face whose centroid is nearest, which is right for
        // the interior and unreliable within a cell's width of an edge -- and a
        // site ON an edge is shared by two faces, so one of them is reached
        // only by asking. Rounding inside the wrong triangle lands a lookup on
        // the wrong site, which is what put this check's round-trip at 24%.
        //
        // So the rounded candidate is measured against the direction it came
        // from, and the neighbouring faces are tried whenever it is not a
        // convincing fit. The interior case takes the fast path and stops.
        if (error <= 0.0001f)
            return;

        for (int other = 0; other < IcoSphere.FaceCount; other++)
        {
            if (other == face)
                continue;

            // Only faces that actually touch this one can hold the answer.
            if (!IcoSphere.FacesAdjacent(face, other))
                continue;

            SiteOn(other, direction, divisions, out int oi, out int oj, out float oerror);

            if (oerror >= error)
                continue;

            error = oerror;
            face = other;
            i = oi;
            j = oj;
        }
    }

    /// <summary>
    /// Rounds a direction to the nearest lattice site of one face, and reports
    /// how far off that site actually is.
    ///
    /// The error is what lets the caller compare candidates from two faces: the
    /// angular distance from the direction asked about to the site chosen.
    /// </summary>
    private static void SiteOn(int face, Vector3 direction, int divisions,
        out int i, out int j, out float error)
    {
        // A FIRST GUESS FROM THE FLAT TRIANGLE, THEN A LOCAL WALK.
        //
        // The spherical placement has no closed-form inverse worth writing.
        // Measuring the angle from a face corner and scaling it looks like one
        // and is not: the arc from that corner to the opposite edge is a
        // different length in every direction, so the ratio is not linear in
        // the lattice index. Doing exactly that put every lookup one step low
        // in both axes -- a site at (5,5) resolving to (4,4) -- which is how
        // this check's round-trip sat at 24% while the geometry underneath was
        // perfectly sound.
        //
        // The flat barycentric IS a good first guess, always within a step or
        // two, so the fix is to start there and walk downhill to the true
        // nearest site. Bounded, exact, and a handful of dot products.
        IcoSphere.FlatBarycentric(face, direction, out float a, out float b, out float c);

        float fb = b * divisions;
        float fc = c * divisions;
        float fa = a * divisions;

        int ri = Mathf.RoundToInt(fb);
        int rj = Mathf.RoundToInt(fc);
        int ra = Mathf.RoundToInt(fa);

        if (ri + rj + ra != divisions)
        {
            float db = Mathf.Abs(ri - fb);
            float dc = Mathf.Abs(rj - fc);
            float da = Mathf.Abs(ra - fa);

            if (db > dc && db > da)
                ri = divisions - rj - ra;
            else if (dc > da)
                rj = divisions - ri - ra;
        }

        i = Mathf.Clamp(ri, 0, divisions);
        j = Mathf.Clamp(rj, 0, divisions - i);
        error = 1f - IcoSphere.PointOn(face, i, j, divisions).Dot(direction);

        // Walk to the best neighbour until none is better. The lattice is
        // convex and the measure is a distance, so this cannot cycle; the guess
        // is close enough that it takes a couple of steps at most.
        for (int guard = 0; guard < 8; guard++)
        {
            int bestI = i, bestJ = j;
            float best = error;

            for (int n = 0; n < MaxLateral; n++)
            {
                Vector2I step = LateralSteps[n];
                int ni = i + step.X;
                int nj = j + step.Y;

                if (ni < 0 || nj < 0 || ni + nj > divisions)
                    continue;

                float candidate =
                    1f - IcoSphere.PointOn(face, ni, nj, divisions).Dot(direction);

                if (candidate >= best)
                    continue;

                best = candidate;
                bestI = ni;
                bestJ = nj;
            }

            if (bestI == i && bestJ == j)
                return;

            i = bestI;
            j = bestJ;
            error = best;
        }
    }

    // ------------------------------------------------------------ neighbours

    /// <summary>
    /// The six lateral steps on a triangular lattice, in (di, dj).
    ///
    /// A hexagon's six walls. The twelve pentagon sites have only five
    /// neighbours, and one of these steps leaves the lattice there -- which
    /// <see cref="Neighbour"/> reports rather than papering over.
    /// </summary>
    public static readonly Vector2I[] LateralSteps =
    {
        new(1, 0), new(1, -1), new(0, -1),
        new(-1, 0), new(-1, 1), new(0, 1),
    };

    /// <summary>How many lateral neighbours a node has: six, or five at a
    /// pentagon.</summary>
    public const int MaxLateral = 6;

    /// <summary>
    /// The node one step away.
    ///
    /// `lateral` indexes <see cref="LateralSteps"/>, or is negative for a purely
    /// radial step. `dOut` steps radially, positive meaning OUTWARD toward the
    /// sky -- the same convention <see cref="QuadSphereGrid.Neighbour"/> takes,
    /// so callers that already think in a local +Z-is-up frame need no change.
    ///
    /// Returns false when there is no such node: past the surface, inside the
    /// core, or the sixth wall of a pentagon.
    /// </summary>
    public bool Neighbour(Vector3I cell, int lateral, int dOut, out Vector3I result)
    {
        Unpack(cell, out int face, out int i, out int j, out int shell);
        result = default;

        int target = shell - dOut;

        if ((uint)target >= (uint)_tables.ShellCount)
            return false;

        if ((uint)shell >= (uint)_tables.ShellCount)
            return false;

        int here = _tables.Divisions(shell);
        int there = _tables.Divisions(target);

        int ni = i;
        int nj = j;

        if (lateral >= 0)
        {
            Vector2I step = LateralSteps[lateral % MaxLateral];
            ni += step.X;
            nj += step.Y;
        }

        // STILL ON THIS FACE: pure arithmetic, and the common case.
        if (ni >= 0 && nj >= 0 && ni + nj <= here)
        {
            if (here != there)
            {
                // Across a band. Levels are powers of two, so this is a shift,
                // and because subdivision only ever appends sites, a site that
                // exists at both levels keeps its exact position.
                if (!Rescale(ref ni, ref nj, here, there))
                    return false;
            }

            result = Canonical(Pack(face, ni, nj, target));
            return true;
        }

        // OFF THE EDGE. Found geometrically rather than from an adjacency
        // table: take the direction one step past this face's edge and ask
        // which face and site own it. That cannot disagree with the
        // projection's own layout, which a hand-written table could.
        //
        // The step is measured from a real neighbour INSIDE the face and
        // reflected outward, because the spherical placement interpolates
        // between the face's corners and extrapolating it past them wanders off
        // the sphere's lattice entirely.
        //
        // The inward neighbour must be the step's OPPOSITE, not a clamp of the
        // step itself: clamping an off-edge index returns the site we started
        // from, so the reflection overshoots to a full step beyond the edge.
        // That put edge neighbours at exactly twice the correct distance --
        // (0,0,0) answering (0,2,0) -- and showed up here as a spacing spread
        // of precisely 2.0000.
        // The site is on this face's boundary, and the step leaves it. Rather
        // than guess a direction and round -- which lands on the wrong face
        // near a corner, where five faces meet within a cell's width -- the
        // answer is found on the neighbouring faces THEMSELVES.
        //
        // Every face that shares this site is asked for the site one step away
        // in the same physical direction, and the nearest real answer wins.
        // That is exact at edges and corners alike, because it never leaves the
        // lattice to ask the question.
        Vector3 from = IcoSphere.PointOn(face, i, j, here);

        // The direction the step was heading, as a tangent at this site. Taken
        // from the in-face neighbour opposite the step so it is always a real
        // lattice direction.
        Vector2I taken = lateral >= 0 ? LateralSteps[lateral % MaxLateral] : Vector2I.Zero;

        int backI = i - taken.X;
        int backJ = j - taken.Y;

        Vector3 heading;
        if (backI >= 0 && backJ >= 0 && backI + backJ <= here)
            heading = (from - IcoSphere.PointOn(face, backI, backJ, here)).Normalized();
        else
            heading = (from - IcoSphere.FaceCentre(face)).Normalized();

        Vector3I candidate = default;
        float best = float.MaxValue;
        bool found = false;

        for (int other = 0; other < IcoSphere.FaceCount; other++)
        {
            if (!IcoSphere.FacesAdjacent(face, other))
                continue;

            // Where this site sits on that face. If it is not on that face at
            // all, it is not a route to the neighbour.
            SiteOn(other, from, there, out int oi, out int oj, out float error);
            if (error > 0.000001f)
                continue;

            for (int n = 0; n < MaxLateral; n++)
            {
                Vector2I step = LateralSteps[n];
                int ci = oi + step.X;
                int cj = oj + step.Y;

                if (ci < 0 || cj < 0 || ci + cj > there)
                    continue;

                Vector3 at = IcoSphere.PointOn(other, ci, cj, there);

                // Must be a step in the direction asked for, not back the way
                // we came or along the edge.
                Vector3 offset = at - from;
                if (offset.LengthSquared() < 0.0000001f)
                    continue;

                if (offset.Normalized().Dot(heading) < 0.5f)
                    continue;

                float distance = offset.LengthSquared();
                if (distance >= best)
                    continue;

                best = distance;
                candidate = Canonical(Pack(other, ci, cj, target));
                found = true;
            }
        }

        if (!found)
            return false;

        // A pentagon has five walls, so one of the six steps lands back on the
        // site it started from. Reporting that as a neighbour would make a node
        // appear to touch itself, and the mesher would cull a wall that should
        // have been drawn.
        //
        // Compared canonically on BOTH sides: the caller may well have handed
        // in an edge site under a different face's address, and a raw compare
        // would miss the match and hand a node itself as its own neighbour.
        if (candidate == Canonical(cell))
            return false;

        result = candidate;
        return true;
    }

    /// <summary>
    /// Maps lattice coordinates between two subdivision levels.
    ///
    /// Going inward the lattice is coarser, so only every other site survives:
    /// a site whose indices are not both even has no counterpart, and the
    /// caller is told so rather than being handed a rounded neighbour.
    /// </summary>
    private static bool Rescale(ref int i, ref int j, int here, int there)
    {
        if (there < here)
        {
            int ratio = here / there;
            if (i % ratio != 0 || j % ratio != 0)
                return false;

            i /= ratio;
            j /= ratio;
            return true;
        }

        int factor = there / here;
        i *= factor;
        j *= factor;
        return true;
    }

    // ----------------------------------------------------------- voronoi cell

    /// <summary>
    /// The corners of a node's top face, in world space, counter-clockwise seen
    /// from outside.
    ///
    /// THIS IS THE BISECTOR CONSTRUCTION. Each corner is the point equidistant
    /// from this site and two consecutive neighbours -- the circumcentre of
    /// that triple -- which is exactly where three bisector planes meet. Walls
    /// built between consecutive corners therefore lie ON the perpendicular
    /// bisector of the line to each neighbour, so two adjacent nodes share a
    /// wall exactly and no gap or overlap is possible.
    ///
    /// Returns how many corners were written: six for a hexagon, five at one of
    /// the twelve pentagons.
    /// </summary>
    public int TopCorners(Vector3I cell, Span<Vector3> corners)
    {
        return CornersAtRadius(cell, OuterRadius(cell), corners);
    }

    /// <summary>The corners of a node's bottom face, same order as
    /// <see cref="TopCorners"/>.</summary>
    public int BottomCorners(Vector3I cell, Span<Vector3> corners)
    {
        return CornersAtRadius(cell, InnerRadius(cell), corners);
    }

    /// <summary>
    /// The node's Voronoi corners projected to one radius.
    ///
    /// Every corner is a direction first and a position second, so the top and
    /// bottom faces of a node are the same polygon at two radii -- which is
    /// what makes the side walls exactly radial.
    /// </summary>
    private int CornersAtRadius(Vector3I cell, float radius, Span<Vector3> corners)
    {
        Span<Vector3> ring = stackalloc Vector3[MaxLateral];
        Span<Vector3I> cells = stackalloc Vector3I[MaxLateral];

        int count = NeighbourRing(cell, cells, ring);

        if (count < 3)
            return 0;

        Vector3 centre = DirectionOf(cell);
        int written = 0;

        // CORNER n SITS BETWEEN NEIGHBOUR n-1 AND NEIGHBOUR n.
        //
        // Offset by one so that wall w -- the wall facing neighbour w -- spans
        // corner w to corner w+1, which is what INodeGrid promises and what the
        // mesher builds its side quads from. Pairing neighbour n with n+1
        // instead puts every corner half a cell around the ring, and then no
        // wall lines up with the node behind it: the contract check saw all
        // 23052 walls unshared.
        for (int n = 0; n < count; n++)
        {
            Vector3 a = ring[(n + count - 1) % count];
            Vector3 b = ring[n];

            // THE CORNER IS WHERE THREE BISECTOR PLANES MEET.
            //
            // The bisector of two sites on a common sphere is the plane through
            // the origin with normal (a - centre) -- every point on it is
            // equidistant from the two. Two such planes cross along a line, and
            // that line pierces the sphere at the corner shared by the three
            // cells.
            //
            // Written as the cross product of the two plane NORMALS rather than
            // of the chords themselves. The chord form names the same point but
            // collapses when the three sites are nearly equally spaced, which
            // on a near-regular lattice is almost everywhere -- it returned a
            // vector of length ~1e-8, failed the degeneracy guard, and left
            // every cell with no corners at all.
            Vector3 corner = (a - centre).Cross(b - centre);

            float length = corner.Length();

            // Genuinely collinear sites have no corner between them. Kept as a
            // DEGENERATE corner rather than skipped: dropping it here would
            // leave fewer corners than walls, and every caller pairs the two by
            // index. Falling back to the midpoint keeps the polygon closed and
            // the indices aligned, and the sliver it leaves is below a
            // float's resolution anyway.
            if (length < 1e-12f)
            {
                Vector3 fallback = (a + b) * 0.5f;

                corner = fallback.LengthSquared() > 1e-12f
                    ? fallback.Normalized() : centre;

                length = 1f;
            }

            corner /= length;

            // The line pierces the sphere twice; take the end on the same side
            // as the cell.
            if (corner.Dot(centre) < 0f)
                corner = -corner;

            corners[written++] = _origin + corner * radius;
        }

        return written;
    }

    // ------------------------------------------------------------- INodeGrid

    /// <summary>
    /// How many side walls this node has: six for a hexagon, five at one of the
    /// twelve pentagons.
    /// </summary>
    public int WallCount(Vector3I cell)
    {
        Unpack(cell, out _, out int i, out int j, out int shell);

        if ((uint)shell >= (uint)_tables.ShellCount)
            return 0;

        // An interior node always has six, and saying so costs an unpack and
        // two comparisons. Building the ring to count its members is the same
        // work as building the geometry, and the mesher asks for this before it
        // asks for anything else -- so the cheap answer is worth having even
        // though the ring behind it is cached.
        int divisions = _tables.Divisions(shell);

        if (i > 1 && j > 1 && i + j < divisions - 1)
            return MaxLateral;

        Span<Vector3> ring = stackalloc Vector3[MaxLateral];
        Span<Vector3I> cells = stackalloc Vector3I[MaxLateral];

        // On a boundary the count is whatever the ring turns out to be: the
        // ring drops any step that is not a true triangulation neighbour, so a
        // node has exactly as many walls as it has corners.
        return NeighbourRing(cell, cells, ring);
    }

    /// <summary>The most walls any node here has.</summary>
    public int MaxWalls => MaxLateral;

    /// <summary>
    /// The node past one of this node's side walls.
    ///
    /// Wall `w` runs between corner `w` and corner `w + 1`, and the corners are
    /// built from the neighbour ring in the same order -- so the wall index and
    /// the neighbour index are the same index. A node with five walls simply
    /// has one fewer entry, not a gap.
    /// </summary>
    public bool WallNeighbour(Vector3I cell, int wall, out Vector3I result) =>
        RingNeighbour(cell, wall, out result);

    /// <summary>The node one shell outward (positive) or inward.</summary>
    public bool RadialNeighbour(Vector3I cell, int dOut, out Vector3I result) =>
        Neighbour(cell, -1, dOut, out result);

    /// <summary>
    /// The chunk one step away, for the streamer's flood fill.
    ///
    /// A chunk is a block of lattice indices, so a lateral step is taken from
    /// the chunk's middle cell and the chunk it lands in is the answer -- which
    /// crosses a face fold correctly because the cell step does.
    /// </summary>
    public bool NeighbourChunk(Vector3I chunk, Vector3I direction, out Vector3I result)
    {
        const int Size = NodeChunkStore.ChunkSize;
        const int Half = Size / 2;

        Vector3I origin = NodeChunkStore.OriginOf(chunk);
        var from = new Vector3I(origin.X + Half, origin.Y + Half, origin.Z + Half);

        // Radial steps are plain arithmetic on the shell axis.
        if (direction.Z != 0)
        {
            var at = new Vector3I(from.X, from.Y, from.Z + direction.Z * Size);

            if ((uint)at.Z >= (uint)_tables.ShellCount)
            {
                result = default;
                return false;
            }

            result = NodeChunkStore.ChunkOf(at);
            return true;
        }

        // Lateral: walk a whole chunk's width through the lattice, so a step
        // that leaves the face is resolved by the grid rather than by adding to
        // an index that means something different over there.
        Vector3I walk = from;

        for (int step = 0; step < Size; step++)
        {
            int lateral = LateralFor(direction);
            if (lateral < 0 || !Neighbour(walk, lateral, 0, out walk))
            {
                result = default;
                return false;
            }
        }

        result = NodeChunkStore.ChunkOf(walk);
        return true;
    }

    /// <summary>
    /// The lateral step nearest a chunk-space direction.
    ///
    /// The streamer thinks in six axis directions because that is what a chunk
    /// lattice offers; the grid has six lattice steps. Mapping the four that
    /// matter is enough to flood the surface.
    /// </summary>
    private static int LateralFor(Vector3I direction)
    {
        if (direction.X > 0) return 0;
        if (direction.X < 0) return 3;
        if (direction.Y > 0) return 5;
        if (direction.Y < 0) return 2;
        return -1;
    }

    /// <summary>
    /// The directions of a node's lateral neighbours, sorted into ring order.
    ///
    /// Ring order is what makes consecutive pairs actually adjacent, so the
    /// corner between them is a real corner of the cell.
    ///
    /// SORTED BY ANGLE, NOT TAKEN FROM THE STEP ORDER.
    /// <see cref="LateralSteps"/> runs around the ring on a node's OWN face,
    /// and a node on a seam has neighbours on another face whose lattice is
    /// rotated against this one -- so they come back out of sequence, and the
    /// corners built from consecutive pairs end up threaded across the cell
    /// rather than around it. Measured before sorting: 924 of 24000 walls did
    /// not line up with the node behind them, all of them on seams.
    ///
    /// Sorting costs a handful of atan2 calls on a cell that is meshed once.
    /// </summary>
    private int NeighbourDirections(Vector3I cell, Span<Vector3> ring)
    {
        int count = 0;

        for (int n = 0; n < MaxLateral; n++)
        {
            if (!Neighbour(cell, n, 0, out Vector3I at))
                continue;

            ring[count++] = DirectionOf(at);
        }

        if (count > 2)
            SortRing(DirectionOf(cell), ring, count);

        return count;
    }

    /// <summary>
    /// A node's neighbours in ring order, as addresses.
    ///
    /// THE RING MUST BE A TRIANGULATION RING. Consecutive members have to be
    /// adjacent to EACH OTHER as well as to the centre, because a corner is the
    /// point equidistant from the centre and one such pair. Two neighbouring
    /// nodes then derive their shared wall from the same two triples and put it
    /// in exactly the same place.
    ///
    /// Sorting by angle alone does not guarantee that. A seam node's six
    /// lattice steps can return a site that is near it but not adjacent on the
    /// triangulation, and the angular sort happily seats that impostor between
    /// two real neighbours -- so the corners either side of it are computed
    /// from a triple that the neighbour does not share, and the wall lands
    /// somewhere else. That was 922 of 24000 walls, every one of them on a
    /// seam, all with adjacency reported correctly in both directions.
    ///
    /// So after sorting, any member not adjacent to the one before it is
    /// dropped. What remains is a genuine ring.
    /// </summary>
    private int NeighbourRing(Vector3I cell, Span<Vector3I> cells, Span<Vector3> ring)
    {
        // SERVED FROM A ONE-ENTRY CACHE.
        //
        // Meshing asks for the same node's ring nine times over -- once for the
        // top cap, once for the bottom, once for the wall count, and once per
        // wall to find what is behind it -- and building it is by far the most
        // expensive thing this grid does. Caching the last answer collapses
        // those nine into one, because they arrive back to back.
        //
        // Per THREAD, because chunks are generated and meshed on workers and a
        // shared cache would need a lock that costs more than the hit saves.
        RingCache cached = _ringCache;

        if (cached == null)
        {
            cached = new RingCache();
            _ringCache = cached;
        }

        if (cached.Owner == this && cached.Valid && cached.Cell == cell)
        {
            for (int n = 0; n < cached.Count; n++)
            {
                cells[n] = cached.Cells[n];
                ring[n] = cached.Ring[n];
            }

            return cached.Count;
        }

        int built = BuildRing(cell, cells, ring);

        cached.Owner = this;
        cached.Cell = cell;
        cached.Count = built;
        cached.Valid = true;

        for (int n = 0; n < built; n++)
        {
            cached.Cells[n] = cells[n];
            cached.Ring[n] = ring[n];
        }

        return built;
    }

    /// <summary>
    /// The last ring built on one thread.
    ///
    /// Keyed by the grid that built it as well as by the cell: a rebuilt planet
    /// is a new grid where every address means something else, and a stale
    /// entry would hand it the old world's neighbours.
    /// </summary>
    private sealed class RingCache
    {
        public IcoSphereGrid Owner;
        public Vector3I Cell;
        public int Count;
        public bool Valid;
        public readonly Vector3I[] Cells = new Vector3I[MaxLateral];
        public readonly Vector3[] Ring = new Vector3[MaxLateral];
    }

    /// <summary>
    /// This thread's ring cache.
    ///
    /// Thread-static rather than shared: meshing runs on worker threads, and a
    /// cache they contended over would cost more in synchronisation than the
    /// lookup saves.
    /// </summary>
    [ThreadStatic]
    private static RingCache _ringCache;

    /// <summary>Builds a node's neighbour ring from scratch.</summary>
    private int BuildRing(Vector3I cell, Span<Vector3I> cells, Span<Vector3> ring)
    {
        Unpack(cell, out int face, out int i, out int j, out int shell);

        if ((uint)shell >= (uint)_tables.ShellCount)
            return 0;

        int divisions = _tables.Divisions(shell);

        // THE INTERIOR FAST PATH.
        //
        // A node whose six lattice steps all stay on its own face has no seam
        // to resolve: its neighbours are plain arithmetic, they are already in
        // ring order, and none of them needs canonicalising. That is the
        // overwhelming majority of nodes, and taking it here avoids the
        // candidate gather entirely -- which is 42 Neighbour calls, each of
        // which can search all twenty faces.
        if (i > 1 && j > 1 && i + j < divisions - 1)
        {
            // TAKEN IN ORDER, NOT SORTED.
            //
            // LateralSteps already runs around the ring on a face's own
            // lattice, and its handedness is the SAME on all twenty faces and
            // at every interior node -- measured, not assumed. So the order is
            // known ahead of time and needs no angular sort, which was six
            // atan2 plus seven normalisations on the hottest path in the grid,
            // paid per node for an answer that never varies.
            //
            // The order is used AS WRITTEN. Reversing it here -- on a reading
            // of the measured sign that turned out to be backwards -- flipped
            // every cap on the planet to face the core, and the surface went
            // invisible from above again. MeshAudit's outward count is what
            // catches that, and it is the check to run after touching this.
            for (int n = 0; n < MaxLateral; n++)
            {
                Vector2I step = LateralSteps[n];
                int ni = i + step.X;
                int nj = j + step.Y;

                cells[n] = Pack(face, ni, nj, shell);
                ring[n] = IcoSphere.PointOn(face, ni, nj, divisions);
            }

            return MaxLateral;
        }

        Vector3 centre = DirectionOf(cell);
        Vector3I self = Canonical(cell);

        // GATHERED BY DISTANCE, NOT BY LATTICE STEP.
        //
        // The six lattice steps are the ring only for a node in a face's
        // interior. On a seam the steps run off the face, and the site found
        // over there need not be the one actually adjacent -- so the ring got
        // an impostor, and the corners either side of it were built from a
        // triple the neighbour did not share. Both nodes then placed their
        // shared wall somewhere different.
        //
        // The true neighbours are simply the nearest sites, which is a fact
        // about the geometry rather than about anyone's indexing. Candidates
        // are collected from this node's own steps AND from the steps of those,
        // which reaches every site on the far side of a seam, and the nearest
        // few win.
        Span<Vector3I> pool = stackalloc Vector3I[MaxLateral * MaxLateral + MaxLateral];
        int pooled = 0;

        for (int n = 0; n < MaxLateral; n++)
        {
            if (!Neighbour(cell, n, 0, out Vector3I first))
                continue;

            Add(pool, ref pooled, Canonical(first), self);

            for (int m = 0; m < MaxLateral; m++)
            {
                if (Neighbour(first, m, 0, out Vector3I second))
                    Add(pool, ref pooled, Canonical(second), self);
            }
        }

        if (pooled == 0)
            return 0;

        // A node's neighbours all sit at about one spacing away, and the next
        // ring out is markedly further, so the cut is unambiguous. Taken
        // relative to the nearest candidate rather than as an absolute, since
        // spacing varies a little across a face.
        float nearest = float.MaxValue;

        for (int n = 0; n < pooled; n++)
        {
            float d = 1f - IcoSphere.PointOn(
                FaceOf(pool[n]), IndexI(pool[n]), IndexJ(pool[n]), divisions).Dot(centre);

            if (d > 0.0000001f && d < nearest)
                nearest = d;
        }

        float limit = nearest * 2.25f;
        int count = 0;

        for (int n = 0; n < pooled && count < MaxLateral; n++)
        {
            Vector3 at = DirectionOf(pool[n]);
            float d = 1f - at.Dot(centre);

            if (d <= 0.0000001f || d > limit)
                continue;

            cells[count] = pool[n];
            ring[count] = at;
            count++;
        }

        if (count > 2)
            SortCells(centre, ring, cells, count);

        return count;
    }

    /// <summary>Adds a candidate if it is new and is not the node itself.</summary>
    private static void Add(Span<Vector3I> pool, ref int count, Vector3I cell, Vector3I self)
    {
        if (cell == self || count >= pool.Length)
            return;

        for (int n = 0; n < count; n++)
        {
            if (pool[n] == cell)
                return;
        }

        pool[count++] = cell;
    }

    private int FaceOf(Vector3I cell)
    {
        Unpack(cell, out int face, out _, out _, out _);
        return face;
    }

    private int IndexI(Vector3I cell)
    {
        Unpack(cell, out _, out int i, out _, out _);
        return i;
    }

    private int IndexJ(Vector3I cell)
    {
        Unpack(cell, out _, out _, out int j, out _);
        return j;
    }

    /// <summary>
    /// Sorts directions into the order they appear going round a node.
    ///
    /// Each neighbour is flattened into the plane tangent at the node and taken
    /// by its angle there. An insertion sort, because the list is five or six
    /// long and the comparison is the expensive part.
    /// </summary>
    private static void SortRing(Vector3 centre, Span<Vector3> ring, int count)
    {
        // Any two perpendicular axes in the tangent plane will do: the ring's
        // ORDER is what matters, not where it starts.
        //
        // The handedness is NOT free, though. Sorting by increasing angle about
        // (reference, centre x reference) runs counter-clockwise seen from
        // outside, and Godot's front face is the CLOCKWISE one -- so the caps
        // built from that ring face into the planet, and the whole world is
        // invisible from above. Crossing the other way round puts the ring in
        // the order the mesher needs.
        Vector3 reference = Tangent(centre, ring[0]);
        Vector3 across = reference.Cross(centre);

        Span<float> angles = stackalloc float[MaxLateral];

        for (int n = 0; n < count; n++)
        {
            Vector3 flat = Tangent(centre, ring[n]);
            angles[n] = Mathf.Atan2(flat.Dot(across), flat.Dot(reference));
        }

        for (int n = 1; n < count; n++)
        {
            float angle = angles[n];
            Vector3 value = ring[n];

            int at = n - 1;
            while (at >= 0 && angles[at] > angle)
            {
                angles[at + 1] = angles[at];
                ring[at + 1] = ring[at];
                at--;
            }

            angles[at + 1] = angle;
            ring[at + 1] = value;
        }
    }

    /// <summary>A direction flattened into the plane tangent at a node.</summary>
    private static Vector3 Tangent(Vector3 centre, Vector3 at)
    {
        Vector3 flat = at - centre * at.Dot(centre);

        return flat.LengthSquared() > 1e-12f ? flat.Normalized() : flat;
    }

    /// <summary>
    /// The node past one of this node's side walls, in the SAME order the
    /// corners are built in.
    ///
    /// Shares the ring sort with <see cref="NeighbourDirections"/>, because
    /// walls and corners are paired by index and a wall list in lattice order
    /// beside a corner list in ring order is the desynchronisation this whole
    /// arrangement exists to avoid.
    /// </summary>
    private bool RingNeighbour(Vector3I cell, int wall, out Vector3I result)
    {
        Span<Vector3> ring = stackalloc Vector3[MaxLateral];
        Span<Vector3I> cells = stackalloc Vector3I[MaxLateral];

        int count = NeighbourRing(cell, cells, ring);

        if (count == 0 || wall < 0 || wall >= count)
        {
            result = default;
            return false;
        }

        result = cells[wall];
        return true;
    }

    /// <summary>Sorts neighbours into ring order, carrying their addresses
    /// along with their directions.</summary>
    private static void SortCells(Vector3 centre, Span<Vector3> ring,
        Span<Vector3I> cells, int count)
    {
        // Same handedness as SortRing: clockwise seen from outside, which is
        // the winding Godot treats as front-facing.
        Vector3 reference = Tangent(centre, ring[0]);
        Vector3 across = reference.Cross(centre);

        Span<float> angles = stackalloc float[MaxLateral];

        for (int n = 0; n < count; n++)
        {
            Vector3 flat = Tangent(centre, ring[n]);
            angles[n] = Mathf.Atan2(flat.Dot(across), flat.Dot(reference));
        }

        for (int n = 1; n < count; n++)
        {
            float angle = angles[n];
            Vector3 direction = ring[n];
            Vector3I value = cells[n];

            int at = n - 1;
            while (at >= 0 && angles[at] > angle)
            {
                angles[at + 1] = angles[at];
                ring[at + 1] = ring[at];
                cells[at + 1] = cells[at];
                at--;
            }

            angles[at + 1] = angle;
            ring[at + 1] = direction;
            cells[at + 1] = value;
        }
    }
}
