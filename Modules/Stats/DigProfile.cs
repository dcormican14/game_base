using System;
using System.Collections.Generic;
using Godot;
using GameBase.Nodes;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Breaks a dig's cost into its parts: how many sections it dirties, how many
/// solid cells those sections hold, and what one section costs to re-mesh.
///
/// The total alone does not say what to fix. A dig that is slow because it
/// re-meshes nine sections wants a smaller dirty set; one that is slow because
/// a single section costs 90 ms wants a cheaper mesher.
/// </summary>
public partial class DigProfile : Node
{
    [Export] public int WaitFrames { get; set; } = 900;
    [Export] public int Samples { get; set; } = 30;
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
        if (_done) return;
        _frames++;
        bool ready = _streamer == null || _streamer.IsReady;
        if (!ready && _frames < WaitFrames) return;
        _done = true;
        Run();
        if (QuitWhenDone) GetTree().Quit(0);
    }

    private void Run()
    {
        GD.Print("=== DIG PROFILE ===");

        if (_world == null || _player == null)
        {
            GD.Print("no world or player");
            return;
        }

        Vector3 eye = _player.GlobalPosition;
        var rng = new Random(20260917);

        long sections = 0;
        int digs = 0;
        double totalDirty = 0;

        for (int i = 0; i < Samples; i++)
        {
            float a = (float)(rng.NextDouble() * Math.PI * 2);
            float t = (float)(rng.NextDouble() * 0.5);
            Vector3 down = -eye.Normalized();
            Vector3 side = down.Cross(Vector3.Right).Normalized();
            if (side.LengthSquared() < 0.1f) side = down.Cross(Vector3.Up).Normalized();
            Vector3 other = down.Cross(side);
            Vector3 dir = (down + (side * Mathf.Cos(a) + other * Mathf.Sin(a)) * t).Normalized();

            if (!_world.RayPick(eye, dir, 40f, out Vector3I hit, out _))
                continue;

            int dirty = _world.DirtyCountFor(hit);
            if (dirty == 0) continue;

            sections += dirty;
            totalDirty += dirty;
            digs++;
        }

        if (digs == 0) { GD.Print("no digs"); return; }

        GD.Print($"  digs:                    {digs}");
        GD.Print($"  sections dirtied, mean:  {totalDirty / digs:F1}");
        GD.Print($"  section is {NodeWorld.SectionSize}^3 = {NodeWorld.SectionSize * NodeWorld.SectionSize * NodeWorld.SectionSize} cells");
        GD.Print($"  so cells re-meshed/dig:  {totalDirty / digs * 512:F0}");
        GD.Print($"  at 93us per BuildCell that is {totalDirty / digs * 512 * 0.093:F0} ms if every cell is solid");
    }
}
