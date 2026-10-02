using System;
using Godot;

namespace GameBase.Terrain;

/// <summary>Questions any terrain shape can answer by probing its distance field.</summary>
public static class TerrainShapes
{
    /// <summary>
    /// How far from the centre the first solid ground is, coming down from the
    /// sky along a direction: the top of whatever is there, whether that is
    /// plain ground, a mesa, a horn's summit or the top of an arch -- never the
    /// ground underneath one.
    /// </summary>
    private const double MaxStep = 4;

    public static double SurfaceRadius(ITerrainShape shape, Vector3 direction)
    {
        Vector3 u = direction.Normalized();
        double ux = u.X, uy = u.Y, uz = u.Z;

        double r = shape.Top + 4;
        double outside = r;

        // Sphere tracing: the distance field says how far the ground can be
        // at most, scaled down by how fast the field may change.
        for (int n = 0; n < 4000 && r > shape.Bottom - 64; n++)
        {
            double d = shape.Distance(ux * r, uy * r, uz * r);

            if (d >= 0)
            {
                // Found ground: close in on the crossing between here and the
                // last point that was air.
                double inside = r;
                for (int b = 0; b < 30; b++)
                {
                    double middle = 0.5 * (inside + outside);
                    if (shape.Distance(ux * middle, uy * middle, uz * middle) >= 0)
                        inside = middle;
                    else
                        outside = middle;
                }

                return 0.5 * (inside + outside);
            }

            // Never more than a few units at a time: a floating island can be
            // thinner than the distance to the ground under it says.
            outside = r;
            r -= Math.Clamp(-d / shape.Slope, 0.25, MaxStep);
        }

        return shape.BaseRadius;
    }

    /// <summary>The point on the ground (see <see cref="SurfaceRadius"/>) along a direction, in the shape's space.</summary>
    public static Vector3 SurfacePoint(ITerrainShape shape, Vector3 direction) =>
        direction.Normalized() * (float)SurfaceRadius(shape, direction);
}
