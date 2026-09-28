using Godot;
using System.Collections.Generic;

namespace GameBase.Nodes.Meshing;

/// <summary>
/// One surface's worth of geometry being built: render vertices plus the
/// triangle soup its collision shape is made from.
///
/// Reused across builds -- the lists keep their capacity -- so a worker that
/// meshes section after section stops allocating after the first few.
/// </summary>
internal sealed class MeshBuffers
{
    public readonly List<Vector3> Vertices = new();
    public readonly List<Vector3> Normals = new();
    public readonly List<Color> Colors = new();
    public readonly List<int> Indices = new();

    /// <summary>Three entries per triangle, in the same space as the vertices.</summary>
    public readonly List<Vector3> Collision = new();

    public bool IsEmpty => Indices.Count == 0;

    public void Clear()
    {
        Vertices.Clear();
        Normals.Clear();
        Colors.Clear();
        Indices.Clear();
        Collision.Clear();
    }

    public int AddVertex(Vector3 position, Vector3 normal, Color color)
    {
        Vertices.Add(position);
        Normals.Add(normal);
        Colors.Add(color);
        return Vertices.Count - 1;
    }

    /// <summary>
    /// Adds a triangle by vertex index, wound the way Godot draws as the front:
    /// CLOCKWISE seen from the side it faces.
    /// </summary>
    public void AddTriangle(int a, int b, int c)
    {
        Indices.Add(a);
        Indices.Add(b);
        Indices.Add(c);

        Collision.Add(Vertices[a]);
        Collision.Add(Vertices[b]);
        Collision.Add(Vertices[c]);
    }

    /// <summary>The geometry as the array layout <see cref="ArrayMesh"/> takes.</summary>
    public Godot.Collections.Array ToArrays()
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = Vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = Normals.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = Colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = Indices.ToArray();
        return arrays;
    }
}

/// <summary>
/// Everything one section build produces: a surface for the raw nodes and one
/// per particle material present, all in section-local space.
/// </summary>
internal sealed class SectionGeometry
{
    public Vector3I Section;
    public long Ticket;
    public int Generation;

    public readonly MeshBuffers Raw = new();

    private readonly MeshBuffers[] _particles = new MeshBuffers[256];
    private readonly List<byte> _particleMaterials = new();

    /// <summary>The particle materials that produced geometry, in first-seen order.</summary>
    public IReadOnlyList<byte> ParticleMaterials => _particleMaterials;

    public MeshBuffers Particle(byte material)
    {
        MeshBuffers buffers = _particles[material];

        if (buffers == null)
        {
            buffers = new MeshBuffers();
            _particles[material] = buffers;
        }

        if (!_particleMaterials.Contains(material))
            _particleMaterials.Add(material);

        return buffers;
    }

    public bool IsEmpty
    {
        get
        {
            if (!Raw.IsEmpty)
                return false;

            foreach (byte material in _particleMaterials)
            {
                if (!_particles[material].IsEmpty)
                    return false;
            }

            return true;
        }
    }

    public void Clear()
    {
        Raw.Clear();

        foreach (byte material in _particleMaterials)
            _particles[material].Clear();

        _particleMaterials.Clear();
    }
}
