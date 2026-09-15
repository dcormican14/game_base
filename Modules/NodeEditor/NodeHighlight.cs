using Godot;
using System;
using GameBase.Nodes;
using GameBase.Core;

namespace GameBase.Player;

/// <summary>
/// Outlines the block the player is looking at.
///
/// The outline is the block's twelve edges, traced through the grid so it
/// follows the ARC of the cell rather than boxing it. That distinction is the
/// whole reason this is not a stock wireframe cube: a block on the sphere has
/// curved sides and its top and bottom lie on different spherical caps, so a
/// cube drawn from a centre and a size sits visibly off the surface.
///
/// Instance under the player alongside NodeEditor; it finds the camera and the
/// NodeWorld itself.
/// </summary>
public partial class NodeHighlight : Node3D
{
    [Export] public NodePath CameraPath { get; set; } = "";
    [Export] public NodePath NodeWorldPath { get; set; } = "";

    /// <summary>How far the player can target, matching NodeEditor's Reach.</summary>
    [Export(PropertyHint.Range, "1,100,0.5")] public float Reach { get; set; } = 6f;

    /// <summary>Warm off-white, matching the crosshair, so the two read as one
    /// targeting system rather than two unrelated marks.</summary>
    [Export] public Color OutlineColor { get; set; } = new(1f, 0.922f, 0.761f);

    /// <summary>
    /// Bar thickness as a fraction of a block.
    ///
    /// 0.025 is the practical floor: the stylised filter quantises to a 320-row
    /// grid, and at the far end of Reach a 0.025 bar spans about one virtual
    /// pixel. Anything thinner drops below a pixel there and flickers as the
    /// camera moves.
    /// </summary>
    [Export(PropertyHint.Range, "0.005,0.1,0.005")] public float Thickness { get; set; } = 0.025f;

    /// <summary>
    /// How far to lift the outline off the surface, as a fraction of a block.
    ///
    /// Without it the bars land exactly on the faces they trace and fight them
    /// for depth, which shows as the outline stitching in and out along its
    /// length.
    /// </summary>
    [Export(PropertyHint.Range, "0,0.05,0.002")] public float Expand { get; set; } = 0.006f;

    private Camera3D _camera;
    private NodeWorld _world;
    private CollisionObject3D _playerBody;

    private MeshInstance3D _mesh;
    private Vector3I _cell;
    private bool _visible;

    /// <summary>Is the outline currently drawn? For tests and tools.</summary>
    public bool Showing => _visible;

    /// <summary>The node the outline is on, when it is showing.</summary>
    public Vector3I Target => _cell;

    private StandardMaterial3D _material;

    /// <summary>Unshaded and drawn on top, so the outline reads the same
    /// against a lit face and a shadowed one.</summary>
    private StandardMaterial3D Material => _material ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = OutlineColor,
        NoDepthTest = false,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public override void _Ready()
    {
        _camera = !CameraPath.IsEmpty ? GetNodeOrNull<Camera3D>(CameraPath) : null;
        _camera ??= NodeSearch.FindByType<Camera3D>(GetTree().CurrentScene ?? GetParent());

        _world = !NodeWorldPath.IsEmpty ? GetNodeOrNull<NodeWorld>(NodeWorldPath) : null;
        _world ??= NodeSearch.FindByType<NodeWorld>(GetTree().CurrentScene ?? GetParent());

        for (Node node = GetParent(); node != null; node = node.GetParent())
        {
            if (node is CollisionObject3D body)
            {
                _playerBody = body;
                break;
            }
        }

        _mesh = new MeshInstance3D
        {
            Name = "Outline",

            // Built in world space, so the parent's transform must not move it
            // again.
            TopLevel = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };

        AddChild(_mesh);
        _mesh.Visible = false;
    }

    public override void _Process(double delta)
    {
        if (_camera == null || _world?.Grid == null)
            return;

        if (!TryGetTarget(out Vector3I cell))
        {
            if (_visible)
            {
                _mesh.Visible = false;
                _visible = false;
            }

            return;
        }

        // Rebuilt only when the target changes: the player spends most frames
        // looking at the same block.
        if (!_visible || cell != _cell)
        {
            _cell = cell;
            _visible = true;
            Rebuild(cell);
            _mesh.Visible = true;
        }
    }

