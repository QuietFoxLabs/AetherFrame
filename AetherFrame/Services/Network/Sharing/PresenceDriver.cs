using System;

namespace AetherFrame.Services.Network.Sharing;

/// <summary>
/// What tells the online count whose presence to send, every frame: the game's own tick, never
/// drawing. Dalamud doesn't call a plugin's draw while it hides plugin windows, which it does by
/// default with the game's UI hidden, in a cutscene and in gpose, so a logout, a pause or sharing
/// turned off in one of those would otherwise go unnoticed and the heartbeats would carry on for a
/// character that is no longer there (requirement 4). The tick also stops at once when nobody is
/// logged in, without waiting for the character cache to read the game again.
/// <para>
/// <see cref="Tick"/> only compares and hands over, as <see cref="OnlineCount.Update"/> does: no
/// request is made on the game's thread.
/// </para>
/// </summary>
internal sealed class PresenceDriver(OnlineCount count, Func<bool> loggedIn, Func<PresenceTarget?> target)
{
    private readonly OnlineCount count = count ?? throw new ArgumentNullException(nameof(count));
    private readonly Func<bool> loggedIn = loggedIn ?? throw new ArgumentNullException(nameof(loggedIn));
    private readonly Func<PresenceTarget?> target = target ?? throw new ArgumentNullException(nameof(target));

    /// <summary>The game's thread, each tick: nothing is sent for a character that isn't logged in and sharing.</summary>
    internal void Tick() => count.Update(loggedIn() ? target() : null);
}
