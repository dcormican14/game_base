using Godot;
using GameBase.Nodes;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Audits what the mesher actually emitted, against what should be visible.
///
/// The symptom this exists for is "nested spheres": faces drawn on the INSIDE
/// of the planet, between two solid cells, where nothing can ever be seen. Each
/// buried shell that gets drawn is another sphere nested inside the last, and
/// mining a hole reveals them.
///
/// A face between two solid cells is the definition of waste, so the audit is
/// simple: for every emitted quad, find the cell it belongs to and the cell it
/// faces into, and ask whether BOTH are solid. Any quad where they are is one
/// that should never have been built.
/// </summary>
public partial class MeshAudit : Node
{
    [Export] public int WaitFrames { get; set; } = 400;

    /// <summary>Dig a hole first, so the audit also covers the faces an edit
    /// exposes rather than only the ones generation produced.</summary>
    [Export] public bool DigFirst { get; set; } = true;

    /// <summary>Save a picture of the result. Off by default: capturing the
    /// viewport needs a real window, so a headless run must not try.</summary>
    [Export] public bool Screenshot { get; set; }

    private NodeWorld _world;
    private Node3D _player;
    private ChunkStreamer _streamer;
    private int _frames;
    private bool _audited;
    private int _after;

    [Export] public bool SingleThread { get; set; }

    public override void _Ready()
    {
        GameBase.Nodes.NodeWorld.ForceSingleThread = SingleThread;
        Node scene = GetTree().CurrentScene;
        _world = NodeSearch.FindByType<NodeWorld>(scene);
        _player = scene?.GetNodeOrNull<Node3D>("Player");
        _streamer = NodeSearch.FindByType<ChunkStreamer>(scene);
    }

    public override void _Process(double delta)
    {
        _frames++;
        if (_frames < WaitFrames || _streamer is { IsReady: false })
            return;

        if (!_audited)
        {
            _audited = true;
            Audit();

            if (!Screenshot)
            {
                GetTree().Quit(0);
                return;
            }

            // Look down into the hole that was just dug.
            var pivot = GetTree().CurrentScene?.GetNodeOrNull<Node3D>("Player/CameraPivot");
            if (pivot != null)
                pivot.Rotation = new Vector3(Mathf.DegToRad(-50f), 0f, 0f);

            return;
        }

        if (++_after < 20)
            return;

        Image image = GetViewport().GetTexture().GetImage();
        image.SavePng("user://audit.png");
        GD.Print("MESHAUDIT: wrote user://audit.png");

        GetTree().Quit(0);
    }

