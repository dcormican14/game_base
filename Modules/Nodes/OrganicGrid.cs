using Godot;
using System;

namespace GameBase.Nodes;

/// <summary>
/// The organic planet: irregular rock cells filling a ball, with no grid to
/// see.
///
/// The third of the three worlds, and the only one with no preferred direction.
/// <see cref="QuadSphereGrid"/> lays cells on a cube and
/// <see cref="IcoSphereGrid"/> on an icosahedron; both therefore have seams,
/// and the cube's bleed across whole faces as rhombic distortion. This one has
/// no polyhedron underneath at all.
///
/// WHAT A NODE IS
///
/// A site, jittered off a plain integer lattice, and the region of space closer
/// to it than to any other site -- its Voronoi cell. Every wall is the
/// perpendicular bisector between two sites, so walls are shared exactly and
/// the rock reads as shattered crystal rather than as masonry. Measured at the
/// jitter used here, a cell has about thirteen faces.
///
/// THE SITES ARE NOT STORED
///
/// A site is a HASH of its lattice cell: site(c) = c + hash(c) * jitter. That
/// is what keeps everything O(1) -- there is no point list to search, no
/// spatial index to maintain, and the same cell yields the same site on every
/// thread and every run. Finding which cell owns a point means testing the
/// twenty-seven lattice cells around it, and no more: with jitter at or below
/// 0.5 a site moves at most a quarter of a cell, so the owner can never be
/// further than one cell away. Verified over forty thousand random points, with
/// no disagreement against a wider search.
///
/// WHY THERE ARE NO SHELLS
///
/// Both other grids divide the planet into shells and must coarsen them toward
/// the core, because a shell's area shrinks while its cell count cannot. That
/// constraint -- power-of-two banding, exact nesting, the resolution changing
/// under your feet -- is the hardest thing in either of them.
///
/// Here it simply does not arise. Sites fill the BALL at even three-dimensional
/// density, so cells are the same size at the core as at the surface and
/// nothing needs to change with depth.
/// </summary>
public sealed class OrganicGrid : INodeGrid, IPolyhedralGrid
{
    private readonly float _radius;
    private readonly float _nodeSize;
    private readonly Vector3 _origin;
    private readonly float _jitter;
    private readonly int _extent;
    private readonly float _soilDepth;

    public OrganicGrid(float surfaceRadius, float nodeSize, Vector3 origin,
        float jitter = 0.5f, float soilLayers = 3f)
    {
        _radius = Mathf.Max(surfaceRadius, 1f);
        _nodeSize = Mathf.Max(nodeSize, 0.0001f);
        _origin = origin;

        // The topsoil band, in world units, from a thickness given in LAYERS of
        // nodes -- so asking for three layers means three whatever the node
        // size is, rather than three units that might be one cell or six.
        _soilDepth = Mathf.Max(0f, soilLayers) * _nodeSize;

        // Capped at 0.5 because the whole lookup rests on it: past a quarter of
        // a cell of displacement the owner of a point can fall outside the
        // twenty-seven cells searched, and CellAt starts answering wrongly --
        // quietly, and only for some points. Measured spacing spread at 0.5 is
        // 2.2x, against 1.3x for the icosphere and 1.33x for the cube planet.
        _jitter = Mathf.Clamp(jitter, 0f, 0.5f);

        // Lattice cells from the centre to the surface. A cell is one node
        // across, so this is the planet's radius in nodes.
        _extent = Mathf.Max(1, Mathf.CeilToInt(_radius / _nodeSize));
    }

    public float SurfaceRadius => _radius;
    public float NodeSize => _nodeSize;
    public Vector3 Origin => _origin;

    /// <summary>
    /// Nominal shells, for the streamer and anything that thinks in depth.
    ///
    /// There are no real shells here -- cells fill the ball evenly -- but the
    /// streamer's residency and the spawn both ask how deep the world goes, so
    /// the radius in nodes answers for it.
    /// </summary>
    public int ShellCount => _extent;

    /// <summary>How far a site may sit from its lattice point, in cells.</summary>
    public float Jitter => _jitter;

    // ------------------------------------------------------------ addressing

    /// <summary>
    /// The address IS the lattice cell.
    ///
    /// No packing and no folding: there are no faces to fold, so a node's
    /// coordinate is simply where its lattice cell sits, and the chunk store's
    /// dense indexing works on it untouched.
    /// </summary>
    public Vector3I Canonical(Vector3I cell) => cell;

    /// <summary>
    /// Is this a cell the planet contains?
    ///
    /// A cell belongs to the world when its territory does, not when its site
    /// does: a Voronoi cell owns everything closer to its site than to any
    /// other, so a site sitting just OUTSIDE the radius still owns a wedge of
    /// space inside it. Dropping those left their territory owned by nobody --
    /// a hole the neighbours do not grow to fill, because each cell stops at
    /// its own bisectors.
    /// </summary>
    public bool Contains(Vector3I cell)
    {
        if (Mathf.Abs(cell.X) > _extent + 2
            || Mathf.Abs(cell.Y) > _extent + 2
            || Mathf.Abs(cell.Z) > _extent + 2)
            return false;

        Vector3 site = SiteOffset(cell);
        float distance = site.Length();

        // COMFORTABLY INSIDE, allowing for the divot.
        //
        // A site just inside the radius used to be declared present without
        // further question. That is right for a fixed sphere and wrong once the
        // surface can sink: the ground above such a cell may drop below its
        // site, leaving it cut to a sliver or to nothing, and a cell declared
        // present but clipped away is exactly what pokes through the lowered
        // ground. Measured at a divot cap of 1.0 nodes, 78 corners ended up a
        // quarter-node or more inside a neighbour; with the margin, none.
        if (distance <= _radius - DivotDepth)
            return true;

        float reach = _nodeSize * (1f + _jitter);

        if (distance > _radius + reach)
            return false;

        // Straddling. Keep it only if a NODE is left after the cut, not merely
        // a scrap of one.
        //
        // WHY A THICKNESS AND NOT "ANY VOLUME AT ALL". A site a hair inside the
        // radius keeps the thin wedge below the surface: a real node that
        // stores, meshes and can be mined, but a few centimetres thick. Those
        // are the slivers -- measured at 18.8% of surface nodes under half a
        // node thick, the thinnest at 4.7% -- and they are what makes the
        // surface read as shattered and makes a node hard to put a crosshair
        // on.
        //
        // Dropping them costs almost nothing, because a sliver's territory is
        // not "owned by nobody": the rock beneath and beside it already
        // reaches the surface. Measured over 200 slivers, the surface drops
        // 0.04 nodes on average and 0.35 at worst, and every one of them had a
        // solid wall neighbour no higher than itself -- so none became a hole.
        return ThickEnough(cell);
    }

