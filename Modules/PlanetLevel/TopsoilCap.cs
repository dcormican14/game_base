using System;
using Godot;
using GameBase.Nodes;

namespace GameBase.Levels;

/// <summary>
/// The planet's topsoil: a smooth shell of dirt laid over the raw rock.
///
/// NOT MADE OF NODES, and that is the whole point. Topsoil used to be the
/// material of the outermost band of Voronoi cells, which meant it inherited
/// every promise a cell has to keep -- tile exactly with its neighbours, share
/// each wall and corner, never overlap, never gap. Five attempts to make it look
/// like soil while keeping those promises each broke one: moving corners pushed
/// solids into one another by a whole node, varying the surface cut deleted
/// shared walls and opened holes, and a plane per cell disagreed with its
/// neighbour by a third of a node where the two meet.
///
/// A shell has none of those obligations. It is one closed surface over the
/// whole planet, so there is nothing to tile with and nothing to tear.
///
/// WHY A FIXED RADIUS WORKS. The rock underneath is far more level than it
/// looks: measured over 400 exposed nodes, the tops of the outermost cells span
/// r 119.60 to 120.00 -- four tenths of a unit on a two-unit node. So a shell a
/// little above the rock covers all of it without following it, which is what
/// lets the cap be smooth while the rock beneath stays shattered.
///
/// The grain comes from the surface itself rather than from geometry: the mesh
/// is displaced by a few hashed millimetres per vertex, enough to read as sand
/// or gravel under light without costing a triangle.
///
/// WHAT IT LOOKS LIKE. Sand: fine speckled grain, broad drift so no two
/// patches match, and low wind-blown ripples. Drawn PER PIXEL by
/// TopsoilSand.gdshader, because this mesh cannot express it -- one quad of
/// the shell is about 3.9 units on the ground, so the finest mark a vertex
/// colour can make is a blotch that size, blurred soft across the quad.
///
/// WHAT YOU STAND ON. The shell carries its own collider, parented to the
/// NodeWorld body rather than to this node -- see <see cref="BuildCollider"/>.
/// That is what lets the soil be a real layer: while it was decorative the
/// player stood on the rock beneath it, so every unit of depth was a unit they
/// stood buried in, and the depth had to stay near zero to hide the problem.
/// </summary>
[GlobalClass]
public partial class TopsoilCap : Node3D
{
    /// <summary>
    /// How far above the planet's radius the soil surface sits.
    ///
    /// ONCE CAUGHT BETWEEN TWO CONSTRAINTS, NOW ONLY ONE. It still has to clear
    /// the rock or the highest cells poke through the dirt -- their tops reach
    /// the radius exactly, and measured over 400 exposed nodes they span r
    /// 119.60 to 120.00, so anything positive covers them.
    ///
    /// The other constraint is gone. This used to be pinned near zero because
    /// the shell carried no collision: the player stood on the ROCK at radius
    /// 120 while seeing dirt above them, so every unit of depth was a unit they
    /// stood buried in, and 0.45 was already ankle deep. The shell now carries
    /// its own collider (see <see cref="Collide"/>), so the surface you see is
    /// the surface you stand on and depth is free to be a real layer.
    ///
    /// Two nodes' worth, so the soil reads as a mantle over the rock at the
    /// horizon and when mining cuts down through it.
    /// </summary>
    [Export] public float Depth { get; set; } = 4.0f;

    /// <summary>
    /// How finely the shell is divided, per cube face.
    ///
    /// A cubed sphere rather than a UV sphere: no pole, no crowding, and every
    /// quad is about the same size. At 64 the quads are roughly a node across at
    /// radius 120, which is fine enough that the silhouette reads as round.
    /// </summary>
    [Export] public int Resolution { get; set; } = 64;

    /// <summary>
    /// How rough the surface is, in world units of vertex displacement.
    ///
    /// GEOMETRY, not colour -- this bends the mesh itself, and is a different
    /// thing from <see cref="Speckle"/>, which shades the sand without moving
    /// it. Both are "grain" in ordinary speech, which is why neither is called
    /// that any more.
    /// </summary>
    [Export] public float Displace { get; set; } = 0.06f;

    /// <summary>
    /// The sand colour.
    ///
    /// CALIBRATED AGAINST A RENDER AND AGAINST THE ROCK -- see the note in
    /// TopsoilSand.gdshader. The scene tonemaps Filmic under a bright sun and
    /// is tuned for dark terrain (the rock beside this is albedo 0.155 to
    /// 0.245), so a tan picked as the colour wanted on screen rendered as
    /// near-white cream.
    /// </summary>
    [Export] public Color Sand { get; set; } = new(0.24f, 0.180f, 0.105f);