    private void Audit()
    {
        INodeGrid grid = _world?.Grid;
        if (grid == null || _player == null)
        {
            GD.Print("MESHAUDIT: no world or player");
            return;
        }

        GD.Print("=== MESH AUDIT ===");
        GD.Print($"surface={grid.SurfaceRadius} shells={grid.ShellCount}" +
            $" maxWalls={grid.MaxWalls}");

        if (DigFirst)
        {
            Vector3 outward = _player.GlobalPosition.Normalized();
            Vector3I at = grid.CellAt(outward * (grid.SurfaceRadius - 0.5f));

            // A WIDE pit, not a one-block shaft. A shaft's near walls face
            // away from any camera looking into it, so it cannot show whether
            // walls are drawn; a 5x5 pit puts walls squarely in view.
            int dug = 0;
            for (int du = -2; du <= 2; du++)
                for (int dv = -2; dv <= 2; dv++)
                {
                    if (!Step(grid, at, du, dv, out Vector3I top))
                        continue;

                    Vector3I column = top;
                    for (int depth = 0; depth < 4; depth++)
                    {
                        if (_world.HasNode(column))
                        {
                            _world.RemoveNode(column);
                            dug++;
                        }

                        if (!grid.RadialNeighbour(column, -1, out column))
                            break;
                    }
                }

            GD.Print($"dug {dug} blocks straight down");

        }

        // Walk every emitted vertex, classify the cell it sits in, and ask what
        // lies just beyond its own normal.
        int total = 0, buried = 0, outsideGrid = 0, onSurface = 0;
        float deepest = float.MaxValue, shallowest = 0f;

        var perShell = new System.Collections.Generic.SortedDictionary<int, int>();
        var buriedPerShell = new System.Collections.Generic.SortedDictionary<int, int>();

        Walk(_world, grid, ref total, ref buried, ref outsideGrid, ref onSurface,
            ref deepest, ref shallowest, perShell, buriedPerShell);

        GD.Print($"-- {total} sampled quads --");
        GD.Print($"  radius range {deepest:F1} .. {shallowest:F1}");
        GD.Print($"  quads whose own cell is outside the grid: {outsideGrid}");
        GD.Print($"  quads facing open air (legitimate):       {onSurface}");
        GD.Print($"  quads BURIED between two solid cells:     {buried}");

        GD.Print("-- quads by shell (buried / total) --");
        foreach (var kv in perShell)
        {
            buriedPerShell.TryGetValue(kv.Key, out int b);
            if (kv.Value > 0)
                GD.Print($"    shell {kv.Key,4}: {b,6} / {kv.Value,6}");
        }

        // WHERE do the buried quads sit inside their section? If they cluster
        // on section boundaries, the occupancy window is the culprit; if they
        // are spread evenly, the shape rule is.
        int onSectionEdge = 0, inSectionInterior = 0;
        EdgeSplit(_world, grid, ref onSectionEdge, ref inSectionInterior);

        GD.Print("-- buried quads by position within their section --");
        GD.Print($"  on a section boundary cell: {onSectionEdge}");
        GD.Print($"  in the section interior:    {inSectionInterior}");

        // WHICH WAY do the buried quads face? A quad buried while facing
        // radially is an interior shell surface -- the nested spheres. One
        // buried while facing sideways is a wall between two solid columns.
        int radial = 0, lateral = 0;
        int deepestBuried = -1, shallowestBuried = int.MaxValue;
        Facing(_world, grid, ref radial, ref lateral,
            ref deepestBuried, ref shallowestBuried);

        GD.Print("-- buried quads by orientation --");
        GD.Print($"  facing radially (nested shells): {radial}");
        GD.Print($"  facing sideways:                 {lateral}");
        GD.Print($"  buried quads span shells {shallowestBuried}..{deepestBuried}");

        // DOES THE WINDING AGREE WITH THE NORMAL?
        //
        // Godot treats a triangle as front-facing when it winds CLOCKWISE as
        // seen from the front. So for the first triangle of a quad, the vector
        // (v2-v0) x (v1-v0) must point the same way as the quad's normal. A
        // quad where it points the other way exists, has a correct normal, and
        // is still invisible -- which is what looking through a wall means.
        int agree = 0, disagree = 0;
        Winding(_world, ref agree, ref disagree);

        GD.Print("-- triangle winding vs normal --");
        GD.Print($"  quads wound to match their normal:  {agree}");
        GD.Print($"  quads wound INSIDE OUT:             {disagree}");

        // The check against an outside reference. See Outward: the winding
        // count above agrees with itself by construction and stayed at a clean
        // 100% while every face in the world was built back-to-front.
        int correct = 0, inverted = 0;
        Outward(_world, grid, ref correct, ref inverted);

        GD.Print("-- radial faces vs the outward direction --");
        GD.Print($"  facing AWAY from the core (visible): {correct}");
        GD.Print($"  facing INTO the core (invisible):    {inverted}");

        GD.Print(buried == 0
            ? "VERDICT: no buried faces -- nothing is drawn inside the planet"
            : $"VERDICT: {buried} buried faces are being drawn ({100f * buried / Mathf.Max(1, total):F1}% of sampled quads)");

        GD.Print(inverted == 0
            ? "VERDICT: every radial face points outward -- the surface is visible from above"
            : $"VERDICT: {inverted} radial faces point INTO the core -- the ground is "
                + "invisible from outside and the far side shows through it");

        GD.Print("=== END MESH AUDIT ===");
    }

