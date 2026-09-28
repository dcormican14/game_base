using Godot;
using System;
using GameBase.Nodes;

namespace GameBase.Tests;

/// <summary>The chunk store and the fill encoding.</summary>
public sealed class StoreTests : TestSuite
{
    [Test]
    public void WritesReadBack()
    {
        var store = new NodeChunkStore();
        var cell = new Vector3I(-5, 40, 1000);

        Check(!store.TryRead(cell, out _, out _), "an unloaded cell reads as known");

        Check(store.Set(cell, (byte)NodeMaterial.Sand, 200), "the first write changed nothing");
        Check(!store.Set(cell, (byte)NodeMaterial.Sand, 200), "an identical write reported a change");

        Check(store.TryRead(cell, out byte material, out byte fill), "a written cell reads as unknown");
        Equal(material, (byte)NodeMaterial.Sand, "material");
        Equal(fill, (byte)200, "fill");
        Equal(store.MaterialAt(cell + Vector3I.Right), NodeChunkStore.Air, "the neighbour");
        Equal(store.NodeCount, 1L, "node count");
    }

    [Test]
    public void UniformChunksCollapse()
    {
        var store = new NodeChunkStore();
        var materials = new byte[NodeChunkStore.ChunkVolume];
        var fills = new byte[NodeChunkStore.ChunkVolume];
        Array.Fill(fills, NodeFill.Full);

        store.Install(Vector3I.Zero, materials, fills);
        NodeChunkStore.Chunk chunk = store.Find(Vector3I.Zero);

        Check(chunk.IsUniform, "a solid chunk kept its arrays");
        Equal(chunk.SolidCount, NodeChunkStore.ChunkVolume, "solid count");

        // Breaking uniformity keeps every other cell as it was.
        store.Set(new Vector3I(3, 4, 5), NodeChunkStore.Air, NodeFill.Empty);
        Check(!chunk.IsUniform, "an edited chunk stayed uniform");
        Equal(store.MaterialAt(new Vector3I(6, 4, 5)), (byte)NodeMaterial.Stone, "an untouched cell");
        Equal(chunk.SolidCount, NodeChunkStore.ChunkVolume - 1, "solid count after a dig");
    }

    [Test]
    public void UnloadForgets()
    {
        var store = new NodeChunkStore();
        store.Set(Vector3I.One, (byte)NodeMaterial.Stone, NodeFill.Full);

        Check(store.Unload(Vector3I.Zero), "unload found nothing");
        Check(!store.TryRead(Vector3I.One, out _, out _), "a dropped chunk still reads");
    }

    [Test]
    public void FillRoundTrips()
    {
        for (float level = -1f; level <= 1f; level += 0.01f)
            Near(NodeFill.ToLevel(NodeFill.FromLevel(level)), level, 1f / 127f, $"level {level}");

        Equal(NodeFill.FromLevel(-5f), NodeFill.Empty, "far outside");
        Equal(NodeFill.FromLevel(5f), NodeFill.Full, "far inside");
        Check(NodeFill.IsInside(NodeFill.FromLevel(0.01f)), "just inside reads as outside");
        Check(!NodeFill.IsInside(NodeFill.FromLevel(-0.01f)), "just outside reads as inside");
    }

    [Test]
    public void TypesKnowTheirForm()
    {
        Check(NodeTypes.IsRaw((byte)NodeMaterial.Stone), "stone is not raw");
        Check(NodeTypes.IsParticle((byte)NodeMaterial.Sand), "sand is not particle");
        Check(!NodeTypes.IsRaw(NodeChunkStore.Air) && !NodeTypes.IsParticle(NodeChunkStore.Air), "air has a form");
        Check(NodeTypes.Of(NodeChunkStore.Air) == null, "air has a type");
        Check(NodeTypes.Of(NodeMaterial.Sand) is ParticleNode, "sand is not a ParticleNode");
    }
}