    /// <summary>
    /// The darker tan the grain and ripples shade toward.
    ///
    /// A DARKER TAN RATHER THAN A DIFFERENT HUE. The two have to read as the
    /// same sand in two shades -- the moment the darker one drifts toward grey
    /// or red the surface stops looking like sand.
    /// </summary>
    [Export] public Color SandDark { get; set; } = new(0.150f, 0.108f, 0.060f);


    // --------------------------------------------------------------- the sand

    /// <summary>
    /// How fine the speckle is. Higher is finer.
    ///
    /// The number that decides whether this looks like sand or like stucco.
    /// Well above the shell's own resolution by design: the grain is meant to
    /// be far smaller than a quad, which is the whole reason it is drawn in a
    /// shader rather than baked into the mesh.
    /// </summary>
    [Export(PropertyHint.Range, "50,4000,10")]
    public float SpeckleScale { get; set; } = 1400f;

    /// <summary>
    /// How strongly the speckle shows.
    ///
    /// COLOUR, not geometry: this shades the sand without moving the surface,
    /// unlike <see cref="Displace"/>.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float Speckle { get; set; } = 0.55f;

    /// <summary>
    /// How many flat levels the speckle is stepped into.
    ///
    /// Sand is made of discrete bits, and smooth tone reads as airbrush. Few
    /// levels, because many is indistinguishable from smooth.
    /// </summary>
    [Export(PropertyHint.Range, "2,12,1")] public int SpeckleLevels { get; set; } = 5;

    /// <summary>How broad the slow variation is. Lower is broader.</summary>
    [Export(PropertyHint.Range, "1,200,1")] public float DriftScale { get; set; } = 26f;

    /// <summary>How much one patch of ground differs from the next.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float Drift { get; set; } = 0.35f;

    /// <summary>How tight the wind ripples are.</summary>
    [Export(PropertyHint.Range, "1,300,1")] public float RippleScale { get; set; } = 60f;

    /// <summary>
    /// How far the ripples are stretched across the wind.
    ///
    /// The number that makes them ripples. Round noise gives blotches; the
    /// stretch is what turns the same noise into long parallel banding.
    /// </summary>
    [Export(PropertyHint.Range, "1,30,0.5")]
    public float RippleStretch { get; set; } = 9f;

    /// <summary>
    /// How strongly the ripples show.
    ///
    /// LOW. Ripples are the feature that says sand rather than dirt, and also
    /// the first thing to look wrong when overdone -- past about half they stop
    /// reading as wind on a surface and start reading as stripes painted on it.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float Ripples { get; set; } = 0.30f;

    /// <summary>How sharply a ripple crest turns into a trough.</summary>
    [Export(PropertyHint.Range, "1,8,0.1")]
    public float RippleSharpness { get; set; } = 2.2f;

    /// <summary>
    /// Whether the shell is something you can stand on.
    ///
    /// ON, and the reason it is an option at all is that it used to be off by
    /// necessity. A decorative shell cannot be walked on, so the player stood
    /// on the rock underneath and the soil had to stay thin enough to be a
    /// coat of paint or they stood buried in it. Given a collider the shell
    /// becomes the ground, and the depth is free.
    ///
    /// Left switchable because a shell with collision has one failure mode a
    /// decorative one does not: if it ever sealed over a hole the player is
    /// supposed to get into, turning this off says whether the shell is why.
    /// </summary>
    [Export] public bool Collide { get; set; } = true;

    private MeshInstance3D _mesh;
    private CollisionShape3D _collider;
    private float _radius = -1f;

    /// <summary>
    /// Builds the shell for a planet of this radius.
    ///
    /// Called by the streamer once the grid exists, since the radius belongs to
    /// the grid rather than to this node.
    /// </summary>
    public void Build(float surfaceRadius)
    {
        if (Mathf.IsEqualApprox(_radius, surfaceRadius) && _mesh != null)
            return;

        _radius = surfaceRadius;

        _mesh?.QueueFree();

        _mesh = new MeshInstance3D
        {
            Name = "Soil",
            Mesh = Shell(surfaceRadius + Depth),

            // NO SHADOW CASTING INWARD. The shell encloses the rock, so a
            // shadow it cast would fall on the very surface it covers and the
            // whole planet would go dark underneath.
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };

        AddChild(_mesh);

        BuildCollider();
    }

