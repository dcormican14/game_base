using Godot;
using System;
using GameBase.Nodes;
using GameBase.Planets;

namespace GameBase.Tests;

/// <summary>The planet generator: rock under sand, a surface exactly at the radius.</summary>
public sealed class PlanetTests : TestSuite
{
    private const float Radius = 6000f;
    private const float ParticleDepth = 6f;

    private readonly PlanetGenerator _planet = new(new VoronoiGrid(2f), Radius, ParticleDepth);

    private static Vector3 Direction(Random random) =>
        new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f)
            .Normalized();

    private byte MaterialAt(Vector3 point)
    {
        _planet.Classify(_planet.Grid.CellAt(point), out byte material, out _);
        return material;
    }

    [Test]
    public void LayersRunRockSandAir()
    {
        var random = new Random(7);

        for (int n = 0; n < 50; n++)
        {
            Vector3 up = Direction(random);

            Equal(MaterialAt(up * (Radius - ParticleDepth - 3f)), (byte)NodeMaterial.Stone, "below the sand");
            Equal(MaterialAt(up * (Radius - 1f)), (byte)NodeMaterial.Sand, "just under the surface");
            Equal(MaterialAt(up * (Radius + 6f)), NodeChunkStore.Air, "above the surface");
        }
    }

    /// <summary>
    /// Each sand cell stores its lattice point's distance to the sphere, which
    /// is what puts the surface exactly at the radius.
    /// </summary>
    [Test]
    public void FillsMeasureDistanceToTheSurface()
    {
        var random = new Random(11);
        VoronoiGrid grid = _planet.Grid;
        int checkedCells = 0;

        for (int n = 0; n < 50; n++)
        {
            Vector3 up = Direction(random);

            for (float r = Radius - 3f; r <= Radius + 3f; r += 1f)
            {
                Vector3I cell = grid.CellAt(up * r);
                _planet.Classify(cell, out byte material, out byte fill);

                if (material != (byte)NodeMaterial.Sand)
                    continue;

                float expected = (Radius - grid.LatticePoint(cell).Length()) / grid.NodeSize;
                if (Mathf.Abs(expected) >= NodeFill.Range)
                    continue;

                Near(NodeFill.ToLevel(fill), expected, 0.01f, $"fill of {cell}");
                checkedCells++;
            }
        }

        Check(checkedCells > 100, $"only {checkedCells} surface cells were checked");
    }

    /// <summary>Sky is generated -- as air, so there is room to build up into it.</summary>
    [Test]
    public void SkyIsAir()
    {
        var materials = new byte[NodeChunkStore.ChunkVolume];
        var fills = new byte[NodeChunkStore.ChunkVolume];
        int outside = (int)(Radius / 2f / NodeChunkStore.ChunkSize) + 3;

        _planet.Generate(new Vector3I(0, outside, 0), materials, fills);

        foreach (byte material in materials)
        {
            if (material != NodeChunkStore.Air)
            {
                Check(false, "a chunk in the sky holds something");
                return;
            }
        }
    }

    /// <summary>The whole-chunk shortcuts give exactly what cell-by-cell would.</summary>
    [Test]
    public void ShortcutsMatchCells()
    {
        var materials = new byte[NodeChunkStore.ChunkVolume];
        var fills = new byte[NodeChunkStore.ChunkVolume];

        Vector3I deep = NodeChunkStore.ChunkOf(_planet.Grid.CellAt(Vector3.Up * (Radius - 300f)));
        Vector3I surface = NodeChunkStore.ChunkOf(_planet.Grid.CellAt(Vector3.Up * Radius));

        foreach (Vector3I chunk in new[] { deep, surface })
        {
            _planet.Generate(chunk, materials, fills);
            Vector3I origin = NodeChunkStore.OriginOf(chunk);

            for (int i = 0; i < NodeChunkStore.ChunkVolume; i += 97)
            {
                var local = new Vector3I(i >> 10, (i >> 5) & 31, i & 31);
                _planet.Classify(origin + local, out byte material, out byte fill);

                Equal(materials[i], material, $"material at {origin + local}");
                Equal(fills[i], fill, $"fill at {origin + local}");
            }
        }
    }
}
