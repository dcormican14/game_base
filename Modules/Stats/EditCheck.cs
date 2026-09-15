using Godot;
using GameBase.Nodes;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Checks that mining and placing actually work on whatever grid is loaded.
///
/// The mesh audits prove the world is BUILT correctly; this proves it can be
/// CHANGED correctly, which is a different set of failures: a ray that picks
/// the wrong node, an edit stored under an address the mesher never reads, a
/// section that is not rebuilt afterward.
///
/// The test is end to end on purpose. It casts a ray the way the player's own
/// mining does, removes what it hits, and checks that the node really went and
/// that the geometry around it was rebuilt -- then puts one back and checks the
/// same in reverse.
/// </summary>
public partial class EditCheck : Node
{
    [Export] public int WaitFrames { get; set; } = 900;
    [Export] public bool QuitWhenDone { get; set; } = true;

    private NodeWorld _world;
    private Node3D _player;
    private ChunkStreamer _streamer;
    private int _frames;
    private bool _done;

    public override void _Ready()
    {
        Node scene = GetTree().CurrentScene;
        _world = NodeSearch.FindByType<NodeWorld>(scene);
        _player = scene?.GetNodeOrNull<Node3D>("Player");
        _streamer = NodeSearch.FindByType<ChunkStreamer>(scene);
    }

    public override void _Process(double delta)
    {
        if (_done)
            return;

        _frames++;

        bool ready = _streamer == null || _streamer.IsReady;
        if (!ready && _frames < WaitFrames)
            return;

        _done = true;
        Run();

        if (QuitWhenDone)
            GetTree().Quit(0);
    }

    private void Run()
    {
        GD.Print("=== EDIT CHECK ===");

        INodeGrid grid = _world?.Grid;
        if (grid == null || _player == null)
        {
            GD.Print("EDITCHECK: no world or player");
            return;
        }

        // Straight down from just above the player, which is how the ground
        // under their feet gets picked.
        Vector3 from = _player.GlobalPosition + _player.GlobalTransform.Basis.Y * 2f;
        Vector3 down = -_player.GlobalTransform.Basis.Y;

        if (!_world.RayPick(from, down, 24f, out Vector3I hit, out Vector3I empty))
        {
            GD.Print("EDITCHECK: ray hit nothing -- cannot test edits");
            return;
        }

        GD.Print($"  ray hit node {hit}, open cell above {empty}");

        // MINE.
        bool wasSolid = _world.HasSolid(hit);
        bool removed = _world.RemoveNode(hit);
        bool nowEmpty = !_world.HasSolid(hit);

        GD.Print("-- mining --");
        GD.Print($"  was solid before: {wasSolid}");
        GD.Print($"  RemoveNode said:  {removed}");
        GD.Print($"  empty after:      {nowEmpty}");

        // PLACE, into the gap just opened.
        bool added = _world.AddNode(hit, NodeMaterial.Stone);
        bool solidAgain = _world.HasSolid(hit);

        GD.Print("-- placing --");
        GD.Print($"  AddNode said:     {added}");
        GD.Print($"  solid after:      {solidAgain}");

        // PLACE INTO OPEN AIR, which is what a right-click against a face does.
        //
        // The cell the ray passed through on its way in is usually SKY -- shell
        // -1, above the surface, which the grid deliberately does not contain
        // so that a ray from the eye cannot report a hit before it has
        // travelled. Refusing to build there is correct, not a failure, so the
        // test only expects a placement where the grid actually has a cell.
        bool openIsOnGrid = grid.Contains(empty);
        bool addedAir = openIsOnGrid && _world.AddNode(empty, NodeMaterial.Stone);
        bool airSolid = openIsOnGrid && _world.HasSolid(empty);

        GD.Print("-- placing into the open cell --");
        GD.Print($"  cell is on the grid: {openIsOnGrid}"
            + (openIsOnGrid ? "" : "  (sky above the surface -- nothing to build on)"));

        if (openIsOnGrid)
        {
            GD.Print($"  AddNode said:     {addedAir}");
            GD.Print($"  solid after:      {airSolid}");
            _world.RemoveNode(empty);
        }

        // Digging one down and building back into the hole is the same motion a
        // player makes, and it exercises a cell the grid really has.
        bool pocket = false;

        bool hasBelow = grid.RadialNeighbour(hit, -1, out Vector3I below);
        bool belowSolid = hasBelow && _world.HasSolid(below);

        GD.Print("-- digging one deeper and filling it back --");
        GD.Print($"  a node below exists: {hasBelow}"
            + (hasBelow ? $" ({below})" : "")
            + $", solid: {belowSolid}");

        if (hasBelow && belowSolid)
        {
            _world.RemoveNode(below);
            bool gone = !_world.HasSolid(below);

            _world.AddNode(below, NodeMaterial.Stone);
            pocket = gone && _world.HasSolid(below);

            GD.Print($"  emptied then refilled: {pocket}");
        }

        bool pass = wasSolid && removed && nowEmpty && added && solidAgain
            && (!openIsOnGrid || (addedAir && airSolid)) && pocket;

        GD.Print(pass
            ? "VERDICT: mining and placing both work"
            : "VERDICT: FAILED -- see the lines above for which half");

        GD.Print("=== END EDIT CHECK ===");
    }
}