    /// <summary>
    /// Gives the shell a collider, so the dirt you see is the dirt you stand on.
    ///
    /// PARENTED TO THE BODY, NOT TO THIS NODE. A CollisionShape3D only counts
    /// when it is a DIRECT child of the PhysicsBody -- put under this Node3D it
    /// sits in the tree looking perfectly correct, draws its outline in the
    /// editor's debug view, and collides with absolutely nothing. The shell is
    /// a child of the NodeWorld, which is the StaticBody3D the rock already
    /// collides as, so the shape goes up one level to there.
    ///
    /// The rock keeps its own colliders underneath. They now sit entirely
    /// inside the soil and so are never touched from outside, but they are what
    /// the player stands on the moment a dig opens the shell, and they are what
    /// stops a dug pit being a hole clean through the planet.
    /// </summary>
    private void BuildCollider()
    {
        // Any previous one goes, whether or not a new one is wanted -- turning
        // Collide off has to actually remove the collision, not just stop
        // refreshing it.
        //
        // FREED NOW, NOT QUEUED. QueueFree defers to the end of the frame, so
        // the old shape is still registered with the physics server while the
        // new one is added -- the planet briefly has TWO surfaces, and a ray
        // cast in between hits whichever is higher. With the shovel that is
        // the ground as it was before the dig, so digging appeared to do
        // nothing at all while the mesh underneath was changing correctly.
        if (_collider != null && Godot.GodotObject.IsInstanceValid(_collider))
        {
            _collider.GetParent()?.RemoveChild(_collider);
            _collider.Free();
        }

        _collider = null;

        if (!Collide || _mesh?.Mesh == null)
            return;

        if (GetParent() is not CollisionObject3D body)
        {
            GD.PushWarning("TopsoilCap: no physics body above the shell, so the "
                + "soil is decorative and the player stands on the rock under it.");
            return;
        }

        _collider = new CollisionShape3D
        {
            Name = "SoilCollision",

            // Built from the same mesh the eye sees, so there is no second
            // surface to drift out of agreement with the first.
            Shape = _mesh.Mesh.CreateTrimeshShape(),
        };

        body.AddChild(_collider);
    }

    // The six faces of the cube, as an origin and two edge vectors.
    //
    // ORIENTED SO ACROSS x DOWN POINTS OUTWARD on every one of them. Got by
    // hand the first time, and two faces came out mirrored -- the mesh audit
    // found 8,192 inside-out quads, which is exactly two faces' worth. The
    // winding is uniform, so each face's own axes have to be right rather than
    // fixed up afterwards.
    //
    // STATIC, AND SHARED, because the mesh builder and the height field's
    // direction walk must agree about the order of every vertex on the planet.
    private static readonly Vector3[] FaceOrigins =
    {
        new(-1, -1, -1),  // -Z
        new(1, -1, -1),   // +X
        new(1, -1, 1),    // +Z
        new(-1, -1, 1),   // -X
        new(-1, 1, -1),   // +Y
        new(-1, -1, 1),   // -Y
    };

    private static readonly Vector3[] FaceAcross =
    {
        new(2, 0, 0),     // -Z
        new(0, 0, 2),     // +X
        new(-2, 0, 0),    // +Z
        new(0, 0, -2),    // -X
        new(2, 0, 0),     // +Y
        new(2, 0, 0),     // -Y
    };

    private static readonly Vector3[] FaceDown =
    {
        new(0, 2, 0),     // -Z
        new(0, 2, 0),     // +X
        new(0, 2, 0),     // +Z
        new(0, 2, 0),     // -X
        new(0, 0, 2),     // +Y
        new(0, 0, -2),    // -Y
    };

    /// <summary>
    /// Walks every vertex direction of the shell, in build order.
    ///
    /// SHARED WITH THE MESH BUILDER ON PURPOSE. The height field is indexed by
    /// position in this walk, so if the two ever disagreed about the order or
    /// the count, a dig would move sand somewhere other than where the player
    /// aimed -- a failure that looks like a physics bug rather than a mismatch.
    /// One walk, used by both, cannot drift.
    /// </summary>
    private void ForEachShellDirection(System.Action<Vector3> each)
    {
        int n = Mathf.Max(2, Resolution);

        Vector3[] origin = FaceOrigins;
        Vector3[] across = FaceAcross;
        Vector3[] down = FaceDown;

        for (int f = 0; f < 6; f++)
        for (int y = 0; y <= n; y++)
        for (int x = 0; x <= n; x++)
        {
            Vector3 on = origin[f]
                + across[f] * ((float)x / n)
                + down[f] * ((float)y / n);

            each(on.Normalized());
        }
    }