    /// <summary>
    /// Walks a few nodes sideways, on whatever grid this is.
    ///
    /// The audit wants a patch of ground around a point, and the two grids
    /// disagree about what a lateral step even is -- a cubed sphere has (u, v)
    /// axes, an icosphere has six ring steps and no axes at all. Walking the
    /// walls is the one motion both understand.
    /// </summary>
    private static bool Step(INodeGrid grid, Vector3I from, int du, int dv,
        out Vector3I result)
    {
        result = from;

        int walls = grid.WallCount(from);
        if (walls == 0)
            return false;

        // The two counts are treated as a number of steps around two different
        // walls, which traces out a patch rather than a line.
        if (!Walk(grid, ref result, 0, du))
            return false;

        return Walk(grid, ref result, walls > 2 ? walls / 3 : 1, dv);
    }

    /// <summary>Takes `count` steps through one wall index, either way.</summary>
    private static bool Walk(INodeGrid grid, ref Vector3I at, int wall, int count)
    {
        int walls = grid.WallCount(at);
        if (walls == 0)
            return false;

        // Negative counts walk out of the opposite wall.
        int direction = count >= 0 ? wall : (wall + walls / 2) % walls;

        for (int n = 0; n < Mathf.Abs(count); n++)
        {
            int here = grid.WallCount(at);
            if (here == 0)
                return false;

            if (!grid.WallNeighbour(at, direction % here, out Vector3I next))
                return false;

            at = grid.Canonical(next);
        }

        return true;
    }

    /// <summary>Vertices within a radius of a point.</summary>
    private static int CountNear(Node from, Vector3 at, float within)
    {
        int total = 0;

        if (from is MeshInstance3D mi && mi.Mesh != null)
        {
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                foreach (Vector3 v in mi.Mesh.SurfaceGetArrays(s)
                    [(int)Mesh.ArrayType.Vertex].AsVector3Array())
                {
                    if ((mi.GlobalTransform * v).DistanceTo(at) <= within)
                        total++;
                }
            }
        }

        foreach (Node child in from.GetChildren())
            total += CountNear(child, at, within);

