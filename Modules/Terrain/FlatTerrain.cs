using System;
using Godot;

namespace GameBase.Terrain;

/// <summary>
/// A perfectly round world: the sphere the planet was before it had terrain.
///
/// Kept as a shape of its own because a known, featureless surface is what the
/// tests measure against -- sand exactly at the radius, rock exactly beneath --
/// and it is still the right world for anything that wants no terrain at all.
/// </summary>
public sealed class FlatTerrain : ITerrainShape
{
    public FlatTerrain(double radius) => BaseRadius = radius;

    public double BaseRadius { get; }

    /// <summary>Sky starts right above the surface.</summary>
    public double Top => BaseRadius;

    /// <summary>Nothing lies below the surface but sand, then rock.</summary>
    public double Bottom => BaseRadius - 64;

    /// <summary>The distance to a sphere is exact.</summary>
    public double Slope => 1;

    public double Distance(double x, double y, double z) => BaseRadius - Math.Sqrt(x * x + y * y + z * z);

    public TerrainInfo Describe(Vector3 point) => new(
        0, RegionType.Flat, MountainCharacter.Jagged, 1f, 0f, 0f, 0f, 0f,
        (float)(point.Length() - BaseRadius), 0f);
}
