using Godot;
using System;
using System.Collections.Generic;
using GameBase.Nodes;

namespace GameBase.Tests;

/// <summary>
/// Small worlds for tests: a flat bed of sand over stone, with up along +Y,
/// covering the 2x2x2 chunks around the origin.
/// </summary>
internal sealed class FlatBed : IChunkGenerator
{
    public FlatBed(VoronoiGrid grid, float surface, float particleDepth)
    {
        Grid = grid;
        Surface = surface;
        ParticleDepth = particleDepth;
    }

    public VoronoiGrid Grid { get; }

    /// <summary>Height of the sand surface.</summary>
    public float Surface { get; }

    public float ParticleDepth { get; }

    public Vector3 DownAt(Vector3 localPoint) => Vector3.Down;

    public void Generate(Vector3I chunk, byte[] materials, byte[] fills)
    {
        Vector3I origin = NodeChunkStore.OriginOf(chunk);
        const int Size = NodeChunkStore.ChunkSize;

        for (int x = 0; x < Size; x++)
        for (int y = 0; y < Size; y++)
        for (int z = 0; z < Size; z++)
        {
            var cell = new Vector3I(origin.X + x, origin.Y + y, origin.Z + z);
            int i = NodeChunkStore.LocalIndex(x, y, z);

            if (Grid.SiteOf(cell).Y < Surface - ParticleDepth)
            {
                materials[i] = (byte)NodeMaterial.Stone;
                fills[i] = NodeFill.Full;
                continue;
            }

            fills[i] = NodeFill.FromLevel((Surface - Grid.LatticePoint(cell).Y) / Grid.NodeSize);
            materials[i] = fills[i] == NodeFill.Empty ? NodeChunkStore.Air : (byte)NodeMaterial.Sand;
        }
    }

    /// <summary>The chunks the bed fills: the eight around the origin.</summary>
    public static IEnumerable<Vector3I> Chunks()
    {
        for (int x = -1; x <= 0; x++)
        for (int y = -1; y <= 0; y++)
        for (int z = -1; z <= 0; z++)
            yield return new Vector3I(x, y, z);
    }

    /// <summary>Builds a meshed world holding a flat bed, under a test's root.</summary>
    public static NodeWorld Build(Node root, float surface = 10.3f, float particleDepth = 6f,
        float nodeSize = 2f)
    {
        var grid = new VoronoiGrid(nodeSize);
        var bed = new FlatBed(grid, surface, particleDepth);

        var world = new NodeWorld();
        root.AddChild(world);
        world.Configure(grid);

        foreach (Vector3I chunk in Chunks())
        {
            var materials = new byte[NodeChunkStore.ChunkVolume];
            var fills = new byte[NodeChunkStore.ChunkVolume];
            bed.Generate(chunk, materials, fills);
            world.InstallChunk(chunk, materials, fills);
        }

        world.MeshAllNow();
        return world;
    }
}

/// <summary>Reads back what the world drew, in world-local space.</summary>
internal static class DrawnGeometry
{
    public readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C, Vector3 NormalA, bool Raw)
    {
        /// <summary>The normal Godot's clockwise front face implies.</summary>
        public Vector3 Facing => (C - A).Cross(B - A);

        public Vector3 Centre => (A + B + C) / 3f;
    }

    public static List<Triangle> Triangles(NodeWorld world)
    {
        var all = new List<Triangle>();

        foreach ((Vector3 origin, ArrayMesh mesh) in world.SectionMeshes)
        {
            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                bool raw = mesh.SurfaceGetMaterial(s) == NodeMaterials.Raw;
                var arrays = mesh.SurfaceGetArrays(s);
                Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                Vector3[] normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                int[] indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();

                for (int i = 0; i < indices.Length; i += 3)
                {
                    all.Add(new Triangle(
                        origin + vertices[indices[i]],
                        origin + vertices[indices[i + 1]],
                        origin + vertices[indices[i + 2]],
                        normals[indices[i]], raw));
                }
            }
        }

        return all;
    }

    /// <summary>Is a point well inside the bed, away from the unloaded border?</summary>
    public static bool Interior(Vector3 p, float margin = 40f) =>
        Math.Abs(p.X) < margin && Math.Abs(p.Z) < margin;
}