    // ------------------------------------------------------------ the digging

    /// <summary>
    /// How far the sand has been raised or lowered at each vertex of the shell.
    ///
    /// ONE FLOAT PER VERTEX, in the same order the shell builds them, rather
    /// than a sparse map of edits. The shell is rebuilt from this array every
    /// time it changes, so the two must line up exactly; an index is cheaper
    /// and less error-prone to keep in step than a position would be, since the
    /// vertex order is already fixed by the cubed-sphere construction.
    ///
    /// Null until something digs. A planet nobody has touched costs nothing,
    /// and <see cref="HeightAt"/> reads zero everywhere.
    /// </summary>
    private float[] _height;

    /// <summary>
    /// The shovel's reach into the array: which vertex a direction belongs to.
    ///
    /// The cubed sphere is not an even angular grid, so this cannot be a
    /// closed-form lookup. It is built once, the first time anything digs, as a
    /// flat list of vertex directions -- the shovel then finds every vertex
    /// within its radius by distance, which is the same test the falloff needs
    /// anyway.
    /// </summary>
    private Vector3[] _vertexDir;

    /// <summary>
    /// The sculpted height at a direction, for the shell builder.
    ///
    /// Reads the array by the index the builder is ABOUT to write, which is why
    /// the builder passes its own running count rather than a position: looking
    /// the direction back up would mean a search per vertex on every rebuild.
    /// </summary>
    private float HeightAt(Vector3 dir)
    {
        if (_height == null) return 0f;

        return _buildIndex < _height.Length ? _height[_buildIndex] : 0f;
    }

    /// <summary>Where the shell builder has got to, so HeightAt can read along
    /// with it. Reset at the start of every build.</summary>
    private int _buildIndex;

    /// <summary>
    /// Raises or lowers the sand around a point on the surface.
    ///
    /// SMOOTH FALLOFF FROM THE CENTRE, so a dig leaves a rounded hollow or
    /// mound rather than a cylinder punched into the ground. A flat disc would
    /// be simpler and is wrong for sand: it leaves a vertical wall at the rim
    /// that sand could not hold.
    /// </summary>
    /// <param name="at">A point on or near the surface -- only its direction
    /// from the planet's centre is used.</param>
    /// <param name="radius">How wide the dig reaches, in world units.</param>
    /// <param name="amount">How far to move the sand. Positive raises.</param>
    /// <returns>True when anything actually moved.</returns>
    public bool Sculpt(Vector3 at, float radius, float amount)
    {
        if (_mesh == null || _radius <= 0f) return false;
        if (at.LengthSquared() < 0.0001f) return false;

        EnsureField();

        if (_vertexDir == null || _vertexDir.Length == 0) return false;

        Vector3 centre = at.Normalized();

        // The dig radius as an ANGLE, since the field is indexed by direction.
        float arcLimit = radius / Mathf.Max(1f, _radius);
        float cosLimit = Mathf.Cos(Mathf.Min(arcLimit, Mathf.Pi));

        bool moved = false;

        for (int i = 0; i < _vertexDir.Length; i++)
        {
            float cos = centre.Dot(_vertexDir[i]);

            if (cos < cosLimit) continue;

            float arc = Mathf.Acos(Mathf.Clamp(cos, -1f, 1f));
            float t = Mathf.Clamp(arc / Mathf.Max(arcLimit, 0.00001f), 0f, 1f);

            // Smooth to nothing at the rim: 1 at the centre, 0 at the edge,
            // with zero slope at both ends so repeated digs blend instead of
            // stacking into terraces.
            float falloff = 1f - Mathf.SmoothStep(0f, 1f, t);

            if (falloff <= 0f) continue;

            float before = _height[i];

            _height[i] = Mathf.Clamp(
                before + amount * falloff, -MaxDepth, MaxHeight);

            if (!Mathf.IsEqualApprox(before, _height[i])) moved = true;
        }

        if (moved) Refresh();

        return moved;
    }

    /// <summary>How far the sand may be piled above its resting surface.</summary>
    [Export(PropertyHint.Range, "0,20,0.5")] public float MaxHeight { get; set; } = 6f;

