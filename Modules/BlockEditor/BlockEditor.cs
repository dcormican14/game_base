using Godot;
using GameBase.Blocks;

namespace GameBase.Player;

/// <summary>
/// Places and mines cubes anywhere in the world. Raycasts from the camera
/// centre (matching the crosshair): left click places a block, right click
/// mines the one being looked at.
///
/// Placement works against anything solid, not only against existing blocks —
/// aim at the terrain and the new cube snaps to the world grid cell in front
/// of the surface. Both buttons are exported input actions, so they appear in
/// the rebind list like any other binding.
///
/// Instance BlockEditor.tscn under the player; it finds the scene's camera and
/// BlockWorld itself unless the paths are set.
/// </summary>
public partial class BlockEditor : Node3D
{
    [Export] public NodePath CameraPath { get; set; } = "";
    /// <summary>The BlockWorld to edit. Found by search when left empty.</summary>
    [Export] public NodePath BlockWorldPath { get; set; } = "";
    [Export] public StringName PlaceAction { get; set; } = "place_block";
    [Export] public StringName MineAction { get; set; } = "destroy_block";
    [Export(PropertyHint.Range, "1,100,0.5")] public float Reach { get; set; } = 6f;
    [Export(PropertyHint.Layers3DPhysics)] public uint CollisionMask { get; set; } = 1;

    [ExportGroup("Player Clearance")]
    /// <summary>Radius of the player capsule, for the do-not-place-inside-me test.</summary>
    [Export] public float PlayerRadius { get; set; } = 0.35f;
    [Export] public float PlayerHeight { get; set; } = 1.8f;

    private Camera3D _camera;
    private BlockWorld _world;
    private CollisionObject3D _playerBody;

    public override void _Ready()
    {
        _camera = !CameraPath.IsEmpty ? GetNodeOrNull<Camera3D>(CameraPath) : null;
        _camera ??= FindNode<Camera3D>(GetTree().CurrentScene ?? GetParent());

        _world = !BlockWorldPath.IsEmpty ? GetNodeOrNull<BlockWorld>(BlockWorldPath) : null;
        _world ??= FindNode<BlockWorld>(GetTree().CurrentScene ?? GetParent());

        if (_camera == null || _world == null)
            GD.PushWarning("BlockEditor: needs a Camera3D and a BlockWorld — editing disabled.");

        for (Node node = GetParent(); node != null; node = node.GetParent())
        {
            if (node is CollisionObject3D body)
            {
                _playerBody = body;
                break;
            }
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_camera == null || _world == null || Input.MouseMode != Input.MouseModeEnum.Captured)
            return;

        bool place = @event.IsActionPressed(PlaceAction);
        bool mine = @event.IsActionPressed(MineAction);
        if (!place && !mine)
            return;

        // Ray comes from the CAMERA (the player's view), so what the crosshair
        // covers is what gets edited — the model is never in the way. Reach is
        // measured from the player though, or standing back from a ledge would
        // silently cost you the camera's set-back distance.
        Vector2 centre = _camera.GetViewport().GetVisibleRect().Size * 0.5f;
        Vector3 from = _camera.ProjectRayOrigin(centre);
        Vector3 dir = _camera.ProjectRayNormal(centre);

        float reach = Reach;
        if (_playerBody != null)
            reach += from.DistanceTo(_playerBody.GlobalPosition);

        bool changed = mine ? Mine(from, dir, reach) : Place(from, dir, reach);
        if (changed)
            GetViewport().SetInputAsHandled();
    }

    private bool Mine(Vector3 from, Vector3 dir, float reach)
    {
        return _world.RayPick(from, dir, reach, out Vector3I hit, out _)
            && _world.RemoveBlock(hit);
    }

    private bool Place(Vector3 from, Vector3 dir, float reach)
    {
        // Against an existing block: place in the empty cell the ray entered
        // through, so the cube lands on the face being looked at.
        if (_world.RayPick(from, dir, reach, out Vector3I hit, out Vector3I empty) && empty != hit)
            return PlaceIfClear(empty);

        // Otherwise place against whatever the physics world hit (terrain), so
        // building can start anywhere rather than only on existing blocks.
        var query = PhysicsRayQueryParameters3D.Create(from, from + dir * reach, CollisionMask);
        if (_playerBody != null)
            query.Exclude = new Godot.Collections.Array<Rid> { _playerBody.GetRid() };

        var surface = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (surface.Count == 0)
            return false;

        Vector3 point = (Vector3)surface["position"];
        Vector3 normal = ((Vector3)surface["normal"]).Normalized();
        return PlaceIfClear(_world.CellAt(point + normal * (_world.BlockSize * 0.5f)));
    }

    /// <summary>
    /// Places a block unless the cube would actually overlap the player's
    /// capsule. Tested as a real box-vs-capsule overlap rather than a keep-out
    /// box around the body origin: the crude version rejected anything near
    /// the feet, so standing on a ledge and aiming at its face refused to
    /// place even with a clear line of sight.
    /// </summary>
    private bool PlaceIfClear(Vector3I cell)
    {
        if (_playerBody != null && OverlapsPlayer(cell))
            return false;

        return _world.AddBlock(cell);
    }

    private bool OverlapsPlayer(Vector3I cell)
    {
        float half = _world.BlockSize * 0.5f;
        Vector3 centre = _world.CellCentre(cell);
        Vector3 min = centre - Vector3.One * half;
        Vector3 max = centre + Vector3.One * half;

        // The capsule stands on the body origin: a vertical segment from
        // radius above the feet to height-radius, expanded by its radius.
        Vector3 feet = _playerBody.GlobalPosition;
        Vector3 segmentBottom = feet + Vector3.Up * PlayerRadius;
        Vector3 segmentTop = feet + Vector3.Up * (PlayerHeight - PlayerRadius);

        // Closest point on the capsule's axis to the box, then a sphere test.
        Vector3 closestOnAxis = segmentBottom;
        float axisLength = segmentTop.Y - segmentBottom.Y;
        if (axisLength > 0.0001f)
        {
            float boxCentreY = Mathf.Clamp(centre.Y, segmentBottom.Y, segmentTop.Y);
            closestOnAxis = new Vector3(feet.X, boxCentreY, feet.Z);
        }

        Vector3 closestOnBox = new(
            Mathf.Clamp(closestOnAxis.X, min.X, max.X),
            Mathf.Clamp(closestOnAxis.Y, min.Y, max.Y),
            Mathf.Clamp(closestOnAxis.Z, min.Z, max.Z));

        return closestOnBox.DistanceTo(closestOnAxis) < PlayerRadius;
    }

    private static T FindNode<T>(Node root) where T : Node
    {
        if (root == null)
            return null;
        if (root is T match)
            return match;
        foreach (Node child in root.GetChildren())
        {
            T found = FindNode<T>(child);
            if (found != null)
                return found;
        }

        return null;
    }
}
