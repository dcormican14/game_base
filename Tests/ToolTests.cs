using Godot;
using System.Threading.Tasks;
using GameBase.Nodes;
using GameBase.Tools;
using GameBase.Tools.Highlight;

namespace GameBase.Tests;

/// <summary>The tools, against a real world with real collision.</summary>
public sealed class ToolTests : TestSuite
{
    private const float Surface = 10.3f;

    private static readonly Vector3 Eye = new(0.3f, Surface + 3f, 0.2f);

    private static ToolContext Looking(NodeWorld world, Vector3 eye, Vector3 aim,
        IMaterialLedger ledger = null, float delta = 0.25f, ShovelMode mode = ShovelMode.Raise,
        ToolStroke stroke = null) =>
        new(world, eye, aim, ledger, null, delta, 0f, stroke, (int)mode);

    /// <summary>The ground's height under a spot, read from the fills.</summary>
    private static float Height(NodeWorld world, Vector3 spot) =>
        ParticleField.GroundAt(world, new Vector3(spot.X, Surface, spot.Z), Vector3.Up, 30f, out Vector3 ground)
            ? ground.Y
            : float.NaN;

    private async Task<NodeWorld> Bed()
    {
        NodeWorld world = FlatBed.Build(Root, Surface);
        await PhysicsFrames();
        return world;
    }

    /// <summary>
    /// Holds the shovel's left button on a spot for some steps, looking straight
    /// down at it, re-meshing between steps so it aims at the ground as it now
    /// is. Returns how many steps changed the world.
    /// </summary>
    private async Task<int> Shape(NodeWorld world, ShovelTool shovel, ShovelMode mode, Vector3 spot,
        int steps, ToolStroke stroke = null, IMaterialLedger ledger = null, float delta = 0.1f)
    {
        stroke ??= new ToolStroke();
        int changed = 0;

        for (int step = 0; step < steps; step++)
        {
            float top = Height(world, spot);
            var eye = new Vector3(spot.X, (float.IsNaN(top) ? Surface : top) + 3f, spot.Z);
            ToolContext context = Looking(world, eye, Vector3.Down, ledger, delta, mode, stroke);

            if (!shovel.TryTarget(context, out NodeHit target))
                break;

            if (shovel.Mine(context, target))
                changed++;

            world.MeshAllNow();
            await PhysicsFrames(1);
        }

        return changed;
    }

    private static readonly Vector3 Spot = new(Eye.X, Surface, Eye.Z);

    /// <summary>Holding the left button raises or lowers the ground; the right does nothing.</summary>
    [Test]
    public async Task ShovelRaisesAndLowers()
    {
        NodeWorld world = await Bed();
        var shovel = new ShovelTool();
        float before = Height(world, Spot);

        ToolContext context = Looking(world, Eye, Vector3.Down);
        Check(shovel.TryTarget(context, out NodeHit target), "no target");
        Check(!shovel.Place(context, target), "the right button changed the ground");

        await Shape(world, shovel, ShovelMode.Raise, Spot, 10);
        float raised = Height(world, Spot);
        Check(raised > before + 1f, $"raising only lifted the ground from {before:0.00} to {raised:0.00}");

        await Shape(world, shovel, ShovelMode.Lower, Spot, 20);
        float lowered = Height(world, Spot);
        Check(lowered < before - 1f, $"lowering only sank the ground from {before:0.00} to {lowered:0.00}");
    }

    /// <summary>
    /// Nothing outside the brush moves, however long the button is held.
    /// "Outside" is a lattice step past the outer circle, because the fills
    /// are a node apart and the ground between one inside the circle and one
    /// outside it slopes across the gap.
    /// </summary>
    [Test]
    public async Task RaisingStaysInsideTheBrush()
    {
        NodeWorld world = await Bed();
        var shovel = new ShovelTool();
        Vector3 outside = Spot + Vector3.Right * (shovel.OuterRadius + world.NodeSize);
        float outsideBefore = Height(world, outside);

        await Shape(world, shovel, ShovelMode.Raise, Spot, 30);

        Check(Height(world, Spot) > Surface + 1f, "a held raise barely lifted the ground");
        Near(Height(world, outside), outsideBefore, 0.05f, "ground just outside the brush");
    }