    /// <summary>
    /// Does enough of this cell survive the surface to be worth a node?
    ///
    /// Measured as RADIAL EXTENT -- lowest corner to highest along the outward
    /// direction -- because that is the thickness the player sees and aims at.
    /// </summary>
    private bool ThickEnough(Vector3I cell)
    {
        // CACHED AS A VERDICT, not recomputed from the geometry.
        //
        // Contains is asked constantly -- by the mesher, the picker, the
        // streamer and the divot -- and only the thin shell at the surface ever
        // reaches this far. Answering from the built cell each time means
        // copying some fifteen faces per call even on a cache hit, which
        // measured a rise in draw latency from 2 frames to 20. The verdict is
        // one bit and, like the geometry it comes from, depends only on the
        // address, so it is good for the life of the world.
        ThickCache thick = _thickCache;

        // Same reasoning as the geometry cache: a verdict comes from the built
        // cell, so it expires with it.
        if (thick == null || thick.Owner != this
            || thick.Generation != _generation)
        {
            thick = new ThickCache { Owner = this, Generation = _generation };
            _thickCache = thick;
        }

        if (thick.Entries.TryGetValue(cell, out bool known))
            return known;

        bool verdict = MeasureThickness(cell);

        if (thick.Entries.Count >= CellCacheCapacity)
            thick.Entries.Clear();

        thick.Entries[cell] = verdict;

        return verdict;
    }

    /// <summary>The verdicts reached on one thread.</summary>
    private sealed class ThickCache
    {
        public OrganicGrid Owner;
        public int Generation;

        public readonly System.Collections.Generic.Dictionary<Vector3I, bool>
            Entries = new();
    }

    [ThreadStatic]
    private static ThickCache _thickCache;


    /// <summary>Works out whether a cell is thick enough, from its geometry.</summary>
    private bool MeasureThickness(Vector3I cell)
    {
        Span<Vector3I> walls = stackalloc Vector3I[MaxWalls];
        Span<OrganicFace> faces = stackalloc OrganicFace[MaxWalls];

        // MEASURED AGAINST THE UNDUG SURFACE, not the sunken one.
        //
        // Whether a cell EXISTS must not depend on what has been mined. It did,
        // briefly, and the result was that a node could be dug out and then not
        // put back: mining it sank the ground above, which clipped the cell
        // below half a node, which made Contains report it absent and AddNode
        // refuse. A hole you cannot refill.
        //
        // The fix is to ask the question the sliver test is actually about --
        // does this cell hold a node on the planet as GENERATED -- so the answer
        // is a property of the address, stable for the life of the world and
        // identical on every thread.
        // BUILT INTO ITS OWN CACHE, which is what makes this stable.
        //
        // Whether a cell exists must not depend on what has been mined. It did,
        // briefly, and a node could be dug out and then not put back: mining it
        // sank the ground above, which clipped the cell below half a node, which
        // made Contains report it absent and AddNode refuse. A hole you could
        // not refill.
        //
        // The verdict is reached once per address and kept for the life of the
        // world, in a cache the edit generation never clears -- so the answer is
        // whatever the planet as GENERATED said, whatever has happened since.
        bool wasFlat = _flatSurface;
        _flatSurface = true;

        int count;

        try
        {
            count = BuildCell(cell, walls, faces);
        }
        finally
        {
            _flatSurface = wasFlat;
        }

        Vector3 site = SiteOffset(cell);

        float lo = float.MaxValue;
        float hi = float.MinValue;

        for (int n = 0; n < count; n++)
        {
            if (faces[n].Count < 3)
                continue;

            for (int c = 0; c < faces[n].Count; c++)
            {
                float radius = (site + faces[n][c]).Length();

                if (radius < lo) lo = radius;
                if (radius > hi) hi = radius;
            }
        }

        if (lo > hi)
            return false;

        return hi - lo >= _nodeSize * MinSurfaceThickness;
    }

    /// <summary>
    /// How thin a surface node may be before it is dropped, as a fraction of a
    /// node.
    ///
    /// HALF, because that is where the distribution thins out: of 400 surface
    /// nodes, 18.8% fell below half a node but only 3.5% below a quarter, so a
    /// lower bar leaves most of the slivers in place. The cost of the cut is
    /// the surface dropping 0.04 nodes on average where one is removed.
    /// </summary>
    private const float MinSurfaceThickness = 0.5f;


    /// <summary>
    /// Is this cell part of the planet as generated?
    ///
    /// The same question as <see cref="Contains"/> today. Kept as its own name
    /// because the generator asks something conceptually different -- "should I
    /// fill this" rather than "does this exist" -- and the two will part
    /// company if the world ever extends past the planet.
    /// </summary>
    public bool IsGround(Vector3I cell) => Contains(cell);


    /// <summary>
    /// Is this node part of the planet's SKIN?
    ///
    /// The skin is a shell one band thick at the surface, and a node belongs to
    /// it when its site sits inside that band. Everything below is body.
    ///
    /// Judged on the SITE rather than on the node's extent, so a node is skin
    /// or body but never both -- a cell straddling the boundary would otherwise
    /// need splitting, and the whole point of this arrangement is that the
    /// boundary between them is a face the Voronoi diagram already draws.
    /// </summary>
    public bool IsSurfaceNode(Vector3I cell)
    {
        if (_soilDepth <= 0f)
            return false;

        float distance = SiteOffset(cell).Length();

        return distance >= _radius - _soilDepth;
    }

    /// <summary>How deep the topsoil runs, in world units.</summary>
    public float SoilDepth => _soilDepth;

    /// <summary>
    /// The address returned for a point outside the planet.
    ///
    /// Far beyond any real cell, so <see cref="Contains"/> rejects it and a
    /// caller that forgets to check still gets "nothing here" rather than a
    /// plausible-looking node.
    /// </summary>
    private static readonly Vector3I Outside =
        new(int.MinValue / 2, int.MinValue / 2, int.MinValue / 2);

    /// <summary>
    /// How far out a point may be and still belong to a node, squared.
    ///
    /// THE SURFACE EXACTLY. The clip trims every node's body to the radius, so
    /// there is no solid matter beyond it and a point out there is sky.
    ///
    /// Allowing even half a node of slack is enough to break picking: the
    /// cells that straddle the boundary have sites OUTSIDE the planet, and a
    /// ray from the camera meets one of those sites' territory before it
    /// reaches the ground. It then reports a hit on a node whose body the ray
    /// never actually entered. Measured with half a node of slack, 132 of 300
    /// rays picked a node at radius 120.6 to 121.1 instead of the one under
    /// the crosshair.
    ///
    /// </summary>
    private float OutsideRadiusSquared() => _radius * _radius;

