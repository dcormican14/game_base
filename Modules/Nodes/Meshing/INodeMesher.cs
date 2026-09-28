namespace GameBase.Nodes.Meshing;

/// <summary>
/// Turns one form of node into geometry for a section.
///
/// One implementation per <see cref="NodeForm"/>. Both run over the same
/// <see cref="SectionSample"/> and write into the same
/// <see cref="SectionGeometry"/>, in section-local space (relative to the
/// section's lowest lattice point), so the world can build a section by running
/// every mesher in turn without knowing what any of them does.
///
/// Implementations must be pure computation -- no engine objects -- because
/// sections are built on worker threads.
/// </summary>
internal interface INodeMesher
{
    NodeForm Form { get; }

    void Build(SectionSample sample, VoronoiGrid grid, SectionGeometry output);
}
