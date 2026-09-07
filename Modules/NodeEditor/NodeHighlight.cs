using Godot;
using System.Collections.Generic;
using GameBase.Nodes;

namespace GameBase.Player;

/// <summary>
/// Outlines the node the player is looking at.
///
/// The outline traces the node's REAL silhouette, not a cube around its cell.
/// A raw node is a bismuth crystal: its faces are recessed and its edge and
/// corner wins branch a quarter-cell out past the cell boundary, so a cube
/// outline sits away from the surface on the flats and cuts straight through
/// the rims. This walks the node's occupancy at quarter-cell resolution and
/// draws a bar along every SILHOUETTE edge -- each edge where the surface
/// folds -- which follows the branches out and the indents in.
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

    /// <summary>
    /// Warm off-white, the same colour the crosshair's outline uses, so the
    /// two read as one targeting system rather than two unrelated marks.
    /// </summary>
    [Export] public Color OutlineColor { get; set; } = new(1f, 0.922f, 0.761f);



    /// <summary>
    /// Bar thickness as a fraction of a node.
    ///
    /// Half the old cube outline's 0.05, and 0.025 is the floor: the stylized
    /// filter quantises to a 320-row grid, and at the far end of Reach a
    /// 0.025 bar spans 1.02 virtual pixels. Anything thinner drops below one
    /// pixel there and flickers in and out as the camera moves -- 0.018
    /// measured 0.73px. The filter's distance ramp does not rescue it, since
    /// it only begins past pixel_near_distance and quantises back to the base
    /// grid within Reach.
    ///
    /// Thin matters more here than it did for a cube: tracing folds draws
    /// about 3.5x a cube's line, so the same thickness would read as 3.5x the
    /// ink.
    /// </summary>
    [Export(PropertyHint.Range, "0.005,0.1,0.005")] public float Thickness { get; set; } = 0.025f;

    /// <summary>
    /// Extra clearance between a bar and the surface, as a fraction of a node,
    /// ON TOP of the minimum the bar's own width requires.
    ///
    /// A bar straddles a convex edge and is pushed out along the 45-degree
    /// diagonal, so to clear the corner underneath it the offset must exceed
    /// the bar's half-width measured along that diagonal -- (Thickness/2) *
    /// sqrt(2). Below that the node's corner protrudes THROUGH the bar and
    /// splits it lengthwise, which renders as a thin slot of node surface
    /// running down the middle of every outline. That minimum is computed and
    /// applied automatically; this is the margin above it.
    /// </summary>
    [Export(PropertyHint.Range, "0,0.05,0.002")] public float Expand { get; set; } = 0.004f;

    private Camera3D _camera;
    private NodeWorld _world;
    private CollisionObject3D _playerBody;
    private MeshInstance3D _mesh;
    private bool _visible;
    private Vector3I _cell;

    public override void _Ready()
    {
        _camera = !CameraPath.IsEmpty ? GetNodeOrNull<Camera3D>(CameraPath) : null;
        _camera ??= GameBase.Core.NodeSearch.FindByType<Camera3D>(GetTree().CurrentScene ?? GetParent());

        _world = !NodeWorldPath.IsEmpty ? GetNodeOrNull<NodeWorld>(NodeWorldPath) : null;
        _world ??= GameBase.Core.NodeSearch.FindByType<NodeWorld>(GetTree().CurrentScene ?? GetParent());

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
            Name = "HighlightMesh",
            // Built in world space, so the node's own transform must not move
            // it again.
            TopLevel = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_mesh);
        _mesh.Visible = false;
    }

    public override void _Process(double delta)
    {
        if (_camera == null || _world == null)
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

        // Rebuild only when the target actually changes. Tracing a silhouette
        // is more work than a cube was, and the player spends most frames
        // looking at the same node.
        if (!_visible || cell != _cell)
        {
            _cell = cell;
            _visible = true;
            Rebuild(cell);
            _mesh.Visible = true;
        }
    }

    /// <summary>The node under the crosshair, if one is in reach.</summary>
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
    /// Traces the node's silhouette.
    ///
    /// A silhouette edge is one where the surface folds: of the four
    /// quarter-cells around a lattice edge, some are filled and some are not.
    /// Walking those instead of the twelve edges of a cube is what makes
    /// branches and indents show up -- each rim that juts out contributes its
    /// own outline, and each recessed face the ring around its opening.
    /// </summary>
    private void Rebuild(Vector3I cell)
    {
        float size = _world.NodeSize;
        float quarter = size / _world.Subdivision;

        // The node's filled quarter-cells, as a set in node-local coordinates.
        int[] cells = _world.NodeSubCells(cell);
        var filled = new HashSet<Vector3I>(cells.Length / 3);
        for (int c = 0; c < cells.Length; c += 3)
            filled.Add(new Vector3I(cells[c], cells[c + 1], cells[c + 2]));

        Vector3 origin = _world.CellCentre(cell) - Vector3.One * (size * 0.5f);
        float bar = size * Thickness;

        // Clear the corner the bar straddles, then add the margin. See Expand:
        // anything less and the corner splits the bar down its length.
        float outward = bar * 0.5f * Mathf.Sqrt2 + size * Expand;

        // Each lattice edge is shared by up to four quarter-cells, so it would
        // be visited up to four times; this collects one bar per edge.
        var seen = new HashSet<(int, Vector3I)>();
        var bars = new List<(Vector3I Edge, int Axis, Vector3 Normal)>();

        foreach (Vector3I f in filled)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                int uAxis = axis == 0 ? 1 : 0;
                int vAxis = axis == 2 ? 1 : 2;

                // The four lattice edges of this quarter-cell running along
                // `axis`, named by their low end.
                for (int du = 0; du <= 1; du++)
                {
                    for (int dv = 0; dv <= 1; dv++)
                    {
                        Vector3I edge = f;
                        edge[uAxis] += du;
                        edge[vAxis] += dv;

                        if (!seen.Add((axis, edge)))
                            continue;

                        if (!IsSilhouette(filled, edge, uAxis, vAxis, out Vector3 normal))
                            continue;

                        bars.Add((edge, axis, normal));
                    }
                }
            }
        }

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        foreach ((Vector3I edge, int axis, Vector3 normal, int length) in MergeRuns(bars))
        {
            if (_world.Grid != null)
                AddEdgeBarOnGrid(st, cell, edge, axis, normal, bar, outward, length);
            else
                AddEdgeBar(st, origin, quarter, edge, axis, normal, bar, outward, length);
        }

        _mesh.Mesh = st.Commit();
        _mesh.MaterialOverride = Material;
    }

    /// <summary>
    /// Joins collinear runs of bars into single long boxes.
    ///
    /// Bars are found one quarter-cell at a time, so a straight edge four
    /// quarter-cells long arrives as four separate boxes, each overlapping the
    /// next by half a bar to close the joint. Along a straight run that
    /// overlap is not a joint at all -- it is two boxes interpenetrating, and
    /// it shows in the render as a doubled line beside the bar. Merging
    /// removes the seam and about 56% of the boxes with it.
    ///
    /// A run is bars sharing an axis, a fold direction, and a line through
    /// space, at consecutive positions along that axis.
    /// </summary>
    private static List<(Vector3I Edge, int Axis, Vector3 Normal, int Length)> MergeRuns(
        List<(Vector3I Edge, int Axis, Vector3 Normal)> bars)
    {
        // Group by the line the run lies on: the axis, the two coordinates
        // that do not vary along it, and the direction the fold faces.
        var lines = new Dictionary<(int, int, int, int), List<int>>();

        foreach ((Vector3I edge, int axis, Vector3 normal) in bars)
        {
            int uAxis = axis == 0 ? 1 : 0;
            int vAxis = axis == 2 ? 1 : 2;

            // The normal is one of a handful of diagonal directions, so it
            // quantises to an exact key rather than needing a tolerance.
            int normalKey = (int)Mathf.Round(normal.X * 2f) * 25
                + (int)Mathf.Round(normal.Y * 2f) * 5
                + (int)Mathf.Round(normal.Z * 2f);

            var key = (axis, edge[uAxis], edge[vAxis], normalKey);
            if (!lines.TryGetValue(key, out List<int> positions))
                lines[key] = positions = new List<int>();
            positions.Add(edge[axis]);
        }

        var merged = new List<(Vector3I, int, Vector3, int)>(bars.Count);
        var byKey = new Dictionary<(int, int, int, int), (Vector3I Edge, Vector3 Normal)>();
        foreach ((Vector3I edge, int axis, Vector3 normal) in bars)
        {
            int uAxis = axis == 0 ? 1 : 0;
            int vAxis = axis == 2 ? 1 : 2;
            int normalKey = (int)Mathf.Round(normal.X * 2f) * 25
                + (int)Mathf.Round(normal.Y * 2f) * 5
                + (int)Mathf.Round(normal.Z * 2f);
            byKey[(axis, edge[uAxis], edge[vAxis], normalKey)] = (edge, normal);
        }

        foreach (var pair in lines)
        {
            List<int> positions = pair.Value;
            positions.Sort();

            (Vector3I sample, Vector3 normal) = byKey[pair.Key];
            int axis = pair.Key.Item1;

            int runStart = positions[0];
            int runLength = 1;

            for (int i = 1; i <= positions.Count; i++)
            {
                // Extend while the next bar is the immediate neighbour.
                if (i < positions.Count && positions[i] == positions[i - 1] + 1)
                {
                    runLength++;
                    continue;
                }

                Vector3I edge = sample;
                edge[axis] = runStart;
                merged.Add((edge, axis, normal, runLength));

                if (i < positions.Count)
                {
                    runStart = positions[i];
                    runLength = 1;
                }
            }
        }

        return merged;
    }

    /// <summary>
    /// Is this lattice edge on the silhouette, and if so which way does the
    /// fold face?
    ///
    /// The four quarter-cells around the edge are read as a 2x2 pattern. All
    /// four filled is interior; none filled is empty space. Anything between
    /// is a fold, and the outward direction is away from the filled ones --
    /// which is what pushes the bar clear of the surface rather than into it.
    /// </summary>
    private static bool IsSilhouette(HashSet<Vector3I> filled, Vector3I edge,
        int uAxis, int vAxis, out Vector3 normal)
    {
        normal = Vector3.Zero;

        int count = 0;
        var away = Vector3.Zero;

        for (int du = -1; du <= 0; du++)
        {
            for (int dv = -1; dv <= 0; dv++)
            {
                Vector3I probe = edge;
                probe[uAxis] += du;
                probe[vAxis] += dv;

                if (!filled.Contains(probe))
                    continue;

                count++;

                // Direction from this quarter-cell toward the edge. Summed
                // over the filled cells, it points away from solid.
                var step = Vector3.Zero;
                step[uAxis] = du == 0 ? -1f : 1f;
                step[vAxis] = dv == 0 ? -1f : 1f;
                away += step;
            }
        }

        // Only ONE filled: a CONVEX fold, where the surface turns outward.
        //
        // The other cases and why each is skipped:
        //   0, 4        interior or empty -- no surface here at all.
        //   2 adjacent  the filled/empty boundary is a straight plane through
        //               the edge, so the surface is FLAT and a bar would be an
        //               interior line ruled across a face. These alone were
        //               272 of 490 bars on a typical node.
        //   2 diagonal  a pinch: the surface touches itself and there is no
        //               single outward direction to offset along.
        //   3           a CONCAVE fold -- the inside corner of an indent.
        //               Geometrically real, but it is a crevice, and a bar
        //               laid in one is enclosed by surface on both sides, so
        //               the Expand offset has nowhere to go and the bar clips
        //               through the walls instead of tracing them.
        //
        // Convex only also matches what an outline is FOR: the node's outer
        // form. The indents are interior detail, and drawing them read as
        // clutter inside the silhouette.
        if (count != 1)
            return false;

        normal = away.Normalized();
        return true;
    }

    /// <summary>
    /// One bar spanning `length` quarter-cells along a lattice edge, offset
    /// clear of the surface.
    /// </summary>
    private static void AddEdgeBar(SurfaceTool st, Vector3 origin, float quarter,
        Vector3I edge, int axis, Vector3 normal, float bar, float outward, int length)
    {
        Vector3 low = origin + new Vector3(edge.X, edge.Y, edge.Z) * quarter;
        Vector3 high = low;
        high[axis] += quarter * length;

        Vector3 centre = (low + high) * 0.5f + normal * outward;

        var extent = Vector3.One * (bar * 0.5f);
        // Half a bar of overlap at each END of the run, so bars meeting at a
        // corner close instead of leaving a notch. Within a run there is no
        // joint to close, which is why runs are merged first.
        extent[axis] = quarter * length * 0.5f + bar * 0.5f;

        AddBox(st, centre, extent);
    }

    /// <summary>
    /// One bar of the outline, placed through the grid.
    ///
    /// The flat path builds the bar from a corner plus sub-cell offsets, which
    /// on a cubed sphere describes a box floating in space near the node
    /// rather than tracing it: the node is an ARC, and its edges curve. This
    /// asks the grid where each end of the run actually is, so the outline sits
    /// on the surface it is outlining.
    ///
    /// The bar is still a straight box between those two points. Over a run of
    /// at most four quarter-cells the arc's deviation from a chord is far
    /// smaller than the bar's own thickness, so bending it further would not
    /// be visible.
    /// </summary>
    private void AddEdgeBarOnGrid(SurfaceTool st, Vector3I cell,
        Vector3I edge, int axis, Vector3 normal, float bar, float outward, int length)
    {
        int sub = _world.Subdivision;

        var lowLocal = new Vector3(edge.X, edge.Y, edge.Z) / sub;
        Vector3 highLocal = lowLocal;
        highLocal[axis] += (float)length / sub;

        Vector3 low = _world.Grid.PointIn(cell, lowLocal);
        Vector3 high = _world.Grid.PointIn(cell, highLocal);

        // The fold direction has to be carried onto the sphere as well, or the
        // bar is held clear of the surface in a world direction that no longer
        // points away from it.
        Vector3 nudged = _world.Grid.PointIn(cell, lowLocal + normal * (1f / sub));
        Vector3 outwardDirection = (nudged - low);
        outwardDirection = outwardDirection.LengthSquared() > 0.000001f
            ? outwardDirection.Normalized()
            : _world.Grid.UpAt(cell);

        Vector3 centre = (low + high) * 0.5f + outwardDirection * outward;

        Vector3 along = high - low;
        float span = along.Length();
        if (span < 0.000001f)
        {
            AddBox(st, centre, Vector3.One * (bar * 0.5f));
            return;
        }

        AddOrientedBox(st, centre, along / span, outwardDirection,
            span * 0.5f + bar * 0.5f, bar * 0.5f);
    }

    /// <summary>
    /// A box aligned to an arbitrary direction rather than the world axes.
    ///
    /// Needed because a bar on the sphere runs along the surface, which is not
    /// an axis anywhere but the six face centres.
    /// </summary>
    private static void AddOrientedBox(SurfaceTool st, Vector3 centre,
        Vector3 along, Vector3 up, float halfLength, float halfThick)
    {
        Vector3 side = along.Cross(up);
        if (side.LengthSquared() < 0.000001f)
            side = along.Cross(Vector3.Up);

        side = side.Normalized();
        Vector3 fold = side.Cross(along).Normalized();

        Vector3 a = along * halfLength;
        Vector3 b = side * halfThick;
        Vector3 c = fold * halfThick;

        var corners = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            corners[i] = centre
                + a * ((i & 1) != 0 ? 1f : -1f)
                + b * ((i & 2) != 0 ? 1f : -1f)
                + c * ((i & 4) != 0 ? 1f : -1f);
        }

        // The six faces, wound outward. Indices follow the bit pattern above.
        AddQuad(st, corners[1], corners[3], corners[7], corners[5]);
        AddQuad(st, corners[0], corners[4], corners[6], corners[2]);
        AddQuad(st, corners[2], corners[6], corners[7], corners[3]);
        AddQuad(st, corners[0], corners[1], corners[5], corners[4]);
        AddQuad(st, corners[4], corners[5], corners[7], corners[6]);
        AddQuad(st, corners[0], corners[2], corners[3], corners[1]);
    }

    private static void AddQuad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        st.AddVertex(a); st.AddVertex(b); st.AddVertex(c);
        st.AddVertex(a); st.AddVertex(c); st.AddVertex(d);
    }

    private StandardMaterial3D _material;

    /// <summary>
    /// One shared material. Unshaded so the outline keeps its exact colour
    /// under any lighting.
    ///
    /// Depth testing stays ON. Turning it off pushes the mesh into a later
    /// draw pass, after the stylized filter has already sampled the screen --
    /// the filter then paints its full-screen quad over the top and the
    /// outline never appears. Keeping depth testing means the bars have to be
    /// held clear of the surface instead, which `Expand` does.
    /// </summary>
    private StandardMaterial3D Material => _material ??= new StandardMaterial3D
    {
        AlbedoColor = OutlineColor,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
    };


    /// <summary>One axis-aligned box, as twelve triangles.</summary>
    private static void AddBox(SurfaceTool st, Vector3 centre, Vector3 extent)
    {
        Vector3 min = centre - extent;
        Vector3 max = centre + extent;

        Vector3[] c =
        {
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z),
            new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z),
            new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z),
        };

        // Wound clockwise, matching the convention the node mesher uses.
        int[] faces =
        {
            0, 2, 1, 0, 3, 2,   // -Z
            4, 5, 6, 4, 6, 7,   // +Z
            0, 1, 5, 0, 5, 4,   // -Y
            3, 7, 6, 3, 6, 2,   // +Y
            0, 4, 7, 0, 7, 3,   // -X
            1, 2, 6, 1, 6, 5,   // +X
        };

        foreach (int i in faces)
            st.AddVertex(c[i]);
    }
}