    /// <summary>The block under the crosshair, if one is in reach.</summary>
    private bool TryGetTarget(out Vector3I cell)
    {
        cell = default;

        Vector2 centre = _camera.GetViewport().GetVisibleRect().Size * 0.5f;
        Vector3 from = _camera.ProjectRayOrigin(centre);
        Vector3 dir = _camera.ProjectRayNormal(centre);

        // Reach is measured from the PLAYER, matching NodeEditor: measuring
        // from the camera would silently shorten it by the third-person
        // set-back distance.
        float reach = Reach;
        if (_playerBody != null)
            reach += from.DistanceTo(_playerBody.GlobalPosition);

        return _world.RayPick(from, dir, reach, out cell, out _);
    }

    /// <summary>
    /// The edges of a node, as boxes.
    ///
    /// Corners come from the world, which maps them through the grid, so each
    /// bar spans the same arc the rendered face does.
    ///
    /// A node is a PRISM of however many sides its grid gives it -- four on the
    /// cubed sphere, six or five on the icosphere -- so the edges are walked as
    /// two rings plus the uprights between them. The previous version treated
    /// the corners as a cube's eight and paired those differing in one index
    /// bit, which on anything else joins corners at random: the highlight came
    /// out as a bowtie of struts across the middle of the block.
    /// </summary>
    private void Rebuild(Vector3I cell)
    {
        // A grid whose nodes are not prisms has no two rings to walk: an
        // organic cell has a dozen-odd faces pointing every way, so its edges
        // are traced face by face instead.
        if (_world.Grid is IPolyhedralGrid polyhedral)
        {
            RebuildPolyhedral(cell, polyhedral);
            return;
        }

        Span<Vector3> corners = stackalloc Vector3[MaxCorners * 2];

        int ring = _world.CellCorners(cell, corners);
        if (ring < 3)
        {
            _mesh.Mesh = null;
            return;
        }

        // The centre, to push each bar outward from.
        Vector3 centre = Vector3.Zero;
        for (int i = 0; i < ring * 2; i++)
            centre += corners[i];

        centre /= ring * 2f;

        float size = _world.NodeSize;
        float bar = size * Thickness;
        float lift = size * Expand;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        for (int n = 0; n < ring; n++)
        {
            int next = (n + 1) % ring;

            // The outer ring, the inner ring, and the upright joining them.
            AddBar(st, corners[n], corners[next], centre, bar, lift);
            AddBar(st, corners[ring + n], corners[ring + next], centre, bar, lift);
            AddBar(st, corners[n], corners[ring + n], centre, bar, lift);
        }

        _mesh.Mesh = st.Commit();
        _mesh.MaterialOverride = Material;
    }

    /// <summary>
    /// Outlines an organic node: every edge of every face.
    ///
    /// A Voronoi cell has no rings to walk, so the outline is built from the
    /// faces themselves -- each face's corners joined in a loop. Adjacent faces
    /// share an edge, so each one would be drawn twice; the second is skipped by
    /// remembering which pairs of endpoints have already been laid down, which
    /// halves the bars and stops the shared edges rendering at double
    /// thickness.
    /// </summary>
    private void RebuildPolyhedral(Vector3I cell, IPolyhedralGrid polyhedral)
    {
        int maxWalls = _world.Grid.MaxWalls;

        Span<Vector3I> walls = stackalloc Vector3I[maxWalls];
        Span<int> sides = stackalloc int[maxWalls];
        Span<Vector3> corners = stackalloc Vector3[polyhedral.MaxFaceCorners];

        int count = polyhedral.Faces(cell, walls, sides, corners);

        if (count == 0)
        {
            _mesh.Mesh = null;
            return;
        }

        Vector3 centre = _world.CellCentre(cell);

        float size = _world.NodeSize;
        float bar = size * Thickness;
        float lift = size * Expand;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        // Edges already drawn, as unordered pairs of endpoints. A cell has a
        // few dozen, so a plain list is quicker than anything with a hash.
        Span<Vector3> fromSeen = stackalloc Vector3[MaxEdges];
        Span<Vector3> toSeen = stackalloc Vector3[MaxEdges];
        int seen = 0;

        int at = 0;

        for (int n = 0; n < count; n++)
        {
            int span = sides[n];

            if (span < 3)
            {
                at += span;
                continue;
            }

            for (int c = 0; c < span; c++)
            {
                Vector3 from = corners[at + c];
                Vector3 to = corners[at + (c + 1) % span];

                if (Already(fromSeen, toSeen, seen, from, to))
                    continue;

                if (seen < MaxEdges)
                {
                    fromSeen[seen] = from;
                    toSeen[seen] = to;
                    seen++;
                }

                AddBar(st, from, to, centre, bar, lift);
            }

            at += span;
        }

        _mesh.Mesh = st.Commit();
        _mesh.MaterialOverride = Material;
    }

