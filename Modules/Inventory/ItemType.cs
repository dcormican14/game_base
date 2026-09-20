using Godot;

namespace GameBase.Items;

/// <summary>
/// One kind of thing that can sit in a slot: its name, how it stacks, and the
/// mesh drawn for its icon.
///
/// A Resource rather than a C# class so items can be authored as .tres files
/// in the editor and referenced from scenes without touching code — the same
/// arrangement NodeType uses for terrain. Slots hold a reference to the shared
/// instance, so an item's definition exists once no matter how many stacks of
/// it are carried.
///
/// The icon is a MESH rather than a texture on purpose. Items are rendered as
/// real geometry through <see cref="ItemIcon"/>, which lights and pixelates
/// them the way the world pixelates its objects, so a cube in the hand and the
/// same cube in a slot are recognisably the same object. Authoring flat icons
/// instead would mean maintaining a second, hand-drawn depiction of every item
/// and keeping the two in sync.
/// </summary>
[GlobalClass]
public partial class ItemType : Resource
{
    /// <summary>Stable identifier, for saves and for looking an item up in code.</summary>
    [Export] public string Id { get; set; } = "";

    /// <summary>Name shown to the player in tooltips and labels.</summary>
    [Export] public string DisplayName { get; set; } = "";

    /// <summary>
    /// Most items the player can hold this many of in one slot. 1 means the
    /// item never stacks, which is the right default for tools and equipment:
    /// an item that silently merges with another is a surprise, an item that
    /// refuses to is merely a full slot.
    /// </summary>
    [Export(PropertyHint.Range, "1,999,1")] public int MaxStack { get; set; } = 1;

    /// <summary>
    /// Shapes <see cref="ItemType"/> can build for itself, for items whose
    /// geometry is several blocks welded together and so cannot be authored as
    /// one of Godot's primitive mesh resources.
    /// </summary>
    public enum IconShape
    {
        /// <summary>Use <see cref="IconMesh"/> as authored.</summary>
        Mesh,
        Shovel,
        Pickaxe,
    }

    /// <summary>
    /// Which shape to draw. <see cref="IconShape.Mesh"/> — the default — uses
    /// whatever <see cref="IconMesh"/> holds, so simple items stay authored
    /// entirely in the .tres file; the others are built by
    /// <see cref="ToolMesh"/> from the two colours below.
    /// </summary>
    [Export] public IconShape Shape { get; set; } = IconShape.Mesh;

    /// <summary>Handle colour for a generated tool shape.</summary>
    [Export] public Color HandleColor { get; set; } = new(0.42f, 0.27f, 0.15f);

    /// <summary>Head colour for a generated tool shape.</summary>
    [Export] public Color HeadColor { get; set; } = new(0.62f, 0.64f, 0.68f);

    /// <summary>Geometry drawn into the slot icon. Null slots render empty.</summary>
    [Export] public Mesh IconMesh { get; set; }

    /// <summary>
    /// The geometry to actually draw: the generated shape when one is
    /// selected, otherwise the authored mesh.
    ///
    /// Built once and kept, because every slot holding this item asks for it
    /// and the answer never changes — and because the icons re-render whenever
    /// a slot's contents change, which would otherwise rebuild the same
    /// geometry on every swap.
    /// </summary>
    public Mesh ResolvedIconMesh => Shape switch
    {
        IconShape.Shovel => _generated ??= ToolMesh.Shovel(HandleColor, HeadColor),
        IconShape.Pickaxe => _generated ??= ToolMesh.Pickaxe(HandleColor, HeadColor),
        _ => IconMesh,
    };

    private Mesh _generated;

    /// <summary>
    /// Orientation the icon mesh is shown at, in degrees. The default turns
    /// the mesh away from face-on so a cube reads as a solid with three
    /// visible faces rather than as a flat square.
    /// </summary>
    [Export] public Vector3 IconRotation { get; set; } = new(-20f, -35f, 0f);

    /// <summary>
    /// Fine adjustment on top of the automatic framing. The icon already fits
    /// itself to the mesh's rotated bounds, so 1 is correct whatever size the
    /// geometry is; this is only for making an item deliberately read as
    /// larger (above 1) or smaller (below 1) than its neighbours.
    /// </summary>
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float IconZoom { get; set; } = 1f;
}
