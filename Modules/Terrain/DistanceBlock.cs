using System;
using Godot;

namespace GameBase.Terrain;

/// <summary>
/// A terrain shape sampled on a regular lattice over a box, with its gradient,
/// and read back anywhere inside by interpolation.
///
/// WHY SAMPLE. A shape is expensive -- dozens of noise lookups a point -- and a
/// chunk has 32,768 cells, each needing the distance at two points. Sampled
/// every other cell and interpolated, a chunk costs a few thousand lookups
/// instead, and the far terrain's first ring, sampling on the SAME lattice,
/// measures exactly the same ground, so the two agree where one hands over to
/// the other.
///
/// WHY THE GRADIENT. A shape's distance is only a true distance on flat
/// ground; on a 60-degree slope it overstates by half again, on a cliff by
/// far more. Dividing by the gradient's length turns it back into a true
/// distance (<see cref="Normalized"/>), which is what the particle fill must
/// hold -- see NodeFill: the mesher, the shovel and Level all assume
/// neighbouring cells agree on how far the surface is. The gradient also
/// says which way the ground faces, which decides where sand lies.
///
/// Samples sit at whole multiples of the spacing from the origin, so any two
/// blocks with the same spacing share their sample points exactly, whatever
/// box each covers.
/// </summary>
public sealed class DistanceBlock
{
    private float[] _values = Array.Empty<float>();
    private float[] _gx = Array.Empty<float>(), _gy = Array.Empty<float>(), _gz = Array.Empty<float>();

    /// <summary>World units between samples.</summary>
    public double Spacing { get; private set; }

    /// <summary>Sample index (world position / spacing) of the first sample with a gradient.</summary>
    public Vector3I Low { get; private set; }

    /// <summary>Sample index of the last sample with a gradient.</summary>
    public Vector3I High { get; private set; }

    // The values reach one sample further each way than the gradients, which
    // are central differences.
    private Vector3I _valueLow;
    private int _sx, _sy, _sz;

    /// <summary>
    /// Samples a shape at every lattice point from <paramref name="low"/> to
    /// <paramref name="high"/> (sample indices, inclusive), with gradients.
    /// </summary>
    public void Fill(ITerrainShape shape, double spacing, Vector3I low, Vector3I high)
    {
        // Only what can reach the box need be measured.
        shape = shape.Within(
            new Vector3(low.X - 1, low.Y - 1, low.Z - 1) * (float)spacing,
            new Vector3(high.X + 1, high.Y + 1, high.Z + 1) * (float)spacing);

        Spacing = spacing;
        Low = low;
        High = high;
        _valueLow = low - Vector3I.One;
        _sx = high.X - low.X + 3;
        _sy = high.Y - low.Y + 3;
        _sz = high.Z - low.Z + 3;

        int volume = _sx * _sy * _sz;
        if (_values.Length < volume)
        {
            _values = new float[volume];
            _gx = new float[volume];
            _gy = new float[volume];
            _gz = new float[volume];
        }

        for (int x = 0; x < _sx; x++)
        for (int y = 0; y < _sy; y++)
        for (int z = 0; z < _sz; z++)
        {
            _values[Index(x, y, z)] = (float)shape.Distance(
                (_valueLow.X + x) * spacing, (_valueLow.Y + y) * spacing, (_valueLow.Z + z) * spacing);
        }

        float twice = (float)(2 * spacing);

        for (int x = 1; x < _sx - 1; x++)
        for (int y = 1; y < _sy - 1; y++)
        for (int z = 1; z < _sz - 1; z++)
        {
            int i = Index(x, y, z);
            _gx[i] = (_values[Index(x + 1, y, z)] - _values[Index(x - 1, y, z)]) / twice;
            _gy[i] = (_values[Index(x, y + 1, z)] - _values[Index(x, y - 1, z)]) / twice;
            _gz[i] = (_values[Index(x, y, z + 1)] - _values[Index(x, y, z - 1)]) / twice;
        }
    }

    /// <summary>Samples a shape over a world-space box, at a spacing, with room to interpolate anywhere inside.</summary>
    public void FillBox(ITerrainShape shape, double spacing, Vector3 min, Vector3 max)
    {
        var low = new Vector3I(
            (int)Math.Floor(min.X / spacing), (int)Math.Floor(min.Y / spacing), (int)Math.Floor(min.Z / spacing));
        var high = new Vector3I(
            (int)Math.Floor(max.X / spacing) + 1, (int)Math.Floor(max.Y / spacing) + 1, (int)Math.Floor(max.Z / spacing) + 1);

        Fill(shape, spacing, low, high);
    }

    /// <summary>The distance at a sample (by sample index; values reach one past Low..High).</summary>
    public float Value(int x, int y, int z) =>
        _values[Index(x - _valueLow.X, y - _valueLow.Y, z - _valueLow.Z)];

