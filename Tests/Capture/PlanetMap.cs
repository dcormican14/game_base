using Godot;
using System;
using System.Threading.Tasks;
using GameBase.Terrain;

namespace GameBase.Tests.Capture;

/// <summary>
/// Tuning aid: draws a whole planet as a flat map (longitude across, latitude
/// down) and saves it as a PNG, so a seed's layout -- where the regions fall,
/// how big they are, the mix of types, the mountain characters, the basins --
/// can be judged at a glance without flying round the planet.
///
///   godot --headless --path . res://Tests/Capture/PlanetMap.tscn -- --seed=1 --out=C:/maps/one.png
///
/// Colours: flat is sand, hills green, basins tan, mountains by character
/// (jagged slate, rounded violet, towering amber, mesa rust); ground below sea
/// level is tinted blue; sky-island zones are pale cyan with their rim drawn;
/// canyons show dark in the shading; region borders are dark; everything is
/// shaded by height, lit from the north-west.
/// </summary>
public partial class PlanetMap : Node
{
    public override void _Ready()
    {
        string seed = WorldSeed.Default;
        string output = "user://planet_map.png";
        int width = 1024;
        float radius = 6000f;

        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--seed="))
                seed = arg["--seed=".Length..];
            else if (arg.StartsWith("--out="))
                output = arg["--out=".Length..];
            else if (arg.StartsWith("--width="))
                width = int.Parse(arg["--width=".Length..]);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var terrain = new PlanetTerrain(radius, WorldSeed.Parse(seed), new TerrainSettings());
        Image map = Draw(terrain, width);
        Error error = map.SavePng(output);

        GD.Print($"PlanetMap: seed {seed}, {width}x{width / 2}, {watch.ElapsedMilliseconds} ms -> {output} ({error})");
        GetTree().Quit(error == Error.Ok ? 0 : 1);
    }

    public static Image Draw(PlanetTerrain terrain, int width)
    {
        int height = width / 2;
        var heights = new float[width * height];
        var regions = new int[width * height];
        var zones = new float[width * height];
        var canyons = new bool[width * height];
        RegionMap map = terrain.Regions;
        double radius = terrain.BaseRadius;

        Parallel.For(0, height, y =>
        {
            double latitude = Math.PI * (0.5 - (y + 0.5) / height);

            for (int x = 0; x < width; x++)
            {
                double longitude = 2 * Math.PI * ((x + 0.5) / width - 0.5);
                double ux = Math.Cos(latitude) * Math.Cos(longitude);
                double uy = Math.Sin(latitude);
                double uz = Math.Cos(latitude) * Math.Sin(longitude);

                // On a heightfield the distance at the base sphere IS the height
                // there: cheap, and close enough for a map.
                heights[y * width + x] = (float)terrain.Distance(ux * radius, uy * radius, uz * radius);
                regions[y * width + x] = terrain.RegionAt(ux, uy, uz);
                canyons[y * width + x] = terrain.Canyons.Inside(ux, uy, uz);
                zones[y * width + x] = terrain.Islands.ZoneAt(ux, uy, uz, out double inside) >= 0 && inside > 0
                    ? (float)inside
                    : 0f;
            }
        });

        var image = Image.CreateEmpty(width, height, false, Image.Format.Rgb8);
        float sea = (float)(terrain.SeaLevel - radius);
        double pixel = 2 * Math.PI * radius / width;

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = y * width + x;
            int region = regions[i];

            Color colour = map.Types[region] switch
            {
                RegionType.Flat => new Color("#e8d9b0"),
                RegionType.Hills => new Color("#8fb56a"),
                RegionType.Basin => new Color("#c9a877"),
                _ => map.Characters[region] switch
                {
                    MountainCharacter.Jagged => new Color("#7d8a9c"),
                    MountainCharacter.Rounded => new Color("#9a7fb8"),
                    MountainCharacter.Towering => new Color("#d9a441"),
                    _ => new Color("#b8603f"),
                },
            };

            float h = heights[i];

            // Brighter with height, then lit from the north-west.
            colour = colour.Lerp(Colors.White, Mathf.Clamp(h / 400f, 0f, 0.5f));
            int left = y * width + (x + width - 1) % width;
            int up = Math.Max(0, y - 1) * width + x;
            float slope = (h - heights[left] + h - heights[up]) / (float)pixel;
            colour = colour.Darkened(Mathf.Clamp(-slope * 0.6f, -0.3f, 0.5f));

            if (h < sea)
                colour = colour.Lerp(new Color("#3a6ea5"), 0.6f);

            if (canyons[i])
                colour = new Color("#8c3b2a").Darkened(Mathf.Clamp(-slope * 0.3f, -0.2f, 0.3f));

            if (zones[i] > 0f)
                colour = colour.Lerp(new Color("#c4f0ff"), zones[i] < 0.03f ? 0.9f : 0.55f);

            if (regions[left] != region || regions[up] != region)
                colour = colour.Darkened(0.5f);

            image.SetPixel(x, y, colour);
        }

        return image;
    }
}