    /// <summary>The drawn ground's height under a spot: what the player sees.</summary>
    private static float DrawnHeight(NodeWorld world, Vector3 spot) =>
        world.Raycast(new Vector3(spot.X, Surface + 40f, spot.Z), new Vector3(spot.X, Surface - 40f, spot.Z),
            out NodeHit hit) ? hit.Point.Y : float.NaN;

    /// <summary>
    /// Held, a raise grows a flat-topped mound SMOOTHLY -- the drawn ground
    /// never jumps from one frame to the next -- and stops once its sides
    /// reach 45 degrees, about a node high. Widening the base lets it grow on.
    /// </summary>
    [Test]
    public async Task RaisingGrowsSmoothlyAndStops()
    {
        NodeWorld world = await Bed();
        var shovel = new ShovelTool();
        float node = world.NodeSize;
        var stroke = new ToolStroke();
        float last = DrawnHeight(world, Spot), biggest = 0f;

        // Four seconds at sixty frames a second, looking straight down.
        for (int frame = 0; frame < 240; frame++)
        {
            var eye = new Vector3(Spot.X, last + 3f, Spot.Z);
            ToolContext context = Looking(world, eye, Vector3.Down, null, 1f / 60f, ShovelMode.Raise, stroke);

            if (shovel.TryTarget(context, out NodeHit target))
                shovel.Mine(context, target);

            world.MeshAllNow();
            await PhysicsFrames(1);

            float now = DrawnHeight(world, Spot);
            biggest = Mathf.Max(biggest, Mathf.Abs(now - last));
            last = now;
        }

        // The brush's own rate is 2.5 units a second: a frame moves 0.04.
        Check(biggest < 0.15f, $"the drawn ground jumped {biggest:0.00} in one frame");

        float stopped = Height(world, Spot);
        Check(stopped > Surface + node * 0.75f, $"a held raise barely lifted the ground, to {stopped:0.00}");
        Check(stopped < Surface + node * 1.25f, $"a held raise did not stop, reaching {stopped:0.00}");

        // Flat on top: most of a node out from the middle, still at the top.
        foreach (Vector3 way in new[] { Vector3.Right, Vector3.Left, Vector3.Forward, Vector3.Back })
            Near(Height(world, Spot + way * node * 0.75f), stopped, 0.3f, $"the top {way} of the middle");

        // Widen the base -- raise all the way round it -- and the middle grows on.
        for (int k = 0; k < 8; k++)
        {
            float angle = Mathf.Tau * k / 8f;
            await Shape(world, shovel, ShovelMode.Raise, Spot + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 3.5f, 40);
        }

        // By about a unit, give or take the dithering of the writes.
        await Shape(world, shovel, ShovelMode.Raise, Spot, 40);
        Check(Height(world, Spot) > stopped + 0.5f,
            $"widening the base did not let the middle grow, {stopped:0.00} to {Height(world, Spot):0.00}");
    }

    /// <summary>Lowering takes particles away down to the rock, and never the rock itself.</summary>
    [Test]
    public async Task LoweringStopsAtRock()
    {
        NodeWorld world = await Bed();
        var shovel = new ShovelTool();

        int RawCells()
        {
            int count = 0;
            for (int x = -4; x <= 4; x++)
            for (int y = -3; y <= 6; y++)
            for (int z = -4; z <= 4; z++)
                count += world.TypeAt(new Vector3I(x, y, z)) is RawNode ? 1 : 0;
            return count;
        }

        int rock = RawCells();

        // Once the rock is bared there is nothing left for a shovel to aim at.
        await Shape(world, shovel, ShovelMode.Lower, Spot, 40);

        float bottom = Height(world, Spot);
        Check(bottom < Surface - 4f, $"lowering only got {Surface - bottom:0.00} down");
        Equal(RawCells(), rock, "raw nodes around the pit");
    }

    /// <summary>Level flattens toward the height of the ground where the press began.</summary>
    [Test]
    public async Task LevelFlattensToThePressHeight()
    {
        NodeWorld world = await Bed();
        var shovel = new ShovelTool();

        await Shape(world, shovel, ShovelMode.Raise, Spot, 12);
        Check(Height(world, Spot) > Surface + 1f, "no bump to level");

        // Press on flat ground beside the bump, then drag onto it.
        var stroke = new ToolStroke();
        await Shape(world, shovel, ShovelMode.Level, Spot + Vector3.Right * 8f, 1, stroke);
        await Shape(world, shovel, ShovelMode.Level, Spot, 25, stroke);

        Near(Height(world, Spot), Surface, 0.35f, "the bump's top after levelling");
    }

