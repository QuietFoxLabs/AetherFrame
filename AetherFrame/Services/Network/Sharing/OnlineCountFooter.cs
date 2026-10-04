using System;
using System.Globalization;
using AetherFrame.UI.Library;

namespace AetherFrame.Services.Network.Sharing;

/// <summary>
/// The online count's item in the My Plates footer, beside the loaded version ("The online
/// count"): the count, or that it is connecting or unavailable, never a zero for a failed request;
/// or, for a sharing character whose player hasn't seen the notice yet, where to turn it on.
/// Nothing for anyone else. It reads only what <see cref="OnlineCount"/> already holds: drawing
/// never sends anything. The item is made again only when what it says changes.
/// </summary>
internal sealed class OnlineCountFooter
{
    internal const string Scope =
        "Characters logged in right now with AetherFrame sharing turned on, counted once each across every player. Characters that don't share aren't counted. Updated about once a minute.";

    internal static readonly MyPlatesFooterItem Connecting = new("Online: connecting...", "Getting the number of sharing characters online from AetherFrame's sharing server.\n" + Scope);

    internal static readonly MyPlatesFooterItem Unavailable = new("Online: unavailable", "AetherFrame's sharing server didn't give the number of sharing characters online. It tries again by itself, and the number shows once the server answers.\n" + Scope);

    internal static readonly MyPlatesFooterItem NoticeDue = new("Online: see Sharing", "Open Sharing and read what the online count sends: nothing is sent until you choose Got it there.\n" + Scope);

    private readonly OnlineCount count;
    private readonly Func<bool> noticeDue;
    private readonly Func<DateTimeOffset> utcNow;
    private (OnlineCountState State, int Count) shown = (OnlineCountState.Off, -1);
    private MyPlatesFooterItem? item;

    internal OnlineCountFooter(OnlineCount count, Func<bool> noticeDue, Func<DateTimeOffset> utcNow)
    {
        this.count = count ?? throw new ArgumentNullException(nameof(count));
        this.noticeDue = noticeDue ?? throw new ArgumentNullException(nameof(noticeDue));
        this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    /// <summary>
    /// Whether the logged-in character shares but the online count waits for its notice
    /// (<see cref="CharacterSharingView.OnlineNotice"/>): My Plates then says where to turn it on.
    /// </summary>
    internal static bool WaitsForNotice(CharacterSharingView sharing, ulong? loggedIn) =>
        sharing is { Loaded: true, Unreadable: false, OnlineNotice: true } && loggedIn is { } contentId
        && sharing.Find(contentId) is { Stage: SharingStage.Shared, ReplacingKey: false };

    /// <summary>The item for <paramref name="view"/> read at <paramref name="now"/>, or none.</summary>
    internal static MyPlatesFooterItem? ItemFor(OnlineCountView view, DateTimeOffset now, bool noticeDue)
    {
        var current = view.AsOf(now);
        return current.State switch
        {
            OnlineCountState.Online => new MyPlatesFooterItem(Text(current.Count), Scope),
            OnlineCountState.Connecting => Connecting,
            OnlineCountState.Unavailable => Unavailable,
            _ => noticeDue ? NoticeDue : null,
        };
    }

    /// <summary>The count's text: "12 online".</summary>
    internal static string Text(int online) => online.ToString("N0", CultureInfo.InvariantCulture) + " online";

    /// <summary>The item this frame, for the framework thread: the one made last unless what it says changed.</summary>
    internal MyPlatesFooterItem? Item()
    {
        var current = count.View.AsOf(utcNow());
        var key = current.State == OnlineCountState.Off ? (noticeDue() ? (OnlineCountState.Off, 1) : (OnlineCountState.Off, 0)) : (current.State, current.Count);
        if (key != shown)
        {
            shown = key;
            item = ItemFor(current, utcNow(), key == (OnlineCountState.Off, 1));
        }

        return item;
    }
}
