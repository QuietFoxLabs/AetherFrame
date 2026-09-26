using System;
using AetherFrame.Services.Plates;

namespace AetherFrame.Services.Caching;

/// <summary>
/// Keeps the logged-in character's <see cref="CharacterContext"/> without re-reading the game's
/// player state (and allocating its name and World strings) on every call: a read is reused for at
/// most <see cref="CharacterInfoCache.RefreshMilliseconds"/>, then taken again, and a "no
/// character" read is never kept past that. The same refresh as <see cref="CharacterInfoCache"/>,
/// invalidated by the same login and logout events. Dalamud-free (clock and reader are injected).
/// </summary>
internal sealed class CharacterContextCache
{
    private readonly Func<CharacterContext?> read;
    private readonly Func<long> clockMilliseconds;
    private CharacterContext? cached;
    private long readAt;
    private bool hasRead;

    /// <param name="read">Reads the current character (null when none is logged in).</param>
    /// <param name="clockMilliseconds">A monotonic millisecond clock.</param>
    internal CharacterContextCache(Func<CharacterContext?> read, Func<long> clockMilliseconds)
    {
        this.read = read;
        this.clockMilliseconds = clockMilliseconds;
    }

    internal CharacterContext? Current
    {
        get
        {
            var now = clockMilliseconds();

            // Elapsed time, not a timestamp comparison, so no starting value can overflow into
            // "still fresh".
            if (!hasRead || now - readAt >= CharacterInfoCache.RefreshMilliseconds || now < readAt)
            {
                cached = read();
                readAt = now;
                hasRead = true;
            }

            return cached;
        }
    }

    /// <summary>Forgets the last read, so the next access reads again (e.g. on login or logout).</summary>
    internal void Invalidate() => hasRead = false;
}