    /// <summary>Writes ground of a given height at each column around a spot, straight into the fills.</summary>
    private static void Sculpt(NodeWorld world, Vector3 around, int span, System.Func<float, float, float> height)
    {
        Vector3I middle = world.Grid.NearestLattice(around);

        for (int x = -span; x <= span; x++)
        for (int z = -span; z <= span; z++)
        for (int y = -2; y <= 8; y++)
        {
            var cell = new Vector3I(middle.X + x, y, middle.Z + z);
            Vector3 at = world.Grid.LatticePoint(cell);
            float top = height(at.X - around.X, at.Z - around.Z);
            world.SetParticleLevel(cell, NodeTypes.Sand, (top - at.Y) / world.NodeSize);
        }

        world.MeshAllNow();
    }

    /// <summary>
    /// Level leaves ground more than a node above or below the press height
    /// alone: dragged from flat ground into a tall mound, the mound stays.
    /// </summary>
    [Test]
    public async Task LevelLeavesFarGroundAlone()
    {
        NodeWorld world = await Bed();
        var shovel = new ShovelTool();
        Vector3 mound = Spot + Vector3.Right * 10f;

        // A flat-topped mound 5 units high, far past a node.
        Sculpt(world, mound, 5, (x, z) => Surface + Mathf.Clamp(8f - new Vector2(x, z).Length(), 0f, 5f));
        await PhysicsFrames();
        float before = Height(world, mound);

        var stroke = new ToolStroke();
        await Shape(world, shovel, ShovelMode.Level, Spot, 1, stroke);
        await Shape(world, shovel, ShovelMode.Level, mound, 25, stroke);

        Near(Height(world, mound), before, 0.2f, "the tall mound's top after levelling into it");
    }

    /// <summary>
    /// Level flattens to the HEIGHT where the press began, level against
    /// gravity -- pressed on a slope, it does not keep the slope's tilt.
    /// </summary>
    [Test]
    public async Task LevelIsLevelOnASlope()
    {
        NodeWorld world = await Bed();
        var shovel = new ShovelTool();

        // A gentle slope rising along x, at the bed's height under the spot.
        Sculpt(world, Spot, 7, (x, z) => Surface + 0.3f * x);
        await PhysicsFrames();

        var stroke = new ToolStroke();
        await Shape(world, shovel, ShovelMode.Level, Spot, 25, stroke);

        foreach (float x in new[] { -1.5f, 1.5f })
            Near(Height(world, Spot + Vector3.Right * x), Surface, 0.25f, $"the ground {x} along the slope");
    }

    /// <summary>Smooth softens a peak, spreading it rather than removing it.</summary>
    [Test]
    public async Task SmoothSoftensAPeak()
    {
        NodeWorld world = await Bed();
        var shovel = new ShovelTool();
        var aside = Vector3.Forward * 2.2f;

        // A cone 4 units high, written straight into the fills: a raise makes
        // only flat tops.
        var middle = world.Grid.NearestLattice(Spot);
        for (int x = -3; x <= 3; x++)
        for (int z = -3; z <= 3; z++)
        for (int y = -2; y <= 6; y++)
        {
            var cell = new Vector3I(middle.X + x, y, middle.Z + z);
            float top = Surface + Mathf.Max(0f, 4f - 1.5f * world.NodeSize * new Vector2(x, z).Length());
            world.SetParticleLevel(cell, NodeTypes.Sand, (top - world.Grid.LatticePoint(cell).Y) / world.NodeSize);
        }

        world.MeshAllNow();
        await PhysicsFrames();

        float peak = Height(world, Spot) - Surface;
        float flank = Height(world, Spot + aside) - Surface;

        await Shape(world, shovel, ShovelMode.Smooth, Spot, 15);
        float smoothedPeak = Height(world, Spot) - Surface;
        float smoothedFlank = Height(world, Spot + aside) - Surface;

        Check(smoothedPeak < peak * 0.8f, $"smoothing only brought the peak from {peak:0.00} to {smoothedPeak:0.00}");
        Check(smoothedFlank > flank, $"smoothing did not spread the peak: its flank went from {flank:0.00} to {smoothedFlank:0.00}");
    }