    /// <summary>
    /// How far the sand may be dug below its resting surface.
    ///
    /// CAPPED SHORT OF THE ROCK. The shell sits <see cref="Depth"/> above the
    /// rock, and digging past that would put the sand surface underneath the
    /// stone it is supposed to cover -- the rock would poke through the sand
    /// and the player would stand on stone while looking at a hole. Kept just
    /// inside that, so the deepest pit still has a skin of sand at the bottom.
    /// </summary>
    public float MaxDepth => Mathf.Max(0f, Depth - 0.35f);

    /// <summary>The height field and the directions that index it, made on
    /// first use.</summary>
    private void EnsureField()
    {
        if (_vertexDir != null && _height != null
            && _height.Length == _vertexDir.Length)
            return;

        var dirs = new System.Collections.Generic.List<Vector3>();

        ForEachShellDirection(dirs.Add);

        _vertexDir = dirs.ToArray();

        // Kept if it is already the right size: a rebuild at the same
        // resolution must not wipe what the player has dug.
        if (_height == null || _height.Length != _vertexDir.Length)
            _height = new float[_vertexDir.Length];
    }

    /// <summary>
    /// Rebuilds the mesh and its collider from the current height field.
    ///
    /// BOTH, and that is the point. The collider is built from the mesh, so a
    /// dig that updated only the mesh would leave the player walking on the
    /// shape of the ground as it was before they dug it.
    /// </summary>
    private void Refresh()
    {
        if (_mesh == null || _radius <= 0f) return;

        _mesh.Mesh = Shell(_radius + Depth);

        BuildCollider();
    }

    /// <summary>
    /// Takes the collider with it.
    ///
    /// The collider is parented to the BODY rather than to this node, so it
    /// does not go when this node does. Without this, a rebuild that replaces
    /// the cap leaves the old shape behind on the body, still colliding, and
    /// the planet slowly accumulates invisible shells of dead ground.
    /// </summary>
    public override void _ExitTree()
    {
        if (_collider != null && Godot.GodotObject.IsInstanceValid(_collider))
            _collider.QueueFree();

        _collider = null;
    }

