using Godot;

namespace GameBase.Nodes.Meshing;

/// <summary>
/// A section's cells and a border around them, read out of the store once so
/// the meshers work from flat local arrays instead of a chunk lookup per
/// neighbour.
///
/// The border is two cells deep. The meshers themselves reach one cell out --
/// a raw node's faces border its lattice neighbours, and the particle surface
/// through a cell depends on the cells either side -- and deciding whether a
/// border cell hides a rock face means looking one step further again.
///
/// A cell whose chunk is not loaded is recorded as <see cref="Unknown"/>, which
/// every classification below treats as SOLID and COVERING. That is the safe
/// way round: guessing solid hides a face until the neighbour arrives and the
/// section is rebuilt, while guessing air would draw a face into the unknown --
/// a window into the planet that nothing later takes back.
/// </summary>
internal sealed class SectionSample
{
    /// <summary>Cells along a section edge.</summary>
    public const int Size = 8;

    /// <summary>How many cells of border are sampled on each side.</summary>
    public const int Border = 2;

    /// <summary>Samples along an edge.</summary>
    public const int Span = Size + Border * 2;

    /// <summary>The material recorded for a cell whose chunk is not loaded.</summary>
    public const byte Unknown = 254;

    public readonly byte[] Materials = new byte[Span * Span * Span];
    public readonly byte[] Fills = new byte[Span * Span * Span];

    /// <summary>
    /// How far each sample's site sits from its lattice point. The particle
    /// mesher measures the shell's crossings between sites where it meets rock,
    /// because the midpoint of two sites lies on the wall between them. Kept
    /// apart from the lattice point, rather than as a position in the section,
    /// so that two sections building the same vertex do the same arithmetic.
    /// </summary>
    public readonly Vector3[] Jitters = new Vector3[Span * Span * Span];

    /// <summary>Whether each sample hides a rock face turned toward it.</summary>
    private readonly bool[] _covers = new bool[Span * Span * Span];


    /// <summary>The section's lowest cell.</summary>
    public Vector3I Origin { get; private set; }

    /// <summary>Does any sample hold a particle node?</summary>
    public bool HasParticles { get; private set; }

    /// <summary>
    /// Could any raw node in the section have a visible face? False when every
    /// sample is solid rock or unknown, which is the whole interior of a planet.
    /// </summary>
    public bool HasOpenings { get; private set; }

    /// <summary>The flat index of a sample, in section-local coordinates -Border..Size+Border-1.</summary>
    public static int Index(int x, int y, int z) =>
        ((x + Border) * Span + (y + Border)) * Span + (z + Border);

    public void Read(NodeChunkStore store, VoronoiGrid grid, Vector3I origin)
    {
        Origin = origin;
        HasParticles = false;
        HasOpenings = false;

        const int Low = -Border, High = Size + Border;

        for (int x = Low; x < High; x++)
        for (int y = Low; y < High; y++)
        for (int z = Low; z < High; z++)
        {
            int i = Index(x, y, z);
            var cell = new Vector3I(origin.X + x, origin.Y + y, origin.Z + z);

            if (!store.TryRead(cell, out byte material, out byte fill))
                material = Unknown;

            Materials[i] = material;
            Fills[i] = fill;
            Jitters[i] = grid.JitterOf(cell);

            if (NodeTypes.IsParticle(material))
                HasParticles = true;

            if (!IsRawOrUnknown(material))
                HasOpenings = true;
        }

        if (HasOpenings)
            FindCovers();
    }

    /// <summary>
    /// Works out which samples hide a rock face. Rock and unknown always do. A
    /// particle cell does only when it is full AND nothing around it -- none of
    /// its 26 neighbours -- is open.
    ///
    /// The particle surface and the rock's faces are built by different rules
    /// and never meet exactly, so near a particle surface there are hairline
    /// seams between the two. A rock face hidden behind particle nodes that are close to
    /// open air leaves nothing behind such a seam, and the view goes straight
    /// through the planet to the sky. Checking only the six face neighbours
    /// missed air touching a cell at a corner, and a ray found its way through.
    /// </summary>
    private void FindCovers()
    {
        const int Low = -Border + 1, High = Size + Border - 1;

        System.Array.Clear(_covers);

        for (int x = Low; x < High; x++)
        for (int y = Low; y < High; y++)
        for (int z = Low; z < High; z++)
        {
            int i = Index(x, y, z);
            byte material = Materials[i];

            _covers[i] = IsRawOrUnknown(material)
                || (NodeTypes.IsParticle(material) && Fills[i] == NodeFill.Full && !NearOpen(x, y, z));
        }
    }

    /// <summary>Is any of a sample's 26 neighbours open: air, or a particle cell the surface lies below?</summary>
    private bool NearOpen(int x, int y, int z)
    {
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++)
        {
            int i = Index(x + dx, y + dy, z + dz);
            byte material = Materials[i];

            if (material == NodeChunkStore.Air
                || (NodeTypes.IsParticle(material) && NodeFill.ToLevel(Fills[i]) <= 0f))
                return true;
        }

        return false;
    }

    public static bool IsRawOrUnknown(byte material) =>
        material == Unknown || NodeTypes.IsRaw(material);

    /// <summary>Does the cell at this sample hide a raw face turned toward it?</summary>
    public bool Covers(int index) => _covers[index];

    /// <summary>
    /// The level a raw node takes in the particle field: just outside the
    /// particle surface.
    ///
    /// Particle nodes are their own shell, not a coat of paint on the rock, so
    /// a raw node is OUTSIDE them -- mine the rock from under the shell and the
    /// shell stays where it was, rounded underneath, rather than spreading down
    /// the new walls. But only just outside: along an edge from a particle cell
    /// the surface crosses at level / (level - RawLevel) of the way to the raw
    /// node's site. From a full cell that is most of the way in, so the shell's
    /// cloud sinks well into the rock and never leaves a seam; from a thin one
    /// it stops short of the wall and the rock stands through. It never
    /// reaches the site itself -- the core of the node.
    /// </summary>
    public const float RawLevel = -0.25f;

    /// <summary>
    /// The signed distance to the particle surface at a sample, in nodes: air
    /// is fully outside, raw nodes just outside (<see cref="RawLevel"/>), and
    /// the unknown fully inside, so no surface is ever drawn into it.
    /// </summary>
    public float Level(int index)
    {
        byte material = Materials[index];

        if (material == Unknown)
            return NodeFill.Range;

        if (NodeTypes.IsRaw(material))
            return RawLevel;

        return material == NodeChunkStore.Air ? -NodeFill.Range : NodeFill.ToLevel(Fills[index]);
    }
}