    [Test]
    public async Task LedgerCanRefuse()
    {
        NodeWorld world = await Bed();
        var refusing = new CountingLedger { Allow = false };
        var shovel = new ShovelTool();
        float before = Height(world, Spot);

        await Shape(world, shovel, ShovelMode.Raise, Spot, 3, ledger: refusing);
        await Shape(world, shovel, ShovelMode.Lower, Spot, 3, ledger: refusing);

        Near(Height(world, Spot), before, 0.001f, "ground after refused edits");
        Check(refusing.Asked >= 2, "the ledger was never consulted");

        var counting = new CountingLedger { Allow = true };
        await Shape(world, shovel, ShovelMode.Raise, Spot, 3, ledger: counting);
        Check(counting.Placed > 0f, "a raise was not recorded");
    }

    /// <summary>Every mode has the one brush, so none draws an inner circle.</summary>
    [Test]
    public async Task NoModeDrawsAnInnerCircle()
    {
        NodeWorld world = await Bed();
        var shovel = new ShovelTool();

        foreach (ShovelMode mode in System.Enum.GetValues<ShovelMode>())
        {
            var builder = new HighlightBuilder();
            ToolContext context = Looking(world, Eye, Vector3.Down, mode: mode);
            shovel.TryTarget(context, out NodeHit target);
            shovel.Outline(context, target, builder);
            Check(!builder.ShowInner, $"{mode} draws an inner circle");
        }
    }

    // --------------------------------------------------- walls, ceilings, edges

    /// <summary>
    /// Clears the particles out of a trench running along z, so its end wall
    /// at x = 5 faces +x: a sheer face 6 units tall from the rock floor to the
    /// ground above.
    /// </summary>
    private async Task<NodeWorld> Trench()
    {
        NodeWorld world = FlatBed.Build(Root, Surface);

        for (int x = 3; x <= 12; x++)
        for (int y = -3; y <= 8; y++)
        for (int z = -5; z <= 5; z++)
        {
            var cell = new Vector3I(x, y, z);
            if (world.TypeAt(cell) is ParticleNode)
                world.ClearNode(cell);
        }

        world.MeshAllNow();
        await PhysicsFrames();
        return world;
    }

    /// <summary>Holds the left button looking along a line, re-meshing between steps.</summary>
    private async Task ShapeAlong(NodeWorld world, ShovelTool shovel, ShovelMode mode, Vector3 eye, Vector3 aim,
        int steps, float delta = 0.1f)
    {
        var stroke = new ToolStroke();

        for (int step = 0; step < steps; step++)
        {
            ToolContext context = Looking(world, eye, aim, null, delta, mode, stroke);

            if (!shovel.TryTarget(context, out NodeHit target))
                break;

            shovel.Mine(context, target);
            world.MeshAllNow();
            await PhysicsFrames(1);
        }
    }

    /// <summary>The distance along a ray to the drawn ground, or NaN.</summary>
    private static float Along(NodeWorld world, Vector3 from, Vector3 direction, float length = 30f) =>
        world.Raycast(from, from + direction * length, out NodeHit hit) ? from.DistanceTo(hit.Point) : float.NaN;

    /// <summary>
    /// On a wall the disc stands sideways; every path walks the full radius, so
    /// it covers the same area as on flat ground; and near the top it folds
    /// over the edge onto the ground above.
    /// </summary>
    [Test]
    public async Task DiscLiesOnAWall()
    {
        NodeWorld world = await Trench();
        var shovel = new ShovelTool();
        var eye = new Vector3(9f, 8.3f, 0.3f);

        ToolContext context = Looking(world, eye, Vector3.Left);
        Check(shovel.TryTarget(context, out NodeHit wall), "the shovel found no wall");

        SurfaceDisc disc = shovel.DiscAt(world, wall);
        Check(disc.Centre.Normal.Dot(Vector3.Right) > 0.8f, $"the disc faces {disc.Centre.Normal}, not out of the wall");

        bool overTheTop = false;

        for (int d = 0; d < disc.Directions; d++)
        {
            SurfaceDisc.Sample rim = disc.At(d, disc.Radius);
            Near(rim.Arc, disc.Radius, 0.05f, $"the length of path {d} along the surface");
            overTheTop |= rim.Normal.Y > 0.7f && rim.Position.Y > Surface - 0.5f;
        }

        Check(overTheTop, "the disc does not fold over the wall's top edge onto the ground");
    }

