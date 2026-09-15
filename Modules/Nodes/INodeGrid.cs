using Godot;
using System;

namespace GameBase.Nodes;

/// <summary>
/// What the mesher, the streamer and the store need to know about a world's
/// shape, whatever shape that is.
///
/// Two grids implement this: <see cref="QuadSphereGrid"/>, whose nodes are
/// four-walled arcs on a cubed sphere, and <see cref="IcoSphereGrid"/>, whose
/// nodes are five- or six-walled prisms on an icosphere. They disagree about
/// almost everything -- how many walls a node has, how a face fold is crossed,
/// how resolution falls toward the core -- and the point of this interface is
/// that nothing above it has to care.
///
/// THE ONE IDEA THAT MAKES BOTH FIT
///
/// A node is a PRISM: some polygon swept between two radii. The top and bottom
/// faces are that polygon at the outer and inner radius, and each side wall is
/// the sweep of one of its edges. A cubed-sphere arc is the four-sided case of
/// exactly that, so the quad grid is not being bent to fit -- it was always a
/// prism with a square cross-section.
///
/// That gives a single description of a node's geometry that both grids can
/// answer: how many walls, where the corners are, and which node lies past each
/// wall. Everything the mesher does follows from those three.
/// </summary>
public interface INodeGrid
{
    // ---------------------------------------------------------------- extent

    /// <summary>Distance from the centre to the surface.</summary>
    float SurfaceRadius { get; }

    /// <summary>How thick one node is, radially.</summary>
    float NodeSize { get; }

    /// <summary>Shells of rock from the surface to the core.</summary>
    int ShellCount { get; }

    /// <summary>Where the planet's centre sits, in the world's own space.</summary>
    Vector3 Origin { get; }

    // -------------------------------------------------------------- identity

    /// <summary>
    /// Is this an address the grid actually contains?
    ///
    /// The bound that matters most is the shell: above it is sky and below it
    /// is solid core, and the chunk store cannot say so for itself -- a
    /// negative index there wraps into a neighbouring chunk and reports open
    /// air as rock.
    /// </summary>
    bool Contains(Vector3I cell);

    /// <summary>
    /// The one address a node is known by.
    ///
    /// Some grids can spell the same node more than one way -- a site on the
    /// seam between two faces belongs to both. Storage is keyed by address, so
    /// without a single agreed spelling a block dug under one name stays solid
    /// under another. A grid with unambiguous addresses returns the cell
    /// unchanged.
    /// </summary>
    Vector3I Canonical(Vector3I cell);

    // -------------------------------------------------------------- geometry

    /// <summary>The world position of a node's centre.</summary>
    Vector3 CentreOf(Vector3I cell);

    /// <summary>The direction a node calls UP: away from the centre.</summary>
    Vector3 UpAt(Vector3I cell);

    /// <summary>The node containing a world point, with a shell outside
    /// 0..ShellCount when the point is off the grid.</summary>
    Vector3I CellAt(Vector3 point);

    // ----------------------------------------------------------------- walls

    /// <summary>
    /// The most side walls any node of this grid has.
    ///
    /// Callers size their scratch by this, so it must be an upper bound rather
    /// than a typical value.
    /// </summary>
    int MaxWalls { get; }

    /// <summary>
    /// How many side walls this particular node has.
    ///
    /// Varies per node on an icosphere: six almost everywhere, five at the
    /// twelve pentagons.
    /// </summary>
    int WallCount(Vector3I cell);

    /// <summary>
    /// The corners of a node's outer face, counter-clockwise seen from outside.
    ///
    /// Returns how many were written. Corner `w` and corner `w + 1` are the two
    /// ends of wall `w`, so the corner order and the wall order are the same
    /// order -- which is what lets a caller build a wall without asking twice.
    /// </summary>
    int TopCorners(Vector3I cell, Span<Vector3> corners);

    /// <summary>The corners of a node's inner face, in the same order.</summary>
    int BottomCorners(Vector3I cell, Span<Vector3> corners);

    /// <summary>
    /// The node past one of this node's side walls.
    ///
    /// `wall` runs 0..WallCount-1 and matches the corner order. Returns false
    /// when there is nothing there.
    /// </summary>
    bool WallNeighbour(Vector3I cell, int wall, out Vector3I result);

    /// <summary>
    /// The node one shell outward or inward.
    ///
    /// Separated from the side walls because the radial direction is the one
    /// every grid shares, and because the mesher answers it differently at the
    /// edges: outward past the surface is sky, inward past the core is rock.
    /// </summary>
    bool RadialNeighbour(Vector3I cell, int dOut, out Vector3I result);

    // -------------------------------------------------------------- chunking

    /// <summary>
    /// The chunk one step away in a direction, for the streamer's flood fill.
    ///
    /// Asked of the grid rather than added to the coordinate because a step off
    /// a face lands on another face, where the axes may be permuted.
    /// </summary>
    bool NeighbourChunk(Vector3I chunk, Vector3I direction, out Vector3I result);
}

/// <summary>
/// A grid whose nodes are not prisms.
///
/// <see cref="INodeGrid"/> describes a node as a polygon swept between two
/// radii, which fits a cubed-sphere arc and a hexagonal prism exactly. An
/// organic Voronoi cell is neither: it has a dozen-odd faces pointing in every
/// direction, with no two of them distinguished as top and bottom.
///
/// So a grid that cannot be described as a prism implements this instead, and
/// the mesher asks for the faces directly. The prism calls still have to work
/// -- collision, picking and the block highlight use them -- but they are
/// approximations there, and geometry comes from here.
/// </summary>
public interface IPolyhedralGrid
{
    /// <summary>
    /// Every face of a node.
    ///
    /// Fills <paramref name="walls"/> with the neighbour behind each face,
    /// <paramref name="sides"/> with how many corners that face has, and
    /// <paramref name="corners"/> with the corners themselves, face after face.
    /// Returns how many faces were written.
    ///
    /// A face whose neighbour is the node ITSELF is a boundary face -- the
    /// planet's surface -- which no neighbour can ever cover, so the mesher
    /// always draws it.
    /// </summary>
    int Faces(Vector3I cell, Span<Vector3I> walls, Span<int> sides,
        Span<Vector3> corners);

    /// <summary>The most corners all of a node's faces can total.</summary>
    int MaxFaceCorners { get; }
}