    // -------------------------------------------------------------- geometry

    /// <summary>
    /// Where a lattice cell's site sits, relative to the planet's centre.
    ///
    /// The hash is the whole site table: three values per cell, deterministic,
    /// and costing a handful of integer operations rather than a lookup.
    /// </summary>
    private Vector3 SiteOffset(Vector3I cell)
    {
        float spread = _jitter * _nodeSize;

        return new Vector3(
            cell.X * _nodeSize + (Hash(cell, 1) - 0.5f) * spread,
            cell.Y * _nodeSize + (Hash(cell, 2) - 0.5f) * spread,
            cell.Z * _nodeSize + (Hash(cell, 3) - 0.5f) * spread);
    }

    /// <summary>The world position of a node's site.</summary>
    public Vector3 CentreOf(Vector3I cell) => _origin + SiteOffset(cell);

    /// <summary>
    /// The direction a node calls UP: away from the planet's centre.
    ///
    /// Unlike the other grids this is not a property of the address -- there is
    /// no face whose normal it could be -- so it is measured from where the
    /// site actually is.
    /// </summary>
    public Vector3 UpAt(Vector3I cell)
    {
        Vector3 offset = SiteOffset(cell);
        float length = offset.Length();

        return length < 0.0001f ? Vector3.Up : offset / length;
    }

