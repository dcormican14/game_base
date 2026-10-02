namespace GameBase.Core;

/// <summary>
/// Something in a level with a line to add to the stats overlay (see
/// PerfStats) -- the planet reports the terrain under the player. An
/// interface, so the overlay stays drop-in: it shows whatever implements this
/// and knows nothing about what that is.
/// </summary>
public interface IStatsReport
{
    /// <summary>The line to show, or null for nothing right now. Asked a few times a second.</summary>
    string StatsLine { get; }
}