    /// <summary>The gradient at a sample (by sample index, within Low..High).</summary>
    public Vector3 Gradient(int x, int y, int z)
    {
        int i = Index(x - _valueLow.X, y - _valueLow.Y, z - _valueLow.Z);
        return new Vector3(_gx[i], _gy[i], _gz[i]);
    }

    /// <summary>The distance at a point, interpolated between samples.</summary>
    public float Distance(Vector3 point)
    {
        Locate(point, out int i, out float tx, out float ty, out float tz);
        return Lerp3(_values, i, tx, ty, tz);
    }

    /// <summary>The gradient at a point, interpolated between samples.</summary>
    public Vector3 GradientAt(Vector3 point)
    {
        Locate(point, out int i, out float tx, out float ty, out float tz);
        return new Vector3(Lerp3(_gx, i, tx, ty, tz), Lerp3(_gy, i, tx, ty, tz), Lerp3(_gz, i, tx, ty, tz));
    }

    /// <summary>
    /// The TRUE distance at a point -- the sampled distance over the length of
    /// its gradient -- and the gradient itself.
    /// </summary>
    public float Normalized(Vector3 point, out Vector3 gradient)
    {
        Locate(point, out int i, out float tx, out float ty, out float tz);
        gradient = new Vector3(Lerp3(_gx, i, tx, ty, tz), Lerp3(_gy, i, tx, ty, tz), Lerp3(_gz, i, tx, ty, tz));
        return Lerp3(_values, i, tx, ty, tz) / Mathf.Max(gradient.Length(), MinGradient);
    }

    /// <summary>
    /// The least gradient a distance is divided by. Warped ground can flatten
    /// the field in places; dividing by almost nothing would turn a small
    /// distance into a huge one.
    /// </summary>
    public const float MinGradient = 0.5f;

    private void Locate(Vector3 point, out int index, out float tx, out float ty, out float tz)
    {
        double fx = point.X / Spacing, fy = point.Y / Spacing, fz = point.Z / Spacing;
        double ix = Math.Floor(fx), iy = Math.Floor(fy), iz = Math.Floor(fz);

        int x = Math.Clamp((int)ix - _valueLow.X, 1, _sx - 3);
        int y = Math.Clamp((int)iy - _valueLow.Y, 1, _sy - 3);
        int z = Math.Clamp((int)iz - _valueLow.Z, 1, _sz - 3);

        tx = (float)Math.Clamp(fx - (x + _valueLow.X), 0, 1);
        ty = (float)Math.Clamp(fy - (y + _valueLow.Y), 0, 1);
        tz = (float)Math.Clamp(fz - (z + _valueLow.Z), 0, 1);
        index = Index(x, y, z);
    }

    private float Lerp3(float[] field, int i, float tx, float ty, float tz)
    {
        int dx = _sy * _sz, dy = _sz;

        float c00 = Mathf.Lerp(field[i], field[i + dx], tx);
        float c10 = Mathf.Lerp(field[i + dy], field[i + dx + dy], tx);
        float c01 = Mathf.Lerp(field[i + 1], field[i + dx + 1], tx);
        float c11 = Mathf.Lerp(field[i + dy + 1], field[i + dx + dy + 1], tx);

        return Mathf.Lerp(Mathf.Lerp(c00, c10, ty), Mathf.Lerp(c01, c11, ty), tz);
    }

    private int Index(int x, int y, int z) => (x * _sy + y) * _sz + z;

    /// <summary>
    /// Is a whole box one side of the surface, by a margin? Samples a coarse
    /// lattice over it and uses the shape's <see cref="ITerrainShape.Slope"/>:
    /// a point can differ from its nearest sample by at most the slope times
    /// the distance between them.
    /// </summary>
    /// <param name="airMargin">How far clear of the surface, in TRUE distance, an all-air box must be.</param>
    /// <param name="solidMargin">How deep below the surface, in TRUE distance, an all-solid box must be.</param>
    /// <param name="steps">Samples along each side, less one.</param>
    /// <returns>+1 all solid, -1 all air, 0 mixed.</returns>
    public static int Uniform(ITerrainShape shape, Vector3 min, Vector3 max,
        double airMargin, double solidMargin, int steps = 4)
    {
        shape = shape.Within(min, max);
        if (double.IsInfinity(shape.Slope))
            return 0;

        // A raw distance is at most the slope times the true one, so a true
        // margin is that many times more in raw distance.
        Vector3 size = max - min;
        double between = shape.Slope * size.Length() / steps * 0.5;
        double air = between + shape.Slope * airMargin;
        double solid = between + shape.Slope * solidMargin;
        int sign = 0;

        for (int x = 0; x <= steps; x++)
        for (int y = 0; y <= steps; y++)
        for (int z = 0; z <= steps; z++)
        {
            double d = shape.Distance(
                min.X + (double)size.X * x / steps,
                min.Y + (double)size.Y * y / steps,
                min.Z + (double)size.Z * z / steps);

            int side = d > solid ? 1 : d < -air ? -1 : 0;
            if (side == 0 || (sign != 0 && side != sign))
                return 0;

            sign = side;
        }

        return sign;
    }
}
