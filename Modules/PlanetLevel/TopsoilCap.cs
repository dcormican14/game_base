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
/// WHAT IT LOOKS LIKE. Two tans, the darker one laid down in logarithmic
/// spirals summed over octaves, so the ground carries a weathered swirl at
/// every scale instead of one flat colour. See <see cref="Spiral"/>.
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

    /// <summary>How rough the surface looks, in world units of displacement.</summary>
    [Export] public float Grain { get; set; } = 0.06f;

    /// <summary>The dirt colour: the lighter tan the spirals are drawn over.</summary>
    [Export] public Color Soil { get; set; } = new(0.62f, 0.49f, 0.32f);

    /// <summary>
    /// The darker tan the spiral arms are painted in.
    ///
    /// A DARKER TAN RATHER THAN A DIFFERENT HUE. The two colours have to read
    /// as the same earth in two shades -- the moment the darker one drifts
    /// toward grey or red the pattern stops looking like soil and starts
    /// looking like something spilled on it.
    /// </summary>
    [Export] public Color SoilDark { get; set; } = new(0.34f, 0.25f, 0.15f);

    /// <summary>How much lighter or darker a grain may be.</summary>
    [Export] public float Mottle { get; set; } = 0.10f;

    // ------------------------------------------------------------- the spiral

    /// <summary>
    /// How many arms wind out of each spiral centre.
    ///
    /// The whole-number part is what you count on the ground; it is a float so
    /// the arms can be tuned off a whole number, which stops every centre on
    /// the planet looking like the same stamp.
    /// </summary>
    [Export(PropertyHint.Range, "1,12,0.5")] public float Arms { get; set; } = 3f;

    /// <summary>
    /// How tightly the arms wind, in turns per unit of log-radius.
    ///
    /// The arms are LOGARITHMIC, which is the one choice here that does most of
    /// the work. A spiral of constant angular pitch bunches its arms into an
    /// unreadable smear near the centre and stretches them into straight rays
    /// far out; winding by the LOG of the distance keeps the arms the same
    /// width apart at every scale, which is what makes the pattern hold
    /// together whether you are standing on it or looking down from orbit.
    /// </summary>
    [Export(PropertyHint.Range, "0.2,6,0.1")] public float Twist { get; set; } = 1.6f;

    /// <summary>
    /// How many times the spiral is redrawn, each smaller and fainter.
    ///
    /// This is the "fractal" half: the same spiral function summed over
    /// octaves, each at roughly twice the frequency and around half the weight,
    /// so a big lazy swirl carries smaller swirls on its arms and those carry
    /// smaller ones again. One octave is a logo; four is terrain.
    /// </summary>
    [Export(PropertyHint.Range, "1,6,1")] public int Octaves { get; set; } = 4;

    /// <summary>
    /// How strongly the dark tan shows, 0 none and 1 full.
    ///
    /// Kept below 1 so the arms stay soil-coloured rather than becoming a
    /// stencil: at 0.65 the darkest part of an arm is most of the way to
    /// <see cref="SoilDark"/> but still has the base tan showing through it.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float Swirl { get; set; } = 0.65f;

    /// <summary>
    /// How many spiral centres are scattered over the planet.
    ///
    /// NOT ONE SPIRAL, AND NOT A GRID OF THEM. A single centre gives the planet
    /// a pole the pattern winds out of, which reads as a target painted on it.
    /// Centres are placed on a Fibonacci sphere instead -- even coverage with no
    /// repeating direction -- and each point takes the pattern of the nearest
    /// few, so the arms of neighbouring spirals run into one another and braid
    /// the way weathered ground does.
    /// </summary>
    [Export(PropertyHint.Range, "1,64,1")] public int Centres { get; set; } = 14;

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
        if (_collider != null && Godot.GodotObject.IsInstanceValid(_collider))
            _collider.QueueFree();

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

        // The six faces, as an origin and two edge vectors on the unit cube.
        //
        // ORIENTED SO ACROSS x DOWN POINTS OUTWARD on every one of them. Got by
        // hand the first time, and two faces came out mirrored -- the audit
        // found 8,192 inside-out quads, which is exactly two faces' worth. The
        // winding below is uniform, so each face's own axes have to be right
        // rather than fixed up afterwards.
        Vector3[] origin =
        {
            new(-1, -1, -1),  // -Z
            new(1, -1, -1),   // +X
            new(1, -1, 1),    // +Z
            new(-1, -1, 1),   // -X
            new(-1, 1, -1),   // +Y
            new(-1, -1, 1),   // -Y
        };

        Vector3[] across =
        {
            new(2, 0, 0),     // -Z
            new(0, 0, 2),     // +X
            new(-2, 0, 0),    // +Z
            new(0, 0, -2),    // -X
            new(2, 0, 0),     // +Y
            new(2, 0, 0),     // -Y
        };

        Vector3[] down =
        {
            new(0, 2, 0),     // -Z
            new(0, 2, 0),     // +X
            new(0, 2, 0),     // +Z
            new(0, 2, 0),     // -X
            new(0, 0, 2),     // +Y
            new(0, 0, -2),    // -Y
        };

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
                float rough = (Noise(dir * 40f) - 0.5f) * Grain;

                verts.Add(dir * (radius + rough));
                norms.Add(dir);

                // THE PATTERN, then the grain -- in that order, because the
                // grain has to sit ON the spiral rather than the spiral being
                // an average of grain. Mixing them the other way round gave a
                // pattern that dissolved wherever the noise happened to be
                // bright.
                float swirl = Spiral(dir);

                Color tint = Soil.Lerp(SoilDark, swirl * Swirl);

                float shade = 1f + (Noise(dir * 18f) - 0.5f) * 2f * Mottle;
                colors.Add(new Color(tint.R * shade, tint.G * shade, tint.B * shade));
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

        var material = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,

            // Dirt is rough and not shiny. Without this the shell reads as
            // polished stone under a moving light.
            Roughness = 0.95f,
            Metallic = 0f,
        };

        mesh.SurfaceSetMaterial(0, material);

        return mesh;
    }

    /// <summary>
    /// How much dark tan belongs at this direction, 0 none and 1 full.
    ///
    /// Summed over octaves: each one is the same spiral drawn at a higher
    /// frequency and a lower weight, which is what makes the result fractal
    /// rather than merely swirly.
    /// </summary>
    private float Spiral(Vector3 dir)
    {
        float total = 0f, weight = 0f;
        float amplitude = 1f, frequency = 1f;

        for (int o = 0; o < Mathf.Max(1, Octaves); o++)
        {
            total += Arm(dir, frequency, o) * amplitude;
            weight += amplitude;

            // Not exactly two and a half, so the octaves do not land on
            // harmonics of one another and beat into visible rings.
            frequency *= 2.17f;
            amplitude *= 0.52f;
        }

        float v = total / Mathf.Max(0.0001f, weight);

        // Pushed toward its ends so the arms have edges. Straight off the sum
        // the pattern is a smooth gradient with no arm you could point at.
        return Mathf.SmoothStep(0.30f, 0.70f, v);
    }

    /// <summary>
    /// One octave: the spiral field around the nearest few centres.
    ///
    /// For each centre, the point is described by how far round it sits
    /// (azimuth) and how far away (arc distance), and an arm is a band in
    /// azimuth that SHIFTS with the log of the distance. Walking outward at a
    /// fixed azimuth therefore crosses arm after arm, which is what winding
    /// means.
    /// </summary>
    private float Arm(Vector3 dir, float frequency, int octave)
    {
        // The nearest few centres, blended -- see the note at the bottom of the
        // loop for why this is not simply the strongest one.
        Span<float> bandAt = stackalloc float[Near];
        Span<float> weightAt = stackalloc float[Near];
        Span<float> arcAt = stackalloc float[Near];

        int held = 0;

        int count = Mathf.Max(1, Centres);

        for (int i = 0; i < count; i++)
        {
            Vector3 centre = Centre(i, count, octave);

            // Arc distance from the centre, in radians: 0 at the centre and
            // pi at the point opposite it.
            float cos = Mathf.Clamp(centre.Dot(dir), -1f, 1f);
            float arc = Mathf.Acos(cos);

            // Only the nearby centres matter. Past this the arms have wound so
            // tight they are noise, and every centre contributing everywhere
            // averages the whole planet to a flat tone.
            const float reach = 1.1f;
            if (arc > reach) continue;

            // A frame on the sphere at this centre, to measure azimuth in.
            // Built from whichever world axis is least aligned with the centre,
            // so the cross product never collapses.
            Vector3 axis = Mathf.Abs(centre.Y) < 0.9f ? Vector3.Up : Vector3.Right;
            Vector3 east = axis.Cross(centre).Normalized();
            Vector3 north = centre.Cross(east).Normalized();

            float azimuth = Mathf.Atan2(dir.Dot(north), dir.Dot(east));

            // THE WINDING. Log of the distance, so the arms stay evenly spaced
            // at every scale rather than bunching at the centre. The offset
            // keeps the log finite as arc approaches zero.
            float wind = Mathf.Log(arc * frequency + 0.08f) * Twist;

            float phase = (azimuth * Arms) / Mathf.Tau + wind;

            // The arm itself: a cosine band round the spiral, in 0..1.
            float band = 0.5f + 0.5f * Mathf.Cos(phase * Mathf.Tau);

            // Faded out at the edge of the centre's reach, so an arm ends by
            // thinning rather than by being cut off mid-stroke.
            float fade = 1f - Mathf.SmoothStep(reach * 0.55f, reach, arc);

            // FLATTENED TOWARD PLAIN SOIL AT THE EYE, which is where the
            // pattern outruns the mesh.
            //
            // The arms wind by the LOG of the distance, so their radial
            // spacing collapses as the centre is approached: worked out
            // against the quad step, the bands pass half a cycle per quad
            // inside about a tenth of a radian and are simply not
            // representable there. Sampled anyway, the eye came out as a speck
            // of noise -- two vertices one quad apart differing by 0.64 of the
            // whole tonal range.
            //
            // Toward the MIDDLE of the range rather than toward zero. Fading
            // the band itself to zero fades the eye to the LIGHT tan, which
            // just moves the step somewhere else; 0.5 is the tone the soil
            // already averages, so the eye melts into it instead.
            //
            // A spiral's eye being a patch of plain earth is also what the real
            // thing looks like.
            band = Mathf.Lerp(0.5f, band, Mathf.SmoothStep(0.05f, 0.34f, arc));

            // KEPT IF IT IS AMONG THE NEAREST FEW, by displacing the furthest
            // one held so far.
            //
            // NOT THE STRONGEST CENTRE, which is what this did first and which
            // measured badly: taking the max over fourteen overlapping spirals
            // is near 1 almost everywhere, so 63% of the planet came out fully
            // dark and the median saturated -- a dark ball with thin light
            // gaps rather than arms on soil.
            //
            // NOT THE SUM OF ALL OF THEM EITHER: that averages overlapping
            // spirals into a uniform mid-tone exactly where two patterns meet,
            // which is the most interesting place on the planet and the last
            // place it should go flat.
            //
            // The nearest few, weighted by how strongly each reaches here, is
            // what keeps arms dark where one spiral owns the ground and lets
            // neighbouring spirals braid where they meet.
            if (held < Near)
            {
                bandAt[held] = band; weightAt[held] = fade; arcAt[held] = arc;
                held++;
            }
            else
            {
                int furthest = 0;

                for (int k = 1; k < Near; k++)
                    if (arcAt[k] > arcAt[furthest]) furthest = k;

                if (arc < arcAt[furthest])
                {
                    bandAt[furthest] = band;
                    weightAt[furthest] = fade;
                    arcAt[furthest] = arc;
                }
            }
        }

        // Nothing reaches here: the plain soil tone, which is the middle of the
        // range rather than 0 -- returning 0 would ring the bare patches with a
        // bright halo the pattern never earned.
        if (held == 0) return 0.5f;

        float sum = 0f, total = 0f;

        for (int k = 0; k < held; k++)
        {
            // SQUARED, so a centre that barely reaches here barely counts.
            // Linear weights let distant spirals drag every value toward the
            // mean and the arms lost their edges.
            float w = weightAt[k] * weightAt[k] + 0.0001f;

            sum += bandAt[k] * w;
            total += w;
        }

        return total > 0f ? sum / total : 0.5f;
    }

    /// <summary>
    /// How many spiral centres may colour one point.
    ///
    /// Three is enough for arms to braid where spirals meet without the count
    /// itself becoming an average of the whole planet.
    /// </summary>
    private const int Near = 3;

    /// <summary>
    /// Where the i-th spiral centre sits, as a unit direction.
    ///
    /// A Fibonacci sphere: points spaced by the golden angle, which covers a
    /// sphere about as evenly as a simple formula can and, unlike a lat/long
    /// grid, has no pole and no repeating row. Rotated per octave so the
    /// octaves do not all wind out of the same places.
    /// </summary>
    private static Vector3 Centre(int i, int count, int octave)
    {
        // Offset by the octave so each octave gets its own scatter, and by a
        // half so no centre lands exactly on a pole.
        float t = (i + 0.5f) / count;

        float y = 1f - 2f * t;
        float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));

        // The golden angle, plus a per-octave turn.
        float theta = i * 2.39996323f + octave * 1.61803399f;

        return new Vector3(Mathf.Cos(theta) * r, y, Mathf.Sin(theta) * r);
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
