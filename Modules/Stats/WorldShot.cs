using Godot;
using GameBase.Nodes;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Waits for a world to stream in, then saves a picture of it.
///
/// The audits measure the mesh as DATA -- how faces are wound, which way their
/// normals point, whether any are buried. All of that can pass while the screen
/// still shows nothing, because rendering has its own ways to fail: a material
/// that never uploaded, geometry outside the camera's range, a shader that
/// discards. The only way to know a world is visible is to look at it.
/// </summary>
public partial class WorldShot : Node
{
    /// <summary>Where to write the picture.</summary>
    [Export] public string Output { get; set; } = "user://world.png";

    /// <summary>Frames to wait before giving up on the streamer.</summary>
    [Export] public int WaitFrames { get; set; } = 900;

    /// <summary>Degrees to tilt the camera down, so the shot looks along the
    /// ground rather than at the horizon.</summary>
    [Export] public float LookDown { get; set; } = 35f;

    /// <summary>Frames to wait after posing before reading the buffer.</summary>
    [Export] public int SettleFrames { get; set; } = 30;

    private ChunkStreamer _streamer;
    private NodeWorld _world;
    private int _frames;
    private int _settle;
    private bool _posed;

    public override void _Ready()
    {
        Node scene = GetTree().CurrentScene;
        _streamer = NodeSearch.FindByType<ChunkStreamer>(scene);
        _world = NodeSearch.FindByType<NodeWorld>(scene);
    }

    public override void _Process(double delta)
    {
        _frames++;

        bool ready = _streamer == null || _streamer.IsReady;

        if (!_posed)
        {
            if (!ready && _frames < WaitFrames)
                return;

            Pose();
            _posed = true;
            return;
        }

        // A few frames between posing the camera and reading the buffer: the
        // mesh upload and the frame it appears in are not the same frame.
        if (++_settle < SettleFrames)
            return;

        Image image = GetViewport().GetTexture().GetImage();
        Error err = image.SavePng(Output);

        GD.Print($"WORLDSHOT: {(err == Error.Ok ? "wrote" : "FAILED to write")} {Output}");
        GD.Print($"WORLDSHOT: streamer ready={ready} frames={_frames}"
            + $" nodes={_world?.NodeCount ?? 0} chunks={_world?.LoadedChunks ?? 0}");

        // What fraction of the screen is not sky? A world that meshed but did
        // not render leaves this at zero, which is the failure a data-only
        // audit cannot see.
        GD.Print($"WORLDSHOT: non-sky pixels {Coverage(image):P1}");

        // How much of what is resident actually has geometry? A world that is
        // generated but not meshed looks empty and reports a healthy node
        // count, which is exactly the pair of numbers needed to tell the two
        // apart.
        int meshed = 0, sections = 0;
        CountMeshes(GetTree().CurrentScene, ref meshed, ref sections);

        GD.Print($"WORLDSHOT: {sections} mesh instances, {meshed} with geometry");

        GetTree().Quit(0);
    }

    /// <summary>Counts how many section meshes actually carry triangles.</summary>
    private static void CountMeshes(Node from, ref int meshed, ref int sections)
    {
        if (from is MeshInstance3D mi)
        {
            sections++;

            if (mi.Mesh != null && mi.Mesh.GetSurfaceCount() > 0)
                meshed++;
        }

        foreach (Node child in from.GetChildren())
            CountMeshes(child, ref meshed, ref sections);
    }

    /// <summary>Points the camera along the ground.</summary>
    private void Pose()
    {
        var pivot = GetTree().CurrentScene?.GetNodeOrNull<Node3D>("Player/CameraPivot");

        if (pivot != null)
            pivot.Rotation = new Vector3(Mathf.DegToRad(-LookDown), 0f, 0f);
    }

    /// <summary>
    /// Fraction of pixels that are not the flat background.
    ///
    /// Crude on purpose: any real geometry breaks up the sky, so a number near
    /// zero means nothing was drawn, and anything substantial means the world
    /// is on screen. It is a smoke test, not a rendering comparison.
    /// </summary>
    private static float Coverage(Image image)
    {
        int width = image.GetWidth();
        int height = image.GetHeight();

        if (width == 0 || height == 0)
            return 0f;

        // Sampled on a grid rather than per pixel: this runs once and the
        // answer only needs a couple of significant figures.
        int seen = 0, hits = 0;

        Color sky = image.GetPixel(2, 2);

        for (int x = 0; x < width; x += 4)
        {
            for (int y = 0; y < height; y += 4)
            {
                Color at = image.GetPixel(x, y);
                seen++;

                if (Mathf.Abs(at.R - sky.R) + Mathf.Abs(at.G - sky.G)
                    + Mathf.Abs(at.B - sky.B) > 0.06f)
                    hits++;
            }
        }

        return seen == 0 ? 0f : (float)hits / seen;
    }
}
