using Godot;

namespace GameBase.Terrain;

/// <summary>
/// The shape of a world, and nothing else: where the ground is, not what it is
/// made of (that is the generator's call; see <c>PlanetGenerator</c>).
///
/// A shape is a SIGNED DISTANCE FIELD in the world's local space: positive
/// inside the ground, negative in the air, zero on the surface, in world units.
/// It need not be an exact distance -- steep ground and warped rock are not --
/// but it must be continuous and change no faster than <see cref="Slope"/>
/// units per unit moved, which is what lets whole blocks of it be skipped when
/// a few samples show the surface is far away.
///
/// Called from worker threads: implementations must be pure functions with no
/// shared mutable state.
/// </summary>
public interface ITerrainShape
{
    /// <summary>The signed distance at a point: positive in the ground.</summary>
    double Distance(double x, double y, double z);

    /// <summary>The radius of the plain sphere the terrain is built on: flat ground sits here.</summary>
    double BaseRadius { get; }

    /// <summary>Nothing solid lies further from the centre than this.</summary>
    double Top { get; }

    /// <summary>
    /// Everything nearer the centre than this is solid, and buried deeper than
    /// any topsoil, so it is plain rock.
    /// </summary>
    double Bottom { get; }

    /// <summary>
    /// The most the distance can change per unit moved (its Lipschitz bound).
    /// A true distance field has 1; steep, warped ground more.
    /// </summary>
    double Slope { get; }

    /// <summary>What the terrain is like at a point, for tools, debugging and (later) biomes.</summary>
    TerrainInfo Describe(Vector3 point);

    /// <summary>
    /// The shape as it is inside a box: the same distances there, but
    /// possibly cheaper to measure, and with a <see cref="Slope"/> that holds
    /// inside the box -- which may be infinite, meaning "never skip this box
    /// on a few samples". Features that are many separate pieces (sky islands)
    /// cannot promise a slope everywhere; this lets a shape promise one where
    /// they are not, and say where they are.
    /// </summary>
    ITerrainShape Within(Vector3 min, Vector3 max) => this;

    /// <summary>
    /// Does a box hold small, separate features -- sky islands -- that need
    /// finer detail than the ground around them to be seen from afar? A
    /// cheap, generous answer: the far terrain splits such boxes further out.
    /// </summary>
    bool Intricate(Vector3 min, Vector3 max) => false;
}

/// <summary>The kinds of terrain a region can be.</summary>
public enum RegionType
{
    Flat,
    Hills,
    Mountains,

    /// <summary>A broad sunken bowl, well below the plains: a dry lake bed, waiting for water.</summary>
    Basin,

    /// <summary>
    /// Not dealt to regions: reported where a sky-island zone lies over
    /// whatever region was there (see <see cref="SkyIslands"/>).
    /// </summary>
    SkyIslands,

    /// <summary>
    /// Not dealt to regions: reported between the rims of a canyon, whatever
    /// region it cuts through (see <see cref="Canyons"/>).
    /// </summary>
    Canyon,
}

/// <summary>How a mountain range is shaped. Each mountain region picks one.</summary>
public enum MountainCharacter
{
    /// <summary>Ridged chains with sharp crests and standout peaks.</summary>
    Jagged,

    /// <summary>Broad domes and smooth ridges.</summary>
    Rounded,

    /// <summary>
    /// Extra-tall jagged peaks: a craggy massif under a few towering horns,
    /// well above every other range.
    /// </summary>
    Towering,

    /// <summary>Stepped plateaus with sheer walls and caprock lips.</summary>
    Mesa,
}

/// <summary>
/// What the terrain is like at one point: the blend of regions there, and the
/// numbers a biome layer will pick from.
/// </summary>
/// <remarks><see cref="Name"/> says it in words, for readouts.</remarks>
public readonly record struct TerrainInfo(
    int Region,
    RegionType Type,
    MountainCharacter Character,
    float FlatWeight,
    float HillsWeight,
    float MountainWeight,
    float BasinWeight,
    float IslandWeight,
    float HeightAboveSeaLevel,
    float SurfaceHeight)
{
    /// <summary>The kind of terrain in words: "Hills", "Mountains (Towering)", "Sky islands".</summary>
    public string Name => Type switch
    {
        RegionType.Mountains => $"Mountains ({Character})",
        RegionType.SkyIslands => "Sky islands",
        _ => Type.ToString(),
    };

    /// <summary>The blend of region types, in words, strongest first, leaving out what is not there.</summary>
    public string Blend
    {
        get
        {
            var parts = new System.Collections.Generic.List<(float Weight, string Name)>
            {
                (FlatWeight, "flat"), (HillsWeight, "hills"), (MountainWeight, "mountains"), (BasinWeight, "basin"),
            };
            parts.RemoveAll(p => p.Weight < 0.005f);
            parts.Sort((a, b) => b.Weight.CompareTo(a.Weight));
            return string.Join(", ", parts.ConvertAll(p => $"{p.Name} {p.Weight:P0}"));
        }
    }
}