    /// <summary>
    /// A cubed sphere: six grids projected onto the shell.
    ///
    /// Wound so the faces look outward, and welded at the seams by construction
    /// -- neighbouring faces compute the same corner from the same formula, so
    /// no crack can open between them however fine the division.
    /// </summary>
    private ArrayMesh Shell(float radius)
    {
        var verts = new System.Collections.Generic.List<Vector3>();
        var norms = new System.Collections.Generic.List<Vector3>();
        var colors = new System.Collections.Generic.List<Color>();
        var indices = new System.Collections.Generic.List<int>();

        int n = Mathf.Max(2, Resolution);

        // The height field is read in step with this walk, so the cursor starts
        // over with it.
        _buildIndex = 0;

        // The six faces, as an origin and two edge vectors on the unit cube.
        //
        // ORIENTED SO ACROSS x DOWN POINTS OUTWARD on every one of them. Got by
        // hand the first time, and two faces came out mirrored -- the audit
        // found 8,192 inside-out quads, which is exactly two faces' worth. The
        // winding below is uniform, so each face's own axes have to be right
        // rather than fixed up afterwards.
        Vector3[] origin = FaceOrigins;
        Vector3[] across = FaceAcross;
        Vector3[] down = FaceDown;

        for (int f = 0; f < 6; f++)
        {
            int start = verts.Count;

            for (int y = 0; y <= n; y++)
            for (int x = 0; x <= n; x++)
            {
                Vector3 on = origin[f]
                    + across[f] * ((float)x / n)
                    + down[f] * ((float)y / n);

                Vector3 dir = on.Normalized();

                // THE GRAIN. A few hashed millimetres along the normal, so the
                // shell reads as sand rather than as glass. Hashed from the
                // DIRECTION, so the same point is displaced the same way every
                // rebuild and the two sides of a seam agree.
                float rough = (Noise(dir * 40f) - 0.5f) * Displace;

                // AND WHATEVER THE SHOVEL HAS DONE HERE. The height field is
                // the only part of this surface that is not a pure function of
                // direction -- it is the record of the player's digging, so it
                // has to be read per vertex rather than derived.
                verts.Add(dir * (radius + rough + HeightAt(dir)));
                norms.Add(dir);

                // FLAT. The shader draws the pattern per pixel, so the mesh
                // carries no colour of its own -- a vertex tint under it would
                // only reintroduce the soft blotches the shader exists to
                // replace, showing through between the lines.
                colors.Add(new Color(1f, 1f, 1f));

                _buildIndex++;
            }

            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int a = start + y * (n + 1) + x;
                int b = a + 1;
                int c = a + (n + 1);
                int d = c + 1;

                // WOUND AGAINST THE NORMAL, checked by the mesh audit rather
                // than by eye.
                //
                // Godot's front face is the CLOCKWISE one seen from the front,
                // so a triangle faces out when (v2-v0) x (v1-v0) agrees with the
                // outward normal. The other order compiles, draws, and is
                // invisible from outside: the audit found all 40,960 of these
                // quads inside out, which is a shell you can only see from
                // within the planet.
                indices.Add(a); indices.Add(b); indices.Add(c);
                indices.Add(b); indices.Add(d); indices.Add(c);
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = norms.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        mesh.SurfaceSetMaterial(0, SandMaterial());

        return mesh;
    }

    /// <summary>
    /// The pattern material: fine spiral lines, drawn per pixel.
    ///
    /// A SHADER RATHER THAN VERTEX COLOURS, which is a correction of how this
    /// was first built. Vertex colour cannot draw a line: one quad of the shell
    /// is about 3.9 world units on the ground, a mark needs three or so quads
    /// to register, and the colour is interpolated across each quad regardless.
    /// The thinnest possible mark was therefore some twelve units wide with
    /// soft edges -- six times the player's height -- which is exactly the
    /// "splotchy and blurred" it looked like. Raising the shell's resolution
    /// does not rescue it: even at 512 a side, which is 1.6 million vertices,
    /// a line is still 1.5 units across and still blurred by interpolation.
    ///
    /// Sampling per PIXEL removes the limit entirely, and costs one material
    /// rather than any extra geometry.
    /// </summary>
    private ShaderMaterial SandMaterial()
    {
        var shader = GD.Load<Shader>("res://Modules/PlanetLevel/TopsoilSand.gdshader");

        if (shader == null)
        {
            GD.PushWarning("TopsoilCap: the sand shader is missing, so the "
                + "soil falls back to a flat tan.");

            return null;
        }

        var material = new ShaderMaterial { Shader = shader };

        material.SetShaderParameter("sand", new Vector3(Sand.R, Sand.G, Sand.B));
        material.SetShaderParameter("sand_dark",
            new Vector3(SandDark.R, SandDark.G, SandDark.B));

        material.SetShaderParameter("grain_scale", SpeckleScale);
        material.SetShaderParameter("grain", Speckle);
        material.SetShaderParameter("grain_levels", SpeckleLevels);

        material.SetShaderParameter("drift_scale", DriftScale);
        material.SetShaderParameter("drift", Drift);

        material.SetShaderParameter("ripple_scale", RippleScale);
        material.SetShaderParameter("ripple_stretch", RippleStretch);
        material.SetShaderParameter("ripples", Ripples);
        material.SetShaderParameter("ripple_sharpness", RippleSharpness);

        return material;
    }

    /// <summary>
    /// A stable value in 0..1 from a position.
    ///
    /// Value noise smoothed between lattice points: continuous, so the grain has
    /// no visible grid to it, and deterministic, so it does not crawl.
    /// </summary>
    private static float Noise(Vector3 at)
    {
        var cell = new Vector3I(
            Mathf.FloorToInt(at.X), Mathf.FloorToInt(at.Y), Mathf.FloorToInt(at.Z));

        Vector3 t = at - new Vector3(cell.X, cell.Y, cell.Z);

        // Smoothstep, so the value has no kink at a lattice boundary.
        t = new Vector3(
            t.X * t.X * (3f - 2f * t.X),
            t.Y * t.Y * (3f - 2f * t.Y),
            t.Z * t.Z * (3f - 2f * t.Z));

        float result = 0f;

        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3I(cell.X + (i & 1), cell.Y + ((i >> 1) & 1),
                cell.Z + ((i >> 2) & 1));

            float weight =
                ((i & 1) == 0 ? 1f - t.X : t.X)
                * (((i >> 1) & 1) == 0 ? 1f - t.Y : t.Y)
                * (((i >> 2) & 1) == 0 ? 1f - t.Z : t.Z);

            result += Hash(corner) * weight;
        }

        return result;
    }

    private static float Hash(Vector3I at)
    {
        unchecked
        {
            uint h = (uint)(at.X * 73856093) ^ (uint)(at.Y * 19349663)
                ^ (uint)(at.Z * 83492791);

            h ^= h >> 13;
            h *= 0x5bd1e995u;
            h ^= h >> 15;

            return (h & 0xFFFFFF) / 16777216f;
        }
    }
}
