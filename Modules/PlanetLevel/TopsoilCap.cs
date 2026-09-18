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
/// </summary>
[GlobalClass]
public partial class TopsoilCap : Node3D
{
    /// <summary>
    /// How far above the planet's radius the soil surface sits.
    ///
    /// CAUGHT BETWEEN TWO CONSTRAINTS. It has to clear the rock or the highest
    /// cells poke through the dirt -- their tops reach the radius exactly, and
    /// measured over 400 exposed nodes they span r 119.60 to 120.00, so anything
    /// positive covers them.
    ///
    /// But the shell carries no collision, so the player stands on the ROCK at
    /// radius 120 while seeing dirt at 120 plus this. At 0.45 that put them
    /// ankle deep in it. Kept small enough that the difference reads as standing
    /// ON soil rather than in it, while still burying the rock: the grain itself
    /// varies by 0.06, so this is comfortably above the noise.
    /// </summary>
    [Export] public float Depth { get; set; } = 0.12f;

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

    /// <summary>The dirt colour.</summary>
    [Export] public Color Soil { get; set; } = new(0.42f, 0.32f, 0.20f);

    /// <summary>How much lighter or darker a grain may be.</summary>
    [Export] public float Mottle { get; set; } = 0.14f;

    private MeshInstance3D _mesh;
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

                float shade = 1f + (Noise(dir * 18f) - 0.5f) * 2f * Mottle;
                colors.Add(new Color(Soil.R * shade, Soil.G * shade, Soil.B * shade));
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