    /// <summary>Has this edge already been laid down, either way round?</summary>
    private static bool Already(ReadOnlySpan<Vector3> fromSeen, ReadOnlySpan<Vector3> toSeen,
        int count, Vector3 from, Vector3 to)
    {
        // Compared by POSITION with a tolerance, because two faces arrive at a
        // shared corner through different clipping arithmetic and the results
        // agree to a few decimal places rather than exactly.
        const float Tolerance = 0.0001f;

        for (int n = 0; n < count; n++)
        {
            bool forward = fromSeen[n].DistanceSquaredTo(from) < Tolerance
                && toSeen[n].DistanceSquaredTo(to) < Tolerance;

            bool backward = fromSeen[n].DistanceSquaredTo(to) < Tolerance
                && toSeen[n].DistanceSquaredTo(from) < Tolerance;

            if (forward || backward)
                return true;
        }

        return false;
    }

    /// <summary>Room for the edges of the busiest node a grid produces.</summary>
    private const int MaxEdges = 128;

    /// <summary>
    /// Room for the corners of the widest node any grid here produces.
    ///
    /// Both rings are written into one span, so this is half the space needed.
    /// </summary>
    private const int MaxCorners = 8;

    /// <summary>One edge, as a thin box pushed clear of the surface.</summary>
    private static void AddBar(SurfaceTool st, Vector3 from, Vector3 to,
        Vector3 centre, float bar, float lift)
    {
        Vector3 along = to - from;
        float span = along.Length();
        if (span < 0.000001f)
            return;

        along /= span;

        // Out from the block's centre, so the bar sits proud of both faces that
        // meet at this edge rather than sinking into either.
        Vector3 mid = (from + to) * 0.5f;
        Vector3 outward = mid - centre;

        // Only the part across the edge: a component along it would slide the
        // bar toward one end instead of lifting it.
        outward -= along * outward.Dot(along);

        float length = outward.Length();
        outward = length < 0.000001f ? Vector3.Up : outward / length;

        Vector3 side = along.Cross(outward).Normalized();

        Vector3 origin = mid + outward * lift;
        float half = bar * 0.5f;

        // Extended by half a bar at each end so the corners meet squarely
        // rather than leaving a notch.
        Box(st, origin, along * (span * 0.5f + half), outward * half, side * half);
    }

    /// <summary>A box from a centre and three half-extent vectors.</summary>
    private static void Box(SurfaceTool st, Vector3 origin, Vector3 x, Vector3 y, Vector3 z)
    {
        Span<Vector3> c = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            c[i] = origin
                + x * ((i & 1) == 0 ? -1f : 1f)
                + y * (((i >> 1) & 1) == 0 ? -1f : 1f)
                + z * (((i >> 2) & 1) == 0 ? -1f : 1f);
        }

        // Faces as corner indices, wound outward.
        ReadOnlySpan<int> quads = stackalloc int[]
        {
            1, 5, 7, 3,   4, 0, 2, 6,
            2, 3, 7, 6,   4, 5, 1, 0,
            4, 6, 7, 5,   0, 1, 3, 2,
        };

        for (int q = 0; q < quads.Length; q += 4)
        {
            Quad(st, c[quads[q]], c[quads[q + 1]], c[quads[q + 2]], c[quads[q + 3]]);
        }
    }

    private static void Quad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        st.AddVertex(a); st.AddVertex(b); st.AddVertex(c);
        st.AddVertex(a); st.AddVertex(c); st.AddVertex(d);
    }
}
