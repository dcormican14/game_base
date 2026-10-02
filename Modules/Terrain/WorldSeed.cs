using System;
using System.Globalization;

namespace GameBase.Terrain;

/// <summary>
/// The seed a world is built from, chosen on the main menu and carried into
/// the level.
///
/// A seed is TEXT, as the player types it. A plain number is used as that
/// number, so a seed read off the pause menu and typed back in gives the same
/// world; anything else ("banana") is hashed to one. A static rather than a
/// scene parameter, so the level still starts on its own from the editor --
/// with <see cref="Default"/> -- when nothing chose one.
/// </summary>
public static class WorldSeed
{
    /// <summary>The seed a level uses when started without the menu.</summary>
    public const string Default = "1";

    /// <summary>What the menu chose, or null when nothing has.</summary>
    public static string Chosen { get; set; }

    /// <summary>The seed text the next world is built from.</summary>
    public static string Current => string.IsNullOrWhiteSpace(Chosen) ? Default : Chosen.Trim();

    /// <summary>A fresh random seed: a number short enough to write down.</summary>
    public static string RandomText() =>
        System.Random.Shared.NextInt64(1, 10_000_000_000L).ToString(CultureInfo.InvariantCulture);

    /// <summary>The number a seed's text stands for.</summary>
    public static ulong Parse(string text)
    {
        text = (text ?? "").Trim();

        if (ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong number))
            return number;

        // FNV-1a over the characters: stable across runs and machines, which
        // string.GetHashCode is not.
        ulong hash = 0xCBF29CE484222325UL;
        foreach (char c in text)
        {
            hash ^= c;
            hash = unchecked(hash * 0x100000001B3UL);
        }

        return hash;
    }
}