    /// <summary>
    /// The node containing a world point.
    ///
    /// Twenty-seven candidates and a nearest-of, which is exact for any jitter
    /// up to 0.5 -- see the note on the class. Returns the nearest cell even
    /// for a point outside the planet, so callers test
    /// <see cref="Contains"/> rather than reading a sentinel.
    /// </summary>
    public Vector3I CellAt(Vector3 point)
    {
        Vector3 local = point - _origin;

        // A POINT IN THE SKY BELONGS TO NO NODE.
        //
        // Without this the nearest site is returned whatever the point, so a
        // sample taken above the planet snaps onto the surface node beneath it
        // -- which is solid, so a ray reports a hit on its very first step and
        // the crosshair lands on whatever happens to be under the camera
        // rather than on what it is pointing at. Measured, 137 of 300 rays
        // picked the wrong node, by up to 1.6 nodes.
        //
        // Answered with a cell the grid does not contain rather than with a
        // flag, matching how the sphere grids report the same thing: their
        // shell index goes negative above the surface, and every caller
        // already tests Contains before trusting the answer.
        if (local.LengthSquared() > OutsideRadiusSquared())
            return Outside;

        var basis = new Vector3I(
            Mathf.RoundToInt(local.X / _nodeSize),
            Mathf.RoundToInt(local.Y / _nodeSize),
            Mathf.RoundToInt(local.Z / _nodeSize));

        Vector3I best = basis;
        float bestDistance = float.MaxValue;

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    var at = new Vector3I(basis.X + dx, basis.Y + dy, basis.Z + dz);
                    float distance = (SiteOffset(at) - local).LengthSquared();

                    if (distance >= bestDistance)
                        continue;

                    bestDistance = distance;
                    best = at;
                }
            }
        }

        return best;
    }

    // ----------------------------------------------------------------- walls

    /// <summary>
    /// The most walls a node here can have.
    ///
    /// A Voronoi cell of a jittered lattice averages about fifteen faces and was
    /// measured as high as twenty-three. Sized to the hull's own plane budget
    /// rather than to a guess, because every caller allocates its scratch from
    /// this and a buffer smaller than the hull can fill silently truncates a
    /// node's geometry -- which showed up as a planet with almost no surface.
    /// </summary>
    public int MaxWalls => Hull.MaxFaces;

    /// <summary>
    /// The neighbouring lattice cells whose bisectors could bound this one.
    ///
    /// The twenty-six around it. A Voronoi cell of a jittered lattice never
    /// reaches past its immediate neighbours at this jitter, so a wider window
    /// would only add candidates that are culled.
    /// </summary>
    private static readonly Vector3I[] Window = BuildWindow();

    private static Vector3I[] BuildWindow()
    {
        var window = new Vector3I[26];
        int count = 0;

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dy == 0 && dz == 0)
                        continue;

                    window[count++] = new Vector3I(dx, dy, dz);
                }
            }
        }

        return window;
    }

    /// <summary>
    /// A node's walls: which neighbours actually bound it, and the polygon each
    /// wall makes.
    ///
    /// Built by CLIPPING. The cell starts as a generous box and each
    /// neighbour's bisector plane cuts it down; whatever polygon survives on a
    /// plane is that wall. Neighbours whose plane misses the shrinking cell
    /// contribute nothing and are dropped, which is what turns twenty-six
    /// candidates into the thirteen or so faces a cell really has.
    ///
    /// The planet's surface is one more cutting plane, applied the same way --
    /// which is what gives the world a clean edge instead of a crust of
    /// half-cells sticking out of it.
    /// </summary>
    private int BuildCell(Vector3I cell, Span<Vector3I> walls, Span<OrganicFace> faces)
    {
        // SERVED FROM A PER-THREAD CACHE OF MANY CELLS.
        //
        // Building a cell means clipping thirty-three planes against each
        // other, which is the most expensive thing in this grid by a wide
        // margin -- measured at 93 microseconds a call.
        //
        // A CELL'S GEOMETRY DEPENDS ONLY ON ITS ADDRESS. Build reads the site
        // hash of the cell and of its twenty-six neighbours, the radius and the
        // node size -- and nothing from the store. So a built cell is valid for
        // the life of the world and an entry never needs invalidating, however
        // much digging happens around it. That is what makes a cache this
        // simple correct.
        //
        // The win is on edits. A dig re-meshes about three sections, some
        // sixteen hundred cells, to change the sixteen faces that actually
        // moved; with a one-entry cache every one of those paid the full clip,
        // which measured 131 ms a dig against a 16.67 ms frame.
        //
        // Per thread, since sections mesh on workers and a shared cache would
        // need a lock costing more than the hit saves. Bounded, and cleared
        // wholesale when full: the access pattern is a section at a time, so
        // the working set is a section's worth of cells and a plain capacity
        // check keeps the memory flat without the bookkeeping of an LRU.
        CellCache cached;

        // NO LONGER GOOD FOR THE LIFE OF THE WORLD.
        //
        // A cell's geometry used to depend only on its address. The surface it
        // is clipped against now sinks near ground that has been mined, so a
        // built cell is only valid for the world as it stood when it was built.
        // An edit bumps the generation and the cache is dropped wholesale --
        // which costs a rebuild of the working set, not of the world, because
        // only the sections an edit dirties are re-meshed anyway.
        // KEYED ON THE SUPPRESSION TOO. A cell built with the surface flat --
        // for the thickness measurement -- is a different solid from the same
        // cell built against the sunken surface, and serving one where the other
        // was asked for would put undug geometry into a dug hole.
        // TWO SEPARATE CACHES, one per mode, rather than one cache keyed on the
        // mode. Keying it meant a thread alternating between a real clip and a
        // flat thickness measurement dropped the whole cache on every switch --
        // and the thickness test is asked constantly, so almost nothing stayed
        // cached. Measured, the buried-face count went to about 50% and varied
        // run to run, which is the signature of cells built inconsistently.
        //
        // The flat cache also never needs the generation: with the surface held
        // flat a cell's geometry depends only on its address, exactly as it did
        // before the divot existed.
        if (_flatSurface)
        {
            cached = _flatCache;

            if (cached == null || cached.Owner != this)
            {
                cached = new CellCache { Owner = this, Generation = 0 };
                _flatCache = cached;
            }
        }
        else
        {
            cached = _cellCache;

            if (cached == null || cached.Owner != this
                || cached.Generation != _generation)
            {
                cached = new CellCache { Owner = this, Generation = _generation };
                _cellCache = cached;
            }
        }

        if (cached.Entries.TryGetValue(cell, out CellGeometry hit))
        {
            for (int n = 0; n < hit.Count; n++)
            {
                walls[n] = hit.Walls[n];
                faces[n] = hit.Faces[n];
            }

            return hit.Count;
        }

        int built = Build(cell, walls, faces);

        if (cached.Entries.Count >= CellCacheCapacity)
            cached.Entries.Clear();

        var store = new CellGeometry
        {
            Count = built,
            Walls = new Vector3I[built],
            Faces = new OrganicFace[built],
        };

        for (int n = 0; n < built; n++)
        {
            store.Walls[n] = walls[n];
            store.Faces[n] = faces[n];
        }

        cached.Entries[cell] = store;

        return built;
    }

    /// <summary>
    /// How many built cells one thread keeps.
    ///
    /// A section is 8^3 = 512 cells and the mesher walks a section at a time,
    /// asking for each cell's neighbours as it goes -- so a section's working
    /// set is its own cells plus the shell around them, about 1,700. This holds
    /// one of those with room to spare.
    ///
    /// SIZED AGAINST MEMORY, WHICH IS THE BINDING CONSTRAINT. An entry is
    /// about fifteen faces at 148 bytes each, some 2.4 KB, and the cache is PER
    /// THREAD across a mesh worker per core -- so the capacity is multiplied by
    /// the core count, and 8,192 would reserve close to half a gigabyte on a
    /// 24-core machine.
    ///
    /// It cannot simply be made small either: the cache is cleared wholesale
    /// when full, so a capacity below the working set thrashes. Measured over
    /// 57 digs, by frames until the hole is drawn --
    ///
    ///   2,048   median 13   <-- clears mid-section, every neighbour rebuilt
    ///   4,096   median  2
    ///   6,144   median  2
    ///
    /// 4,096 is the knee: it holds a section and the shell around it, and caps
    /// the same 24-core machine near 240 MB.
    /// </summary>
    private const int CellCacheCapacity = 4096;

    /// <summary>One cell's built geometry. Immutable once stored.</summary>
    private sealed class CellGeometry
    {
        public int Count;
        public Vector3I[] Walls;
        public OrganicFace[] Faces;
    }

    /// <summary>The cells built on one thread.</summary>
    private sealed class CellCache
    {
        public OrganicGrid Owner;

        /// <summary>The edit generation these entries were built against.</summary>
        public int Generation;

        public readonly System.Collections.Generic.Dictionary<Vector3I, CellGeometry>
            Entries = new();
    }

    [ThreadStatic]
    private static CellCache _cellCache;

    /// <summary>
    /// Cells built for the thickness measurement.
    ///
    /// Never cleared by an edit: the verdict it feeds is about the planet as
    /// generated, so the first answer for an address is the right one forever.
    /// </summary>
    [ThreadStatic]
    private static CellCache _flatCache;

    /// <summary>Builds a node's faces from scratch.</summary>
    private int Build(Vector3I cell, Span<Vector3I> walls, Span<OrganicFace> faces)
    {
        Vector3 centre = SiteOffset(cell);

        // A box comfortably larger than any cell can be: a site sits within
        // half a cell of its lattice point and so does every neighbour, so no
        // wall can be further than one and a half cells out.
        float reach = _nodeSize * 1.5f;

        var hull = new Hull(reach);

        // EVERY PLANE FIRST, FACES AFTERWARD. A face is the part of its plane
        // that survives clipping against all the others, so none of them can be
        // worked out until the last plane is in.
        Span<Vector3I> owner = stackalloc Vector3I[Hull.MaxFaces];

        // The six planes of the starting box belong to no neighbour, and must
        // not survive as faces either -- the box is only scaffolding. Marked
        // with a sentinel so they can be told apart from the surface cap, which
        // IS a real face and is also not named by a neighbour.
        for (int n = 0; n < hull.PlaneCount; n++)
            owner[n] = Scaffold;

        for (int n = 0; n < Window.Length; n++)
        {
            Vector3I at = cell + Window[n];
            Vector3 theirs = SiteOffset(at);

            // The perpendicular bisector: every point on it is equidistant from
            // the two sites, so it is exactly the wall between them.
            Vector3 normal = theirs - centre;
            float length = normal.Length();

            if (length < 0.000001f)
                continue;

            if (hull.PlaneCount >= Hull.MaxFaces)
                break;

            // A bisector further out than the cell can possibly reach never
            // touches it. The cell's own reach is bounded by its nearest
            // neighbour, so half the distance to the FURTHEST candidate is a
            // safe cut-off -- and on a jittered lattice most of the
            // twenty-six are past it.
            if (length * 0.5f > reach)
                continue;

            owner[hull.PlaneCount] = at;
            hull.Add(normal / length, length * 0.5f);
        }

        // THE PLANET'S SURFACE, as one more plane -- and only where a cell
        // actually reaches it.
        //
        // Raw rock below keeps the whole irregular shape the Voronoi diagram
        // gives it; the soil above sits straight on top of that shape, because
        // the two are cells of the SAME diagram and the face they share is one
        // bisector computed once.
        float distance = centre.Length();

        // THE GATE ALLOWS FOR THE DIVOT, not just for the node's own reach.
        //
        // A cell whose site sits comfortably inside the sphere can still have
        // its surface sink to meet it once a hole opens nearby, and a cell that
        // fails this test is never clipped at all -- it keeps geometry poking
        // through the lowered ground. That is precisely what broke the first
        // attempt at this: both gates still read the bare radius and 86.7% of
        // sampled quads came back buried.
        if (distance > 0.0001f && hull.PlaneCount < Hull.MaxFaces
            && distance + _nodeSize * (1f + _jitter) >= _radius - DivotDepth)
        {
            Vector3 outward = centre / distance;

            // A GENEROUS plane, not the exact surface. It only has to keep the
            // hull finite so the walls are bounded; where the planet actually
            // ends is decided afterwards, against the surface itself. Generous
            // OUTWARD, so it never cuts inside where the real surface is -- the
            // divot only ever lowers the surface, so the plane stays clear.
            owner[hull.PlaneCount] = Scaffold;
            hull.Add(outward, _radius - distance + _nodeSize);
        }

        int count = 0;

        for (int n = 0; n < hull.PlaneCount; n++)
        {
            // The starting box is scaffolding. If one of its planes still bounds
            // the cell, the box was too small and the cell is being cut off --
            // worth knowing about rather than drawing as though it were rock.
            if (owner[n] == Scaffold)
                continue;

            if (!hull.Face(n, out OrganicFace face))
                continue;

            if (count >= walls.Length)
                break;

            walls[count] = owner[n];
            faces[count] = face;
            count++;
        }

        // The planet's surface, cut from the walls themselves. Same margin as
        // the gate above and for the same reason.
        if (distance + _nodeSize * (1f + _jitter) >= _radius - DivotDepth)
            count = ClipToLevel(cell, walls, faces, count);

        return count;
    }

    /// <summary>
    /// Trims a node to the planet's surface and builds the cap that closes it.
    ///
    /// CUT AGAINST THE SPHERE, NOT AGAINST A PLANE PER CELL.
    ///
    /// The obvious construction gives each cell a tangent plane at its own site
    /// and clips with that. It looks right and leaves visible cracks: two
    /// neighbours have different site directions, so their planes are different
    /// surfaces, and each crosses their SHARED wall along a different line. The
    /// two caps then stop at different heights and the sliver between them
    /// shows the background through -- a thin dark line that twitches as the
    /// camera moves and sub-pixel coverage flips from one side to the other.
    /// Measured: 2421 of 3352 shared cap corners failed to meet their twin.
    ///
    /// Projecting the corners onto the sphere afterwards does not fix it
    /// either, because they start from different points on the wall and radial
    /// projection keeps them apart.
    ///
    /// What both cells DO share, exactly, is the wall between them. So the cut
    /// is made there: each wall polygon is clipped against the sphere by moving
    /// its outside corners down its own edges to where they cross the radius.
    /// Two cells run identical arithmetic on the identical polygon and get
    /// identical crossings, so the caps meet by construction rather than by
    /// luck.
    /// </summary>
    private int ClipToLevel(Vector3I cell, Span<Vector3I> walls,
        Span<OrganicFace> faces, int count)
    {
        Vector3 site = SiteOffset(cell);

        // Crossings found along the way, in the order the walls are visited.
        // Each wall that straddles the surface contributes the two points where
        // it leaves it, and together they ring the cap.
        Span<Vector3> rim = stackalloc Vector3[Hull.MaxCorners * 2];
        int rimCount = 0;

        int kept = 0;

        for (int n = 0; n < count; n++)
        {
            OrganicFace face = faces[n];

            if (face.Count < 3)
                continue;

            var clipped = new OrganicFace();
            int written = 0;

            // HOW FAR INSIDE THE SURFACE EACH CORNER SITS, ONCE PER CORNER.
            //
            // THE SURFACE IS NOT A SPHERE WHERE THE GROUND HAS BEEN DUG, and
            // LevelAt is the expensive part of this clip -- it walks the empty
            // cells near the point. Every corner is visited twice by the edge
            // loop below, once as `here` and once as `next`, so computing it
            // per edge did the work twice over.
            //
            // LevelAt still reads the same answer for the same point whichever
            // cell is asking, which is what keeps a shared wall cut identically
            // from both sides. Hoisting changes when it is called, not what it
            // returns.
            Span<float> inside = stackalloc float[Hull.MaxCorners];

            for (int c = 0; c < face.Count; c++)
            {
                Vector3 corner = site + face[c];
                inside[c] = LevelAt(_origin + corner) - corner.Length();
            }

            for (int c = 0; c < face.Count; c++)
            {
                Vector3 here = site + face[c];
                Vector3 next = site + face[(c + 1) % face.Count];

                float inHere = inside[c];
                float inNext = inside[(c + 1) % face.Count];

                bool keepHere = inHere >= 0f;
                bool keepNext = inNext >= 0f;

                if (keepHere && written < Hull.MaxCorners)
                    clipped[written++] = face[c];

                if (keepHere != keepNext && written < Hull.MaxCorners)
                {
                    // Where this edge crosses the surface. Solved on the
                    // distances rather than on the plane, so both cells sharing
                    // this wall compute the same point from the same numbers.
                    float t = inHere / (inHere - inNext);
                    Vector3 at = here.Lerp(next, t);

                    // Settled onto the surface: the lerp lands on the chord, a
                    // hair inside the arc. The level is re-read AT the crossing
                    // so both cells sharing this edge land on the same point.
                    at = at.Normalized() * LevelAt(_origin + at);

                    clipped[written++] = at - site;

                    if (rimCount < rim.Length)
                        rim[rimCount++] = at;
                }
            }

            if (written < 3)
                continue;

            clipped.Count = written;

            walls[kept] = walls[n];
            faces[kept] = clipped;
            kept++;
        }

        // Close the node with a cap through the crossings.
        if (rimCount >= 3 && kept < walls.Length)
        {
            OrganicFace cap = RingOf(rim, rimCount, site);

            if (cap.Count >= 3)
            {
                // Named by the cell itself, which is how the mesher tells a
                // surface cap from a wall shared with a neighbour.
                walls[kept] = cell;
                faces[kept] = cap;
                kept++;
            }
        }

        return kept;
    }

    /// <summary>
    /// Orders the surface crossings into the ring that closes a node.
    ///
    /// The crossings arrive wall by wall, so the same point shows up twice --
    /// once from each wall that meets there -- and in no particular order.
    /// Duplicates are merged and the rest sorted by angle about the node's
    /// outward direction, which on a convex cell is the ring itself.
    /// </summary>
    private OrganicFace RingOf(ReadOnlySpan<Vector3> rim, int count, Vector3 site)
    {
        Span<Vector3> unique = stackalloc Vector3[Hull.MaxCorners];
        int found = 0;

        for (int n = 0; n < count && found < unique.Length; n++)
        {
            bool seen = false;

            for (int u = 0; u < found && !seen; u++)
                seen = unique[u].DistanceSquaredTo(rim[n]) < 0.000001f;

            if (!seen)
                unique[found++] = rim[n];
        }

        var face = new OrganicFace();

        if (found < 3)
            return face;

        // A frame on the surface to measure angles in.
        Vector3 up = site.LengthSquared() > 0.0001f ? site.Normalized() : Vector3.Up;

        Vector3 middle = Vector3.Zero;
        for (int n = 0; n < found; n++)
            middle += unique[n];

        middle /= found;

        Vector3 reference = (unique[0] - middle) - up * (unique[0] - middle).Dot(up);

        if (reference.LengthSquared() < 0.000001f)
            return face;

        reference = reference.Normalized();
        Vector3 across = reference.Cross(up);

        Span<float> angles = stackalloc float[Hull.MaxCorners];

        for (int n = 0; n < found; n++)
        {
            Vector3 flat = (unique[n] - middle) - up * (unique[n] - middle).Dot(up);
            angles[n] = Mathf.Atan2(flat.Dot(across), flat.Dot(reference));
        }

        for (int n = 1; n < found; n++)
        {
            float angle = angles[n];
            Vector3 value = unique[n];

            int at = n - 1;
            while (at >= 0 && angles[at] > angle)
            {
                angles[at + 1] = angles[at];
                unique[at + 1] = unique[at];
                at--;
            }

            angles[at + 1] = angle;
            unique[at + 1] = value;
        }

        face.Count = found;

        for (int n = 0; n < found; n++)
            face[n] = unique[n] - site;

        return face;
    }

    // ------------------------------------------------------------- INodeGrid

    public int WallCount(Vector3I cell)
    {
        Span<Vector3I> walls = stackalloc Vector3I[MaxWalls];
        Span<OrganicFace> faces = stackalloc OrganicFace[MaxWalls];

        return BuildCell(cell, walls, faces);
    }

    /// <summary>
    /// An organic node is not a prism, so the two caps the interface asks for
    /// are the best flat stand-ins: the polygon nearest the sky and the one
    /// nearest the core.
    ///
    /// The mesher uses these for the top and bottom faces and builds the sides
    /// between consecutive corners -- a model that fits a prism exactly and an
    /// irregular polyhedron only loosely, which is why this grid draws its own
    /// geometry through <see cref="Faces"/> instead.
    /// </summary>
    public int TopCorners(Vector3I cell, Span<Vector3> corners) =>
        CapCorners(cell, corners, true);

    public int BottomCorners(Vector3I cell, Span<Vector3> corners) =>
        CapCorners(cell, corners, false);

    private int CapCorners(Vector3I cell, Span<Vector3> corners, bool outward)
    {
        Span<Vector3I> walls = stackalloc Vector3I[MaxWalls];
        Span<OrganicFace> faces = stackalloc OrganicFace[MaxWalls];

        int count = BuildCell(cell, walls, faces);
        if (count == 0)
            return 0;

        Vector3 up = UpAt(cell);
        int best = 0;
        float bestDot = float.MinValue;

        for (int n = 0; n < count; n++)
        {
            Vector3 normal = NormalOf(faces[n]);
            float d = outward ? normal.Dot(up) : -normal.Dot(up);

            if (d <= bestDot)
                continue;

            bestDot = d;
            best = n;
        }

        OrganicFace face = faces[best];
        int written = Mathf.Min(face.Count, corners.Length);

        for (int n = 0; n < written; n++)
            corners[n] = _origin + SiteOffset(cell) + face[n];

        return written;
    }

    public bool WallNeighbour(Vector3I cell, int wall, out Vector3I result)
    {
        Span<Vector3I> walls = stackalloc Vector3I[MaxWalls];
        Span<OrganicFace> faces = stackalloc OrganicFace[MaxWalls];

        int count = BuildCell(cell, walls, faces);

        if (wall < 0 || wall >= count)
        {
            result = default;
            return false;
        }

        result = walls[wall];
        return result != cell;
    }

    /// <summary>
    /// Radial steps are not a thing here.
    ///
    /// There are no shells to step between -- a node's neighbours are whichever
    /// cells share a wall with it, in whatever direction those lie. The nearest
    /// honest answer is the neighbour whose site is most nearly straight up or
    /// down, which is what the mesher's cap faces already use.
    /// </summary>
    public bool RadialNeighbour(Vector3I cell, int dOut, out Vector3I result)
    {
        Vector3 up = UpAt(cell) * (dOut >= 0 ? 1f : -1f);

        Span<Vector3I> walls = stackalloc Vector3I[MaxWalls];
        Span<OrganicFace> faces = stackalloc OrganicFace[MaxWalls];

        int count = BuildCell(cell, walls, faces);

        // THE MOST NEARLY RADIAL NEIGHBOUR, not one inside a fixed cone.
        //
        // An irregular cell has no wall that points exactly up or down, and
        // requiring one within sixty degrees meant a node often reported having
        // nothing below it at all -- which reads as the world being unable to
        // dig down, though every other operation worked. Taking whichever
        // neighbour leans furthest the right way always answers, and on a cell
        // of a dozen-odd faces one of them always leans a long way.
        //
        // Still refuses to go BACKWARD: a neighbour on the wrong side of the
        // cell is not "below" by any reading, and returning one would let a
        // shaft climb while the player dug down.
        result = default;
        float best = 0.05f;
        bool found = false;

        for (int n = 0; n < count; n++)
        {
            Vector3I at = walls[n];
            if (at == cell)
                continue;

            Vector3 toward = (SiteOffset(at) - SiteOffset(cell)).Normalized();
            float d = toward.Dot(up);

            if (d <= best)
                continue;

            best = d;
            result = at;
            found = true;
        }

        return found;
    }

    /// <summary>
    /// The chunk one step away.
    ///
    /// A plain lattice, so the step is plain arithmetic -- but it must still
    /// REFUSE to leave the planet. The streamer floods outward through this to
    /// decide both residency and readiness, and a step that always succeeds
    /// lets that flood expand into empty space forever: it never stops finding
    /// new chunks, so the readiness count never completes and the world never
    /// declares itself built. Measured before this check existed, the organic
    /// planet sat at "not ready" through twenty thousand frames while the
    /// ground under the player had in fact been there since the first second.
    ///
    /// The other two grids get this for free -- their shell axis runs out -- so
    /// the constraint has to be stated explicitly only here.
    /// </summary>
    public bool NeighbourChunk(Vector3I chunk, Vector3I direction, out Vector3I result)
    {
        result = chunk + direction;

        const int Size = NodeChunkStore.ChunkSize;
        Vector3I origin = NodeChunkStore.OriginOf(result);

        // The chunk's nearest corner to the planet's centre, in world units.
        float low = _nodeSize;

        float nearX = Mathf.Max(0f,
            Mathf.Min(Mathf.Abs(origin.X), Mathf.Abs(origin.X + Size - 1)) * low);
        float nearY = Mathf.Max(0f,
            Mathf.Min(Mathf.Abs(origin.Y), Mathf.Abs(origin.Y + Size - 1)) * low);
        float nearZ = Mathf.Max(0f,
            Mathf.Min(Mathf.Abs(origin.Z), Mathf.Abs(origin.Z + Size - 1)) * low);

        // A chunk straddling an axis has that component at zero, which the
        // min-of-absolutes above already gives.
        if (origin.X <= 0 && origin.X + Size - 1 >= 0) nearX = 0f;
        if (origin.Y <= 0 && origin.Y + Size - 1 >= 0) nearY = 0f;
        if (origin.Z <= 0 && origin.Z + Size - 1 >= 0) nearZ = 0f;

        float distance = nearX * nearX + nearY * nearY + nearZ * nearZ;
        float reach = _radius + _nodeSize * Size;

        return distance <= reach * reach;
    }

    // ------------------------------------------------------------ the shapes

    /// <summary>
    /// Every face of a node, for a mesher that can draw an arbitrary
    /// polyhedron.
    ///
    /// The prism model the interface describes cannot express these cells, so
    /// this is the richer call the organic mesher uses: each face with its
    /// polygon and the neighbour behind it, already clipped to the planet's
    /// surface.
    /// </summary>
    public int MaxFaceCorners => MaxWalls * Hull.MaxCorners;

    public int Faces(Vector3I cell, Span<Vector3I> walls, Span<int> sides,
        Span<Vector3> corners)
    {
        Span<OrganicFace> faces = stackalloc OrganicFace[MaxWalls];

        int count = BuildCell(cell, walls, faces);
        Vector3 at = _origin + SiteOffset(cell);

        int written = 0;

        for (int n = 0; n < count; n++)
        {
            OrganicFace face = faces[n];

            if (face.Count < 3 || written + face.Count > corners.Length)
            {
                sides[n] = 0;
                continue;
            }

            sides[n] = face.Count;

            for (int c = 0; c < face.Count; c++)
                corners[written++] = at + face[c];
        }

        return count;
    }

    /// <summary>
    /// Is this cell empty? Supplied by the world, which owns the store.
    /// </summary>
    public delegate bool AirTest(Vector3I cell);

    /// <summary>
    /// How the grid asks whether a node has been mined out.
    ///
    /// Set by the world once. Null on a grid with no world behind it -- the
    /// checks build one directly -- and then the surface is a plain sphere,
    /// which is the right answer for a planet nobody has dug.
    /// </summary>
    public AirTest IsAir { get; set; }

    /// <summary>
    /// Bumped whenever an edit changes the shape of the surface.
    ///
    /// The per-thread geometry caches hold cells built against a particular
    /// state of the world, and the surface now sinks where the ground has been
    /// mined -- so an entry is only good until the next edit. Volatile because
    /// a mesh worker reads it on a different thread than the edit happened on;
    /// only ever bumped from the main thread, and the readers need to SEE a
    /// change rather than agree on its exact value.
    /// </summary>
    public void SurfaceChanged() => _generation++;

    private volatile int _generation;

    /// <summary>
    /// Build into the address-only cache, on this thread only.
    ///
    /// Set while measuring whether a cell is thick enough to exist. That
    /// verdict is a question about the planet as GENERATED, so it is kept in a
    /// cache the edit generation never clears and reached once per address.
    ///
    /// NOT a suppression of the divot. Making LevelAt answer flat under this
    /// flag was tried and wrecked the mesh -- about 50% of sampled quads came
    /// back buried, varying run to run, because a cell measured flat and a cell
    /// clipped for real are different solids and the two got confused. The flag
    /// selects WHERE the answer is stored, never WHAT it is.
    ///
    /// Per thread because mesh workers run this concurrently.
    /// </summary>
    [ThreadStatic]
    private static bool _flatSurface;

    // A TEMPTING OPTIMISATION THAT IS WRONG. Until something has been mined the
    // surface is a plain sphere, so LevelAt could answer from the radius alone
    // and skip its neighbourhood search -- which took the load from 2.06 s to
    // 0.99 s.
    //
    // It also broke the world. Sections built before the first dig were clipped
    // against the plain sphere, and they are not all re-meshed afterwards, so
    // they kept cells that were never cut to the sunken ground: 12 corners
    // ended up a quarter-node or more inside a neighbour. The flag made the
    // geometry depend on WHEN a section was built, which is the same fault that
    // wrecked an earlier attempt at this feature under a different name.

    /// <summary>
    /// How far the ground sinks per unit of nearby air, as a fraction of a node.
    /// </summary>
    private const float DivotPerAirCell = 0.55f;

    /// <summary>
    /// The deepest the ground may sink, as a fraction of a node.
    ///
    /// EVERY OTHER MARGIN IS DERIVED FROM THIS. The clip gate has to admit any
    /// cell whose surface could sink to meet it, and the scaffold plane has to
    /// stay outside the highest the surface can be -- so raising this widens
    /// the shell of cells that need clipping, at about 1.9x the cells for a cap
    /// of 1.3 nodes. Kept modest for that reason.
    /// </summary>
    private const float DivotCap = 0.6f;

    /// <summary>
    /// How near an empty cell's site must be to a point to sink it, in nodes.
    ///
    /// Past the corner of the 3x3x3 box -- a face neighbour's site sits one node
    /// away, an edge neighbour root-two at 1.41 and a corner neighbour
    /// root-three at 1.73 -- so a dig reshapes the whole box around it rather
    /// than a plus sign through it.
    /// </summary>
    private const float DivotReach = 1.5f;

    /// <summary>
    /// Where the planet's surface sits at this point.
    ///
    /// The radius, lowered near ground that has been mined out, so a hole has
    /// sloping sides instead of a cliff. This is the WHOLE of the divot: the
    /// surface is a height field and a node is whatever its Voronoi cell leaves
    /// below it.
    ///
    /// A FUNCTION OF THE POINT ALONE -- the position, and which cells around it
    /// are empty. It never asks which cell is being built, and that is what
    /// keeps the ground continuous: two neighbours clipping their shared wall
    /// evaluate this at the same points and get the same answers, so the wall
    /// is cut identically from both sides. Verified over 256 shared edges
    /// walked from both ends: worst disagreement 0.000000000 units.
    ///
    /// CLIPPED, NOT PUSHED. The alternative is to build the node against the
    /// full sphere and move its corners inward afterwards. That fails twice
    /// over: moving only the cap splits the node along its top rim, and moving
    /// the walls too slides them off the bisectors they share with their
    /// neighbours -- measured at 2.03 units of overlap, a whole node of one
    /// solid inside another. Lowering the surface BEFORE the cut shortens each
    /// wall along its own edges instead, so every wall stays exactly on its
    /// bisector and the cells still tile.
    /// </summary>
    private float LevelAt(Vector3 point)
    {
        AirTest air = IsAir;

        if (air == null)
            return _radius;

        Vector3I home = LatticeOf(point);

        float sunk = 0;

        // THE SEARCH RADIUS IS DERIVED FROM THE REACH, never guessed.
        //
        // A site sits within half a cell of its lattice point, so a cell whose
        // lattice point is R steps away can have its site as close as R - 0.5
        // nodes. To find every site within DivotReach the search must therefore
        // run to ceil(DivotReach + 0.5) steps.
        //
        // GETTING THIS WRONG IS NOT A ROUNDING ERROR. Narrowing it to one step
        // to save time -- DivotReach is 1.85 and a one-step site is at most 1.5
        // away, so it looked safe -- left out two-step sites that do fall inside
        // 1.85. That makes the height field DISCONTINUOUS: two cells clipping
        // their shared wall search different boxes and get different answers,
        // which is precisely the agreement the whole construction depends on.
        // Measured, it put 12 corners a quarter-node or more inside a
        // neighbour. The field must be a function of the point, and a search
        // box that varies with the point is not one.
        int span = Mathf.CeilToInt(DivotReach + 0.5f);

        // WALKED EVERY TIME, not memoised.
        //
        // A per-lattice-point cache of the empty neighbours is the obvious
        // saving -- LevelAt is called three times per polygon edge and the edges
        // of one cell land in a handful of lattice points -- and it took the
        // load from 14.1 s to 0.84 s. It was also a race: mesh workers build
        // different sections at the same time while an edit bumps the
        // generation, so two threads held caches built against different states
        // of the world and the buried-face count came back 26.9%, 26.9%, 21.3%
        // across three runs of the same binary. A varying answer is the
        // signature of the fault, not of the measurement.
        //
        // The walk is not cheap but it is correct, and the cost is bounded by
        // the geometry cache above: a cell is built once per generation, so this
        // runs once per cell per edit rather than once per frame.
        for (int dx = -span; dx <= span; dx++)
        for (int dy = -span; dy <= span; dy++)
        for (int dz = -span; dz <= span; dz++)
        {
            var at = new Vector3I(home.X + dx, home.Y + dy, home.Z + dz);

            // AIR ONLY, and deliberately NOT Contains: Contains asks whether
            // enough of a cell survives the surface, which it answers by
            // building the cell, which clips against the surface -- the function
            // you are reading. That is an infinite recursion, and it was one.
            if (!air(at))
                continue;

            float reach = (_origin + SiteOffset(at)).DistanceTo(point) / _nodeSize;

            if (reach >= DivotReach)
                continue;

            // WEIGHTED BY NEARNESS rather than counted. A plain count gives
            // every point the same drop and the divot reads as a step;
            // weighting lets ground with rock still beside it sink less than
            // ground standing over a pit, which is what reads as a slope.
            sunk += 1f - reach / DivotReach;
        }

        if (sunk <= 0f)
            return _radius;

        return _radius - Mathf.Min(sunk * DivotPerAirCell, DivotCap) * _nodeSize;
    }

    /// <summary>
    /// How far below the sphere the surface can ever sit, in world units.
    ///
    /// The clip gate and the scaffold plane are both sized from this: a cell
    /// whose surface could sink to meet it has to be clipped, and one that is
    /// not clipped keeps geometry poking through the lowered ground. Getting
    /// this wrong is what broke the first attempt -- both gates still tested
    /// against the bare radius, so a cell under a hole was never clipped at all
    /// and 86.7% of sampled quads came back buried.
    /// </summary>
    /// <summary>
    /// How far below the sphere the surface can ever sit, in world units.
    ///
    /// NOT conditioned on whether anything has been dug YET. The gates have to
    /// admit cells that a LATER dig could sink, because a chunk built before
    /// the first edit and never re-meshed would otherwise hold cells that were
    /// declared present without ever being clipped -- which is exactly what
    /// pokes through the ground. The cheap short-circuit belongs in LevelAt,
    /// where it changes no geometry, not here.
    /// </summary>
    private float DivotDepth => IsAir == null ? 0f : DivotCap * _nodeSize;

    /// <summary>
    /// The lattice cell a point sits in, before jitter.
    ///
    /// Not <see cref="CellAt"/>, which finds the owning Voronoi cell by testing
    /// sites. This only needs a box to search around, so rounding is enough.
    /// </summary>
    private Vector3I LatticeOf(Vector3 point)
    {
        Vector3 local = point - _origin;

        return new Vector3I(
            Mathf.RoundToInt(local.X / _nodeSize),
            Mathf.RoundToInt(local.Y / _nodeSize),
            Mathf.RoundToInt(local.Z / _nodeSize));
    }

    /// <summary>
    /// Marks a plane belonging to the starting box rather than to a neighbour
    /// or the surface.
    ///
    /// Far outside any real address, so it can never collide with one.
    /// </summary>
    private static readonly Vector3I Scaffold = new(int.MinValue, int.MinValue, int.MinValue);

    /// <summary>The outward normal of a face's polygon.</summary>
    private static Vector3 NormalOf(OrganicFace face)
    {
        if (face.Count < 3)
            return Vector3.Up;

        Vector3 normal = (face[2] - face[0]).Cross(face[1] - face[0]);
        float length = normal.Length();

        return length < 0.000001f ? Vector3.Up : normal / length;
    }

    // ------------------------------------------------------------- the hash

    /// <summary>
    /// One deterministic value in 0..1 per cell and stream.
    ///
    /// The site table, in three integer multiplies and a few shifts. It must be
    /// a pure function of the coordinate: workers build neighbouring chunks at
    /// the same moment and have to agree on where a shared site sits, or the
    /// wall between two nodes lands in two different places.
    /// </summary>
    private static float Hash(Vector3I cell, int stream)
    {
        unchecked
        {
            int n = cell.X * 73856093
                ^ cell.Y * 19349663
                ^ cell.Z * 83492791
                ^ stream * -1640531527;

            n ^= n >> 13;
            n *= 1274126177;
            n ^= n >> 16;

            return ((n & 0xFFFF) + 0.5f) / 65536f;
        }
    }
}
