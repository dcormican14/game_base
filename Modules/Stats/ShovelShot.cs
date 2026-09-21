using Godot;
using GameBase.Nodes;
using GameBase.Core;
using GameBase.Levels;
using GameBase.Player;

namespace GameBase.Stats;

/// <summary>
/// Photographs the shovel indicator in both modes, so the mark can be reviewed
/// without playing.
///
/// Deliberately separate from <see cref="ShovelCheck"/>: that one asserts that
/// sand moves and belongs in a headless run, this one needs a real window and a
/// camera pointed at the ground. The indicator is a thing you LOOK at, and no
/// measurement of its geometry says whether it reads as a circle with rays
/// coming out of it.
/// </summary>
public partial class ShovelShot : Node
{
    [Export] public string Output { get; set; } = "user://shovel.png";
    [Export] public int WaitFrames { get; set; } = 900;
    [Export] public int SettleFrames { get; set; } = 30;

    /// <summary>Degrees to tilt the camera down, to look at the ground.</summary>
    [Export] public float LookDown { get; set; } = 34f;

    /// <summary>Shoot the lowering mode instead of the raising one.</summary>
    [Export] public bool Lowering { get; set; }

    /// <summary>Dig a crater under the mark first, so the shot shows the
    /// indicator against ground the shovel has actually shaped.</summary>
    [Export] public bool DigFirst { get; set; } = true;

    private ChunkStreamer _streamer;
    private int _frames;
    private int _settle;
    private bool _posed;

    public override void _Ready()
    {
        _streamer = NodeSearch.FindByType<ChunkStreamer>(GetTree().CurrentScene);
    }

    public override void _Process(double delta)
    {
        _frames++;

        bool ready = _streamer == null || _streamer.IsReady;

        if (!_posed)
        {
            if (!ready && _frames < WaitFrames) return;

            Pose();
            _posed = true;
            return;
        }

        // The rays are animated, so a few frames also let them reach a point in
        // their travel where several are mid-flight rather than all at a start.
        if (++_settle < SettleFrames) return;

        Image image = GetViewport().GetTexture().GetImage();
        Error err = image.SavePng(Output);

        var shovel = NodeSearch.FindByType<ShovelHighlight>(GetTree().CurrentScene);

        GD.Print($"SHOVELSHOT: {(err == Error.Ok ? "wrote" : "FAILED")} {Output}");
        GD.Print($"SHOVELSHOT: indicator showing={shovel?.Showing}"
            + $" raising={shovel?.Raising}");

        GetTree().Quit(0);
    }

    private void Pose()
    {
        Node scene = GetTree().CurrentScene;

        var pivot = scene?.GetNodeOrNull<Node3D>("Player/CameraPivot");

        if (pivot != null)
            pivot.Rotation = new Vector3(Mathf.DegToRad(-LookDown), 0f, 0f);

        var shovel = NodeSearch.FindByType<ShovelHighlight>(scene);
        var cap = NodeSearch.FindByType<TopsoilCap>(scene);

        if (shovel == null) return;

        // The mode is swapped through the same action the player presses, so
        // the shot exercises the real path rather than a private setter.
        if (Lowering)
            shovel.SetMode(false);

        if (DigFirst && cap != null)
        {
            var player = scene?.GetNodeOrNull<Node3D>("Player");

            if (player != null)
            {
                // A crater a little ahead of the player, where the camera is
                // already looking.
                Vector3 at = player.GlobalPosition;

                cap.Sculpt(at, 6f, Lowering ? -2.5f : 2.5f);
            }
        }
    }
}
