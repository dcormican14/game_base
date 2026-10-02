using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using GameBase.Core;
using GameBase.Planets;
using GameBase.Terrain;

namespace GameBase.Tests.Capture;

/// <summary>
/// Tuning aid and load-time check: plays the real level at chosen spots on a
/// seed's planet -- the middle of a flat region, of hill country, of each
/// kind of mountain range, and a border where a range meets the plains --
/// times how long each takes to become playable, and screenshots each from
/// the player's eye and from the air.
///
///   godot --path . res://Tests/Capture/TerrainCapture.tscn -- --seed=1 --out=C:/shots [--spots=flat,jagged]
///
/// Quits with 1 if any spot took longer than <see cref="LoadBudgetSeconds"/>
/// to load. Not headless: it needs the renderer for its pictures.
/// </summary>
public partial class TerrainCapture : Node
{
    public const double LoadBudgetSeconds = 60;

    private string _seed = WorldSeed.Default;
    private string _out = "user://terrain";
    private string[] _spots =
        { "flat", "hills", "jagged", "rounded", "towering", "mesa", "border", "basin", "canyon", "islands", "rim" };

    public override void _Ready()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--seed="))
                _seed = arg["--seed=".Length..];
            else if (arg.StartsWith("--out="))
                _out = arg["--out=".Length..];
            else if (arg.StartsWith("--spots="))
                _spots = arg["--spots=".Length..].Split(',');
        }

        _ = Run();
    }

    private async Task Run()
    {
        bool slow = false;

        try
        {
            DirAccess.MakeDirRecursiveAbsolute(_out);
            var terrain = new PlanetTerrain(6000f, WorldSeed.Parse(_seed), new TerrainSettings());

            foreach (string spot in _spots)
            {
                Vector3 direction = Find(terrain, spot);
                double seconds = await Visit(spot, direction);
                slow |= seconds > LoadBudgetSeconds;
            }
        }
        catch (Exception error)
        {
            GD.PushError($"TerrainCapture: {error}");
            slow = true;
        }

        GetTree().Quit(slow ? 1 : 0);
    }

    /// <summary>A direction for a named spot on the planet.</summary>
    public static Vector3 Find(PlanetTerrain terrain, string spot)
    {
        RegionMap map = terrain.Regions;

        Vector3 PointOf(int i) => new((float)map.X[i], (float)map.Y[i], (float)map.Z[i]);

        int First(Func<int, bool> match)
        {
            for (int i = 0; i < map.Count; i++)
            {
                if (match(i))
                    return i;
            }

            throw new InvalidOperationException($"no region for '{spot}'");
        }

        switch (spot)
        {
            case "flat":
                return PointOf(First(i => map.Types[i] == RegionType.Flat));
            case "hills":
                return PointOf(First(i => map.Types[i] == RegionType.Hills));
            case "basin":
                return PointOf(First(i => map.Types[i] == RegionType.Basin));
            case "canyon":
            {
                // Halfway up the first canyon, on its floor.
                Canyons.Path path = terrain.Canyons.Paths[0];
                int middle = path.Count / 2;
                return new Vector3((float)path.X[middle], (float)path.Y[middle], (float)path.Z[middle]);
            }
            case "islands":
            {
                // The middle of a zone: the spawn lands on whatever island is highest there.
                SkyIslands.Zone zone = terrain.Islands.Zones[0];
                return new Vector3((float)zone.X, (float)zone.Y, (float)zone.Z);
            }
            case "rim":
            {
                // Just outside a zone's rim, looking in.
                SkyIslands.Zone zone = terrain.Islands.Zones[0];
                var centre = new Vector3((float)zone.X, (float)zone.Y, (float)zone.Z);
                Vector3 east = centre.Cross(Mathf.Abs(centre.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
                return (centre + east * (float)((zone.Radius + 60) / terrain.BaseRadius)).Normalized();
            }
            case "border":
            {
                // Between a range and the nearest plain to it, a little on the
                // plain's side, looking at the range.
                int range = First(i => map.Types[i] == RegionType.Mountains);
                Vector3 mountain = PointOf(range);
                int plain = -1;
                float best = -2f;
                for (int i = 0; i < map.Count; i++)
                {
                    float dot = PointOf(i).Dot(mountain);
                    if (map.Types[i] == RegionType.Flat && dot > best)
                    {
                        best = dot;
                        plain = i;
                    }
                }

                return mountain.Lerp(PointOf(plain), 0.62f).Normalized();
            }
            default:
                var character = Enum.Parse<MountainCharacter>(spot, true);
                return PointOf(First(i => map.Types[i] == RegionType.Mountains && map.Characters[i] == character));
        }
    }

    private async Task<double> Visit(string spot, Vector3 direction)
    {
        WorldSeed.Chosen = _seed;
        Node level = GD.Load<PackedScene>("res://Game/PlanetLevel.tscn").Instantiate();
        level.GetNode<PlanetSpawn>("PlanetSpawn").Direction = direction;

        var watch = Stopwatch.StartNew();
        AddChild(level);

        var planet = NodeSearch.FindByType<Planet>(level);
        while (!planet.IsReady && watch.Elapsed.TotalSeconds < LoadBudgetSeconds * 2)
            await Frames(1);

        double seconds = watch.Elapsed.TotalSeconds;
        GD.Print($"TerrainCapture: {spot} ready in {seconds:0.0} s "
            + $"(far blocks {planet.Far?.WantedBlocks} by level {planet.Far?.LevelCounts}, chunks {planet.World.Store.ChunkCount}, "
            + $"tris {planet.World.TriangleCount})");

        // Let the loading screen fade and the handover settle.
        await Frames(40);

        Node3D player = (Node3D)GetTree().GetFirstNodeInGroup(Groups.Player);
        Vector3 up = planet.UpAt(player.GlobalPosition);
        Vector3 across = up.Cross(Mathf.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();

        TerrainInfo info = planet.Describe(player.GlobalPosition);
        GD.Print($"TerrainCapture: {spot} is {info.Type} {info.Character}, "
            + $"surface {info.SurfaceHeight:0} above base, {info.HeightAboveSeaLevel:0} above sea");

        var camera = new Camera3D { Far = 4500f };
        level.AddChild(camera);
        camera.MakeCurrent();

        // From the eye, level, then from the air.
        var shots = new List<(string Name, Vector3 At, Vector3 Look)>
        {
            ("eye", player.GlobalPosition + up * 1.6f, player.GlobalPosition + up * 1.6f + across * 100f),
            ("air", player.GlobalPosition + up * 140f - across * 200f,
                player.GlobalPosition + up * 20f + across * 400f),
            ("high", player.GlobalPosition + up * 600f - across * 900f,
                player.GlobalPosition + across * 600f),
        };

        // Near a sky-island zone, two more looking into it: from the eye, and
        // from outside and above, the whole heap.
        var terrain = planet.Shape as PlanetTerrain;
        if (terrain != null && (spot == "rim" || spot == "islands") && terrain.Islands.Zones.Count > 0)
        {
            SkyIslands.Zone zone = terrain.Islands.Zones[0];
            Vector3 middle = planet.ToGlobal(new Vector3((float)zone.X, (float)zone.Y, (float)zone.Z)
                * (float)(terrain.BaseRadius + zone.Rim));
            Vector3 toward = (middle - player.GlobalPosition).Slide(up);
            toward = toward.LengthSquared() > 1f ? toward.Normalized() : across;

            shots.Add(("inward", player.GlobalPosition + up * 1.6f, middle + up * 150f));
            shots.Add(("heap", middle - toward * (float)(zone.Radius * 2.2) + up * 450f, middle + up * 300f));
        }

        // The terrain readout (F3) over every shot, to check it reads.
        Input.ParseInputEvent(new InputEventAction { Action = "debug_terrain", Pressed = true });

        foreach ((string name, Vector3 at, Vector3 look) in shots)
        {
            camera.LookAtFromPosition(at, look, up);
            await Frames(12);

            Image image = GetViewport().GetTexture().GetImage();
            string path = $"{_out}/{spot}_{name}.png";
            image.SavePng(path);
        }

        // The handover, looked at straight on from a little above the player:
        // everything, then the far terrain alone, then the real ground alone.
        Vector3 perch = player.GlobalPosition + up * 20f;
        camera.LookAtFromPosition(perch, player.GlobalPosition + across * 160f, up);

        foreach ((string name, bool real, bool far) in new[] { ("band", true, true), ("band_far", false, true), ("band_real", true, false) })
        {
            planet.World.Visible = real;
            if (planet.Far != null)
                planet.Far.Visible = far;

            await Frames(8);
            GetViewport().GetTexture().GetImage().SavePng($"{_out}/{spot}_{name}.png");
        }

        if (planet.Far != null)
        {
            List<string> near = planet.Far.DescribeNear(100f);
            GD.Print($"TerrainCapture: {spot}: {near.Count} far blocks drawn within 100 of the camera");
            foreach (string line in near.GetRange(0, Math.Min(12, near.Count)))
                GD.Print($"TerrainCapture:   {line}");
        }

        planet.World.Visible = true;
        if (planet.Far != null)
            planet.Far.Visible = true;

        level.QueueFree();
        await Frames(4);
        return seconds;
    }

    private async Task Frames(int count)
    {
        for (int n = 0; n < count; n++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