    /// <summary>Raising a wall pushes it out sideways, toward whoever is facing it.</summary>
    [Test]
    public async Task RaisingAWallPushesItOut()
    {
        NodeWorld world = await Trench();
        var shovel = new ShovelTool();
        var eye = new Vector3(9f, 7.3f, 0.3f);

        float before = Along(world, eye, Vector3.Left);
        await ShapeAlong(world, shovel, ShovelMode.Raise, eye, Vector3.Left, 10);
        float after = Along(world, eye, Vector3.Left);

        Check(after < before - 1f, $"the wall only came out from {before:0.00} to {after:0.00} away");
    }

    /// <summary>Lowering a ceiling digs up into it.</summary>
    [Test]
    public async Task LoweringACeilingDigsUp()
    {
        NodeWorld world = FlatBed.Build(Root, Surface);

        // A cavity in the rock, right up under the particle shell.
        for (int x = -4; x <= 4; x++)
        for (int y = -3; y <= 2; y++)
        for (int z = -4; z <= 4; z++)
        {
            var cell = new Vector3I(x, y, z);
            if (world.TypeAt(cell) is RawNode)
                world.ClearNode(cell);
        }

        world.MeshAllNow();
        await PhysicsFrames();

        var shovel = new ShovelTool();
        var eye = new Vector3(0.3f, 1f, 0.2f);

        Check(shovel.TryTarget(Looking(world, eye, Vector3.Up), out NodeHit ceiling), "the shovel found no ceiling");
        Check(ceiling.Type is ParticleNode, "the ceiling is not particles");

        float before = Along(world, eye, Vector3.Up);
        await ShapeAlong(world, shovel, ShovelMode.Lower, eye, Vector3.Up, 8);
        float after = Along(world, eye, Vector3.Up);

        Check(after > before + 0.8f, $"the ceiling only went from {before:0.00} to {after:0.00} away");
    }

    /// <summary>The shovel reaches a few steps, as the pickaxe does.</summary>
    [Test]
    public async Task ShovelReachIsShort()
    {
        NodeWorld world = await Bed();
        var shovel = new ShovelTool();

        Check(!shovel.TryTarget(Looking(world, Eye + Vector3.Up * 5f, Vector3.Down), out _),
            "the shovel reached ground 8 units away");
        Check(shovel.TryTarget(Looking(world, Eye + Vector3.Up * 1f, Vector3.Down), out _),
            "the shovel did not reach ground 4 units away");
    }

    /// <summary>The mode key steps through a tool's modes and wraps round; each tool keeps its own.</summary>
    [Test]
    public void ModesCycle()
    {
        var controller = new ToolController();
        var shovel = new ShovelTool();
        var pickaxe = new PickaxeTool();

        try
        {
            Equal(controller.ModeOf(shovel), 0, "the shovel's first mode");

            controller.CycleMode(shovel);
            Equal(controller.ModeOf(shovel), 1, "the shovel's mode after one press");

            for (int n = 1; n < shovel.Modes.Count; n++)
                controller.CycleMode(shovel);

            Equal(controller.ModeOf(shovel), 0, "the shovel's mode after going all the way round");

            controller.CycleMode(pickaxe);
            Equal(controller.ModeOf(pickaxe), 0, "a tool without modes");
            Equal(shovel.Modes.Count, 4, "the shovel's modes");
        }
        finally
        {
            controller.Free();
        }
    }

    [Test]
    public async Task EachToolTargetsOnlyItsOwnForm()
    {
        NodeWorld world = await Bed();
        ToolContext context = Looking(world, Eye, Vector3.Down);

        var shovel = new ShovelTool();
        var pickaxe = new PickaxeTool();

        Check(shovel.TryTarget(context, out NodeHit hit), "the shovel found no sand below");
        Check(hit.Type is SandNode, $"the shovel hit {hit.Type}");
        Near(hit.Point.Y, Surface, 0.05f, "hit height");

        Check(!pickaxe.TryTarget(context, out _), "the pickaxe targeted sand");
    }