        return total;
    }

    /// <summary>
    /// Checks each triangle's winding against its own normal.
    /// </summary>
    /// <remarks>
    /// NOT SUFFICIENT ON ITS OWN, and it was the only check here while the
    /// whole planet rendered inside out. AddFace derives the stored normal with
    /// the very same expression this test recomputes, so the two agree by
    /// construction whichever way the corner table is wound -- reversing a face
    /// flips the normal and the winding together and this count never moves.
    /// What it catches is a normal that came from somewhere else than the
    /// corners; what it cannot catch is the corners being wound backwards.
    ///
    /// <see cref="Outward"/> is the test with an outside reference, and the one
    /// that fails when a face is built back-to-front.
    /// </remarks>
    private static void Winding(Node from, ref int agree, ref int disagree)
    {
        if (from is MeshInstance3D mi && mi.Mesh != null)
        {
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                var arrays = mi.Mesh.SurfaceGetArrays(s);
                var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var norms = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                var idx = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();

                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    Vector3 a = verts[idx[t]];
                    Vector3 b = verts[idx[t + 1]];
                    Vector3 c = verts[idx[t + 2]];
                    Vector3 n = norms[idx[t]];

                    // Godot: front-facing is clockwise from the front, i.e.
                    // (c-a) x (b-a) points along the normal.
                    Vector3 face = (c - a).Cross(b - a);

                    if (face.Dot(n) >= 0f)
                        agree++;
                    else
                        disagree++;
                }
            }
        }

        foreach (Node child in from.GetChildren())
            Winding(child, ref agree, ref disagree);
    }

    /// <summary>
    /// Do the faces point AWAY from the planet's centre?
    ///
    /// The check with an outside reference. Every other test here compares the
    /// mesh against itself and so cannot tell a correctly built world from one
    /// built entirely back-to-front; this one asks whether a quad's normal
    /// agrees with the direction that quad is displaced from the core, which is
    /// a fact about the planet rather than about the mesh.
    ///
    /// Only quads on an OUTWARD-facing surface are counted. A block's side and
    /// underside faces are legitimately tangential or inward, so including them
    /// would drown the signal; a surface quad's normal should sit within a few
    /// degrees of straight up.
    /// </summary>
    private void Outward(Node from, INodeGrid grid, ref int correct, ref int inverted)
    {
        if (from is MeshInstance3D mi && mi.Mesh != null)
        {
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                var arrays = mi.Mesh.SurfaceGetArrays(s);
                var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var norms = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();

                int n = Mathf.Min(verts.Length, norms.Length);
                for (int i = 0; i < n; i += 4)
                {
                    Vector3 at = mi.GlobalTransform * verts[i];
                    if (at.LengthSquared() < 0.0001f)
                        continue;

                    Vector3 up = at.Normalized();
                    float alignment = norms[i].Normalized().Dot(up);

                    // Radial faces only: a side face is tangential and says
                    // nothing about which way the surface was built.
                    if (Mathf.Abs(alignment) < 0.8f)
                        continue;

                    if (alignment > 0f)
                        correct++;
                    else
                        inverted++;
                }
            }
        }

        foreach (Node child in from.GetChildren())
            Outward(child, grid, ref correct, ref inverted);
    }

    /// <summary>Splits buried quads by whether they face radially or sideways.</summary>
    private void Facing(Node from, INodeGrid grid, ref int radial, ref int lateral,
        ref int deepest, ref int shallowest)
    {
        if (from is MeshInstance3D mi && mi.Mesh != null)
        {
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                var arrays = mi.Mesh.SurfaceGetArrays(s);
                var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var norms = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();

                int n = Mathf.Min(verts.Length, norms.Length);
                for (int i = 0; i < n; i += 4)
                {
                    Vector3 at = mi.GlobalTransform * verts[i];
                    Vector3 normal = norms[i].Normalized();
                    float step = grid.NodeSize * 0.25f;

                    Vector3I owner = grid.CellAt(at - normal * step);
                    Vector3I facing = grid.CellAt(at + normal * step);

                    if (!grid.Contains(owner) || !_world.HasNode(owner))
                        continue;
                    if (!grid.Contains(facing) || !_world.HasNode(facing))
                        continue;

                    // Shell is the packed address's Z on every grid, so it
                    // needs no unpacking the layouts disagree about.
                    int shell = owner.Z;
                    if (shell > deepest) deepest = shell;
                    if (shell < shallowest) shallowest = shell;

                    // Radial if the normal lines up with the outward direction.
                    Vector3 up = at.Normalized();
                    if (Mathf.Abs(normal.Dot(up)) > 0.7f)
                        radial++;
                    else
                        lateral++;
                }
            }
        }

        foreach (Node child in from.GetChildren())
            Facing(child, grid, ref radial, ref lateral, ref deepest, ref shallowest);
    }

    /// <summary>Splits buried quads by whether their cell sits on a section
    /// boundary (where the occupancy window ends) or inside one.</summary>
    private void EdgeSplit(Node from, INodeGrid grid, ref int edge, ref int interior)
    {
        const int Section = 8;

        if (from is MeshInstance3D mi && mi.Mesh != null)
        {
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                var arrays = mi.Mesh.SurfaceGetArrays(s);
                var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var norms = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();

                int n = Mathf.Min(verts.Length, norms.Length);
                for (int i = 0; i < n; i += 4)
                {
                    Vector3 at = mi.GlobalTransform * verts[i];
                    Vector3 normal = norms[i].Normalized();
                    float step = grid.NodeSize * 0.25f;

                    Vector3I owner = grid.CellAt(at - normal * step);
                    Vector3I facing = grid.CellAt(at + normal * step);

                    if (!grid.Contains(owner) || !_world.HasNode(owner))
                        continue;
                    if (!grid.Contains(facing) || !_world.HasNode(facing))
                        continue;

                    // Buried. Where does it sit in its section?
                    int lx = Mod(owner.X, Section);
                    int ly = Mod(owner.Y, Section);
                    int lz = Mod(owner.Z, Section);

                    bool onEdge = lx == 0 || lx == Section - 1
                        || ly == 0 || ly == Section - 1
                        || lz == 0 || lz == Section - 1;

                    if (onEdge)
                        edge++;
                    else
                        interior++;
                }
            }
        }

        foreach (Node child in from.GetChildren())
            EdgeSplit(child, grid, ref edge, ref interior);
    }

    private static int Mod(int value, int m)
    {
        int r = value % m;
        return r < 0 ? r + m : r;
    }

    /// <summary>
    /// Classifies each quad by sampling one vertex per quad (they share a
    /// normal) and stepping a little along that normal to find the cell the
    /// quad faces into.
    /// </summary>
    /// <remarks>
    /// THE BURIED COUNT IS NOT RELIABLE ON AN ORGANIC WORLD. The step is taken
    /// from a VERTEX, which works where a face sits squarely between two cell
    /// centres -- true on both sphere grids. A Voronoi wall is perpendicular to
    /// the line between its two sites but need not be centred on it, so a step
    /// from one of its corners can land in a third cell entirely and the quad
    /// is called buried when it is not.
    ///
    /// Measured: this reports about 3% buried on the organic planet, while
    /// asking the grid directly -- OrganicCheck's mutual-neighbour test --
    /// finds every one of 6185 walls agreed on by both nodes, which is the
    /// property that would actually have to fail for a face to be buried. Read
    /// the grid's own check for that world, and this one for the others.
    /// </remarks>
    private void Walk(Node from, INodeGrid grid,
        ref int total, ref int buried, ref int outsideGrid, ref int onSurface,
        ref float deepest, ref float shallowest,
        System.Collections.Generic.SortedDictionary<int, int> perShell,
        System.Collections.Generic.SortedDictionary<int, int> buriedPerShell)
    {
        if (from is MeshInstance3D mi && mi.Mesh != null)
        {
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                var arrays = mi.Mesh.SurfaceGetArrays(s);
                var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var norms = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();

                int n = Mathf.Min(verts.Length, norms.Length);

                // One sample per quad: vertices arrive four to a quad and share
                // a normal, so testing every one is four times the work for the
                // same answer.
                for (int i = 0; i < n; i += 4)
                {
                    Vector3 at = mi.GlobalTransform * verts[i];
                    Vector3 normal = norms[i].Normalized();

                    float radius = at.Length();
                    if (radius < deepest) deepest = radius;
                    if (radius > shallowest) shallowest = radius;

                    total++;

                    // Step a quarter of a node each way along the normal: the
                    // cell the quad belongs to, and the cell it faces into.
                    float step = grid.NodeSize * 0.25f;
                    Vector3I owner = grid.CellAt(at - normal * step);
                    Vector3I facing = grid.CellAt(at + normal * step);

                    int shell = owner.Z;
                    perShell.TryGetValue(shell, out int c);
                    perShell[shell] = c + 1;

                    if (!grid.Contains(owner))
                    {
                        outsideGrid++;
                        continue;
                    }

                    // The quad faces into something solid AND sits on something
                    // solid: nothing can see it.
                    bool ownerSolid = _world.HasNode(owner);
                    bool facingSolid = grid.Contains(facing) && _world.HasNode(facing);

                    if (ownerSolid && facingSolid)
                    {
                        buried++;
                        buriedPerShell.TryGetValue(shell, out int b);
                        buriedPerShell[shell] = b + 1;
                    }
                    else
                    {
                        onSurface++;
                    }
                }
            }
        }

        foreach (Node child in from.GetChildren())
            Walk(child, grid, ref total, ref buried, ref outsideGrid, ref onSurface,
                ref deepest, ref shallowest, perShell, buriedPerShell);
    }
}
