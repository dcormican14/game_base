using System;

namespace GameBase.Core;

/// <summary>
/// Something a level waits on before it is playable, with a progress figure a
/// loading screen can show. Implemented by the chunk streamer; anything else a
/// level must wait for can implement it too.
/// </summary>
public interface ILoadProgress
{
    /// <summary>0..1, never decreasing.</summary>
    float Progress { get; }

    bool IsReady { get; }

    /// <summary>Raised once, when <see cref="IsReady"/> first becomes true.</summary>
    event Action BecameReady;
}