    [Test]
    public async Task PickaxeMinesAndPlacesRock()
    {
        NodeWorld world = await Bed();

        // Dig the sand away to bare the rock beneath.
        for (int x = -3; x <= 3; x++)
        for (int y = -3; y <= 7; y++)
        for (int z = -3; z <= 3; z++)
        {
            var cell = new Vector3I(x, y, z);
            if (world.TypeAt(cell) is ParticleNode)
                world.ClearNode(cell);
        }

        world.MeshAllNow();
        await PhysicsFrames();

        var pickaxe = new PickaxeTool();
        ToolContext context = Looking(world, new Vector3(0.2f, 8f, 0.1f), Vector3.Down);

        Check(pickaxe.TryTarget(context, out NodeHit rock), "the pickaxe found no bare rock");
        Check(rock.Type is StoneNode, $"the pickaxe hit {rock.Type}");
        Check(!new ShovelTool().TryTarget(context, out _), "the shovel targeted bare rock");

        Check(pickaxe.Place(context, rock), "placing did nothing");
        Check(world.TypeAt(rock.Outside) is StoneNode, "the placed node is not stone");

        Check(pickaxe.Mine(context, rock), "mining did nothing");
        Check(world.TypeAt(rock.Cell) == null, "the mined node is still there");
    }

    [Test]
    public async Task HighlightsDrawForTheirTargets()
    {
        NodeWorld world = await Bed();
        ToolContext context = Looking(world, Eye, Vector3.Down);
        var builder = new HighlightBuilder();

        var shovel = new ShovelTool();
        Check(shovel.TryTarget(context, out NodeHit sand), "no sand target");
        shovel.Outline(context, sand, builder);

        // The shovel draws its brush as a skin on the ground, carrying where
        // each point is in the flat circle -- never past the disc it lies on.
        ArrayMesh skin = builder.BuildDisc();
        Check(skin != null, "the shovel drew no brush");
        Check(builder.BuildOutline() == null && builder.BuildGlow() == null, "the shovel still drew bars or a glow");
        Near(builder.DiscRadius, shovel.OuterRadius, 0.0001f, "the brush's drawn radius");

        Vector2[] inDisc = skin.SurfaceGetArrays(0)[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        float furthest = 0f;
        foreach (Vector2 point in inDisc)
            furthest = Mathf.Max(furthest, point.Length());

        Near(furthest, shovel.OuterRadius + shovel.SkinMargin, 0.05f, "how far the skin reaches in the disc");

        builder.Clear();
        var pickaxe = new PickaxeTool();
        var cell = world.CellAt(new Vector3(0, -2, 0));
        var rock = new NodeHit(world.SiteOf(cell), Vector3.Up, NodeTypes.Stone, cell, cell);
        pickaxe.Outline(context, rock, builder);

        Check(builder.BuildOutline() != null, "the pickaxe drew no outline");
        CheckGlowWorksInward(builder.BuildGlow(), "pickaxe");
    }

    /// <summary>
    /// A glow's gradient and particles run in from the outline: every surface
    /// starts at distance 0 on the outline and reaches its full depth in the
    /// middle, and the particle grid's inward axis agrees with that distance.
    /// </summary>
    private static void CheckGlowWorksInward(ArrayMesh glow, string tool)
    {
        Check(glow != null, $"the {tool} drew no glow");

        var arrays = glow.SurfaceGetArrays(0);
        Vector2[] grid = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        Vector2[] inward = arrays[(int)Mesh.ArrayType.TexUV2].AsVector2Array();

        bool touchesOutline = false, reachesMiddle = false;

        for (int n = 0; n < inward.Length; n++)
        {
            Near(grid[n].Y, inward[n].X, 0.0001f, $"{tool} particle grid against distance in");
            Check(inward[n].X <= inward[n].Y + 0.0001f, $"{tool} glow runs past its middle");

            touchesOutline |= inward[n].X < 0.0001f;
            reachesMiddle |= Mathf.Abs(inward[n].X - inward[n].Y) < 0.0001f;
        }

        Check(touchesOutline && reachesMiddle, $"the {tool} glow does not span outline to middle");
    }

    private sealed class CountingLedger : IMaterialLedger
    {
        public bool Allow;
        public int Asked;
        public float Placed;

        public bool CanMine(NodeType type, float amount)
        {
            Asked++;
            return Allow;
        }

        public bool CanPlace(NodeType type, float amount)
        {
            Asked++;
            return Allow;
        }

        public void Mined(NodeType type, float amount)
        {
        }

        void IMaterialLedger.Placed(NodeType type, float amount) => Placed += amount;
    }
}
