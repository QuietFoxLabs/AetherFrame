using AetherFrame.Services;
using AetherFrame.Services.Caching;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The logged-in character is asked for several times per frame; it is read from the game every
/// half second, and again at once after a login or logout — the same rule as the character's details.
/// </summary>
public class CharacterContextCacheTests
{
    private static readonly CharacterContext Alice = new(1001, "Alice Example", "Twintania");
    private static readonly CharacterContext Bob = new(1002, "Bob Example", "Twintania");

    [Fact]
    public void ReadsOnce_WithinTheRefreshWindow_AndAgainAfterIt()
    {
        var reads = 0;
        var now = 10_000L;
        var cache = new CharacterContextCache(() => { reads++; return Alice; }, () => now);

        Assert.Equal(Alice, cache.Current);
        Assert.Equal(Alice, cache.Current);
        now += CharacterInfoCache.RefreshMilliseconds - 1;
        Assert.Equal(Alice, cache.Current);
        Assert.Equal(1, reads);

        now += 1;
        Assert.Equal(Alice, cache.Current);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void Invalidate_ReadsAgainAtOnce()
    {
        var current = (CharacterContext?)Alice;
        var reads = 0;
        var cache = new CharacterContextCache(() => { reads++; return current; }, () => 5_000L);

        Assert.Equal(Alice, cache.Current);
        current = Bob;
        Assert.Equal(Alice, cache.Current);

        cache.Invalidate();

        Assert.Equal(Bob, cache.Current);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void NoCharacter_IsNeverKeptPastTheWindow()
    {
        var current = (CharacterContext?)null;
        var now = 0L;
        var cache = new CharacterContextCache(() => current, () => now);

        Assert.Null(cache.Current);
        current = Alice;
        Assert.Null(cache.Current);

        now += CharacterInfoCache.RefreshMilliseconds;
        Assert.Equal(Alice, cache.Current);
    }

    [Fact]
    public void AClockThatWentBackwards_ReadsAgain()
    {
        var reads = 0;
        var now = 1_000L;
        var cache = new CharacterContextCache(() => { reads++; return Alice; }, () => now);

        _ = cache.Current;
        now = 900L;
        _ = cache.Current;

        Assert.Equal(2, reads);
    }
}
