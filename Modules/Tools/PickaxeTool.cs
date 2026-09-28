using Godot;
using System;
using System.Collections.Generic;
using GameBase.Nodes;
using GameBase.Player;
using GameBase.Tools.Highlight;

namespace GameBase.Tools;

/// <summary>
/// Works raw nodes one at a time: mining takes the node under the crosshair,
/// placing puts one against the face being looked at.
///
/// Its highlight traces the node's every edge and glows on each of its faces,
/// the glow and its particles working in from the face's edges.
/// </summary>
public sealed class PickaxeTool : Tool
{
    public override string Id => "pickaxe";

    public override NodeForm Form => NodeForm.Raw;

    public override ToolCadence Cadence => ToolCadence.Discrete;

    public override float Reach => 6f;

    /// <summary>What a place puts down. Stone until the inventory can say otherwise.</summary>
    public RawNode PlaceType { get; set; } = NodeTypes.Stone;

    /// <summary>The user's capsule, for refusing to place a node inside them.</summary>
    public float UserRadius { get; set; } = 0.35f;

    public float UserHeight { get; set; } = 1.8f;

    public override bool Mine(in ToolContext context, in NodeHit target)
    {
        NodeType type = target.Type;

        if (!MayMine(context, type, 1f) || !context.World.ClearNode(target.Cell))
            return false;

        context.Ledger.Mined(type, 1f);
        return true;
    }

    public override bool Place(in ToolContext context, in NodeHit target)
    {
        Vector3I cell = target.Outside;
        NodeWorld world = context.World;

        // Into air, or displacing loose particles -- never over another solid.
        if (world.TypeAt(cell) is RawNode || !world.IsCellLoaded(cell) || Blocks(context, cell))
            return false;

        if (!MayPlace(context, PlaceType, 1f) || !world.SetNode(cell, PlaceType))
            return false;

        context.Ledger.Placed(PlaceType, 1f);
        return true;
    }

    /// <summary>
    /// Would a node here overlap the user? Tested as the node's bounding sphere
    /// against the user's capsule, standing along their own up -- on a planet
    /// that is not world Y. Free-flying sandbox users are intangible and may
    /// build around themselves.
    /// </summary>
    private bool Blocks(in ToolContext context, Vector3I cell)
    {
        if (context.User == null || context.User is PlayerController { IsSandbox: true })
            return false;

        Vector3 up = context.Up;
        Vector3 feet = context.User.GlobalPosition;
        Vector3 bottom = feet + up * UserRadius;
        Vector3 top = feet + up * (UserHeight - UserRadius);

        Vector3 site = context.World.SiteOf(cell);
        Vector3 axis = top - bottom;
        float t = Mathf.Clamp((site - bottom).Dot(axis) / axis.LengthSquared(), 0f, 1f);
        Vector3 nearest = bottom + axis * t;

        // A Voronoi cell reaches about three quarters of a node from its site.
        return nearest.DistanceTo(site) < UserRadius + context.World.NodeSize * 0.7f;
    }

    public override bool SameOutline(in NodeHit previous, in NodeHit current) =>
        previous.Cell == current.Cell && previous.Type == current.Type;

    public override void Outline(in ToolContext context, in NodeHit target, HighlightBuilder builder)
    {
        NodeWorld world = context.World;

        Span<Vector3I> neighbours = stackalloc Vector3I[VoronoiGrid.MaxFaces];
        Span<int> sides = stackalloc int[VoronoiGrid.MaxFaces];
        Span<Vector3> corners = stackalloc Vector3[VoronoiGrid.MaxFaceCorners];

        int faces = world.Grid.Faces(target.Cell, neighbours, sides, corners);
        int total = 0;
        for (int n = 0; n < faces; n++)
            total += sides[n];

        // Into global space: the grid answers in the world's local space.
        Transform3D toGlobal = world.GlobalTransform;
        for (int c = 0; c < total; c++)
            corners[c] = toGlobal * corners[c];

        Vector3 centre = world.SiteOf(target.Cell);

        var drawn = new HashSet<(Vector3, Vector3)>();
        Span<Vector3> grown = stackalloc Vector3[Nodes.Hull.MaxCorners];
        int at = 0;

        for (int n = 0; n < faces; n++)
        {
            int count = sides[n];
            ReadOnlySpan<Vector3> face = corners.Slice(at, count);
            at += count;

            for (int c = 0; c < count; c++)
            {
                Vector3 from = Snap(face[c]);
                Vector3 to = Snap(face[(c + 1) % count]);

                // Each edge is shared by two faces; draw it once.
                if (drawn.Add((from, to)) && !drawn.Contains((to, from)))
                    builder.Edge(face[c], face[(c + 1) % count], centre);
            }

            // The glow sits a hair outside the node so it never fights the
            // node's own faces for depth.
            Vector3 middle = Vector3.Zero;
            for (int c = 0; c < count; c++)
            {
                grown[c] = centre + (face[c] - centre) * 1.015f;
                middle += grown[c];
            }

            builder.GlowFace(grown[..count], middle / count);
        }
    }

    /// <summary>
    /// Positions rounded to a millimetre, so the same corner reached through
    /// two faces' clipping arithmetic compares equal.
    /// </summary>
    private static Vector3 Snap(Vector3 v) =>
        new(Mathf.Round(v.X * 1000f), Mathf.Round(v.Y * 1000f), Mathf.Round(v.Z * 1000f));
}
