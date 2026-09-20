using Godot;

namespace GameBase.Items;

/// <summary>
/// Builds the chunky block geometry the tool icons are drawn from.
///
/// A tool is a handle plus a head, which is two pieces — but
/// <see cref="ItemType.IconMesh"/> holds ONE mesh, because an item's icon is
/// one picture and a list of parts would push posing and material assignment
/// out into every item that has more than one. So the parts are welded into a
/// single ArrayMesh here instead, at author time rather than at render time.
///
/// Everything is built from boxes on purpose. The icons are rasterised into a
/// 32x32 target, where a smoothly tapered axe head and a blocky one resolve to
/// the same handful of texels — and the blocky one reads better, because its
/// edges land on the pixel grid instead of dithering across it. The shapes are
/// drawn for the size they will actually be seen at.
/// </summary>
public static class ToolMesh
{
    /// <summary>
    /// A shovel: a long shaft with a broad, slightly tapered blade at the
    /// bottom. The blade is wider than it is thick so the tool still reads as
    /// a shovel from the three-quarter angle the icons are posed at.
    /// </summary>
    public static ArrayMesh Shovel(Color handleColor, Color headColor)
    {
        var builder = new BoxBuilder();

        // Shaft, running up the icon's vertical axis. Thick relative to its
        // length: at 32 pixels a slender handle falls below one texel and
        // breaks into a dotted line, so it is drawn closer to a haft than to
        // a stick.
        builder.Add(new Vector3(0f, 0.16f, 0f), new Vector3(0.2f, 0.92f, 0.2f), handleColor);

        // Grip at the top, so the handle end does not read as a cut-off stick.
        builder.Add(new Vector3(0f, 0.64f, 0f), new Vector3(0.36f, 0.16f, 0.22f), handleColor);

        // Socket joining blade to shaft.
        builder.Add(new Vector3(0f, -0.32f, 0f), new Vector3(0.3f, 0.16f, 0.24f), headColor);

        // Blade: wide, flat, and stepped at the tip so the digging end is
        // visibly narrower than the shoulder.
        builder.Add(new Vector3(0f, -0.54f, 0f), new Vector3(0.66f, 0.32f, 0.14f), headColor);
        builder.Add(new Vector3(0f, -0.74f, 0f), new Vector3(0.48f, 0.14f, 0.14f), headColor);

        return builder.Build();
    }

    /// <summary>
    /// A pickaxe: the same shaft crossed by a head that sweeps out to a point
    /// on each side. The two arms are stepped rather than angled — at icon
    /// size a true diagonal turns into a staircase anyway, so the steps are
    /// placed deliberately instead of falling where the rasteriser puts them.
    /// </summary>
    public static ArrayMesh Pickaxe(Color handleColor, Color headColor)
    {
        var builder = new BoxBuilder();

        builder.Add(new Vector3(0f, -0.08f, 0f), new Vector3(0.2f, 1.0f, 0.2f), handleColor);
        builder.Add(new Vector3(0f, -0.60f, 0f), new Vector3(0.28f, 0.14f, 0.22f), handleColor);

        // Centre boss where the head meets the shaft.
        builder.Add(new Vector3(0f, 0.44f, 0f), new Vector3(0.34f, 0.24f, 0.26f), headColor);

        // Arms, stepping down and out to a point on both sides. Each step is
        // at least a couple of texels tall at icon size, so the taper reads as
        // a deliberate stair rather than as rasteriser noise.
        for (int side = -1; side <= 1; side += 2)
        {
            builder.Add(new Vector3(0.28f * side, 0.40f, 0f), new Vector3(0.3f, 0.2f, 0.22f), headColor);
            builder.Add(new Vector3(0.52f * side, 0.26f, 0f), new Vector3(0.24f, 0.18f, 0.2f), headColor);
            builder.Add(new Vector3(0.72f * side, 0.08f, 0f), new Vector3(0.18f, 0.16f, 0.18f), headColor);
        }

        return builder.Build();
    }

    /// <summary>
    /// Accumulates axis-aligned boxes into one mesh.
    ///
    /// Colour is carried per vertex rather than per box material, so the whole
    /// tool is a single surface with a single material. That keeps each icon
    /// to one draw call, and means a tool's two colours cost no more to render
    /// than a cube's one.
    /// </summary>
    private sealed class BoxBuilder
    {
        private readonly Godot.Collections.Array _surface = new();
        private readonly Vector3[] _normals =
        {
            Vector3.Right, Vector3.Left, Vector3.Up,
            Vector3.Down, Vector3.Back, Vector3.Forward,
        };

        private readonly System.Collections.Generic.List<Vector3> _vertices = new();
        private readonly System.Collections.Generic.List<Vector3> _vertexNormals = new();
        private readonly System.Collections.Generic.List<Color> _colors = new();
        private readonly System.Collections.Generic.List<int> _indices = new();

        /// <summary>Adds a box centred on <paramref name="centre"/> whose full
        /// width, height and depth are <paramref name="size"/>.</summary>
        public void Add(Vector3 centre, Vector3 size, Color color)
        {
            Vector3 h = size * 0.5f;

            foreach (Vector3 normal in _normals)
            {
                // The face's own axes: any two directions perpendicular to its
                // normal. Which two does not matter for a flat-shaded box, only
                // that they are consistent so the winding stays outward.
                Vector3 u = new(normal.Y, normal.Z, normal.X);
                Vector3 v = normal.Cross(u);

                Vector3 faceCentre = centre + normal * h;
                Vector3 du = u * h;
                Vector3 dv = v * h;

                int start = _vertices.Count;
                _vertices.Add(faceCentre - du - dv);
                _vertices.Add(faceCentre + du - dv);
                _vertices.Add(faceCentre + du + dv);
                _vertices.Add(faceCentre - du + dv);

                for (int i = 0; i < 4; i++)
                {
                    _vertexNormals.Add(normal);
                    _colors.Add(color);
                }

                _indices.Add(start);
                _indices.Add(start + 1);
                _indices.Add(start + 2);
                _indices.Add(start);
                _indices.Add(start + 2);
                _indices.Add(start + 3);
            }
        }

        public ArrayMesh Build()
        {
            _surface.Resize((int)Mesh.ArrayType.Max);
            _surface[(int)Mesh.ArrayType.Vertex] = _vertices.ToArray();
            _surface[(int)Mesh.ArrayType.Normal] = _vertexNormals.ToArray();
            _surface[(int)Mesh.ArrayType.Color] = _colors.ToArray();
            _surface[(int)Mesh.ArrayType.Index] = _indices.ToArray();

            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, _surface);

            // Vertex colour has to be switched on explicitly, or the per-vertex
            // colours above are ignored and the tool renders in flat white.
            mesh.SurfaceSetMaterial(0, new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                Roughness = 0.65f,
            });

            return mesh;
        }
    }
}
