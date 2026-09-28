using GameBase.Nodes;

namespace GameBase.Tools;

/// <summary>
/// The check every mine and place goes through before it changes the world,
/// and the record it makes afterwards.
///
/// Nothing is collected yet, so the only ledger today is
/// <see cref="UnlimitedLedger"/>. This is where collecting will plug in: a
/// ledger backed by the inventory refuses a place the player has no material
/// for, and banks what a mine removes, without any tool changing.
///
/// Amounts are in NODES: a raw node is 1, and a particle edit is the volume of
/// fill it moved, in cells.
/// </summary>
public interface IMaterialLedger
{
    /// <summary>May this much of this material be removed from the world?</summary>
    bool CanMine(NodeType type, float amount);

    /// <summary>May this much of this material be put into the world?</summary>
    bool CanPlace(NodeType type, float amount);

    /// <summary>Records material that was removed.</summary>
    void Mined(NodeType type, float amount);

    /// <summary>Records material that was put in.</summary>
    void Placed(NodeType type, float amount);
}

/// <summary>Allows everything and records nothing: the ledger until collecting exists.</summary>
public sealed class UnlimitedLedger : IMaterialLedger
{
    public static readonly UnlimitedLedger Instance = new();

    public bool CanMine(NodeType type, float amount) => true;

    public bool CanPlace(NodeType type, float amount) => true;

    public void Mined(NodeType type, float amount)
    {
    }

    public void Placed(NodeType type, float amount)
    {
    }
}
