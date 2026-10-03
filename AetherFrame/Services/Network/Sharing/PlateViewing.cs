using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Threading;
using AetherFrame.Personas;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Protocol.Requests;
using AetherFrame.Services.Network.Transport;

namespace AetherFrame.Services.Network.Sharing;

/// <summary>Where viewing a player's Plate stands.</summary>
internal enum ViewStage
{
    /// <summary>Nothing asked for yet.</summary>
    Idle,

    /// <summary>Asked for, and waiting for the sharing service to be free.</summary>
    Waiting,

    /// <summary>Being looked up.</summary>
    Looking,

    /// <summary>Shown.</summary>
    Shown,

    /// <summary>The server has no Plate for that name and World (C5's one answer for every cause).</summary>
    NotFound,

    /// <summary>The player hid this player's Plate, so it isn't looked up.</summary>
    Hidden,

    /// <summary>It couldn't be looked up: <see cref="PlateViewerView.Failure"/> says why.</summary>
    Failed,
}

/// <summary>Why a lookup or a report failed.</summary>
internal enum ViewFailure
{
    None,

    /// <summary>None of the player's characters shares, so nothing can be looked up (V1).</summary>
    NotSharing,

    /// <summary>The server's limits were reached (C6): try again later.</summary>
    TooMany,

    /// <summary>The server couldn't be reached, or its answer couldn't be used.</summary>
    Unreachable,

    /// <summary>The character's key couldn't sign.</summary>
    KeyUnavailable,

    /// <summary>Another AetherFrame's Lodestone check took the signing character over (C1).</summary>
    TakenOver,

    /// <summary>The server refused the request, or sent something this build refuses.</summary>
    Refused,
}

/// <summary>Where a report stands.</summary>
internal enum ReportStage
{
    None,
    Sending,
    Sent,
    Failed,
}

/// <summary>A player's character, by the full name and World a lookup names.</summary>
internal sealed record PlateTarget(string Name, string World);

/// <summary>
/// A Plate as received: the served profile, drawn as <see cref="Plate"/>, and the bytes of each of its
/// images that passed the checks, by index (null for one that didn't, which isn't drawn): how many
/// failed the checks, and how many the server didn't send (one changed while it loaded, say).
/// </summary>
internal sealed record ViewedPlate(ServedPlate Plate, IReadOnlyList<byte[]?> Images, int ImagesRefused, int ImagesMissing = 0);

/// <summary>What the viewer window reads each frame: one immutable value, replaced whole.</summary>
internal sealed record PlateViewerView(PlateTarget? Target, ViewStage Stage, ViewFailure Failure, ViewedPlate? Plate, long Generation, ReportStage Report)
{
    internal static readonly PlateViewerView Initial = new(null, ViewStage.Idle, ViewFailure.None, null, 0, ReportStage.None);
}

/// <summary>
/// Viewing another player's Plate (NETWORK2's N2-10; decisions V1, V5, C5, D6 and I1): a lookup by
/// full name and World, signed by the key of one of the player's own shared characters (the one
/// logged in when it shares), then each image by the served profile's revision marker. Each image
/// is checked by the specification's section 8.2.1 and against its entry before it is drawn, and one
/// that fails is left out. Everything received is held in memory only and never saved, and a lookup
/// that finds nothing clears what was shown. Nothing is looked up for a player whose Plate is
/// hidden, and nothing at all until one of the player's characters shares. Each request is one
/// persona-session operation, retried each frame while the session is busy; the log gets outcome
/// kinds only, never a name, a World or anything shown. Compiled only in the networking preview
/// flavour.
/// </summary>
internal sealed class PlateViewing
{
    /// <summary>The reasons a report can give (ServerApi-v1.md, section 2.3).</summary>
    internal static readonly IReadOnlyList<string> Reasons = ["offensive", "impersonation", "spam", "other"];

    private readonly Func<string, Action<PersonaManager>, bool> tryRun;
    private readonly Func<CharacterSharingView> sharing;
    private readonly Func<ulong?> currentCharacter;
    private readonly SharingClient client;
    private readonly HiddenPlates hidden;
    private readonly Action<ulong, PersonaId> takenOver;
    private readonly CancellationToken stopping;
    private readonly Action<string> log;
    private readonly object gate = new();
    private volatile PlateViewerView view = PlateViewerView.Initial;
    private Action<PersonaManager>? pending;
    private string? pendingName;

    internal PlateViewing(
        Func<string, Action<PersonaManager>, bool> tryRun,
        Func<CharacterSharingView> sharing,
        Func<ulong?> currentCharacter,
        SharingClient client,
        HiddenPlates hidden,
        Action<ulong, PersonaId> takenOver,
        CancellationToken stopping,
        Action<string> log)
    {
        this.tryRun = tryRun ?? throw new ArgumentNullException(nameof(tryRun));
        this.sharing = sharing ?? throw new ArgumentNullException(nameof(sharing));
        this.currentCharacter = currentCharacter ?? throw new ArgumentNullException(nameof(currentCharacter));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.hidden = hidden ?? throw new ArgumentNullException(nameof(hidden));
        this.takenOver = takenOver ?? throw new ArgumentNullException(nameof(takenOver));
        this.stopping = stopping;
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal PlateViewerView View => view;

    internal HiddenPlates Hidden => hidden;

    /// <summary>
    /// Runs on the framework thread when a lookup is asked for, before it is handed to the sharing
    /// service: looking up another player's Plate is a player's action, at which the plugin asks for
    /// its own character's re-read when one is due, so it goes first.
    /// </summary>
    internal Action? Looking { get; init; }

    /// <summary>Whether one of the player's characters shares: only then can anything be looked up, and only then do the menu item and the search show (V1).</summary>
    internal bool CanView
    {
        get
        {
            var characters = sharing().Characters;
            foreach (var character in characters)
            {
                if (character.IsBound)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Asks for the Plate of the character named <paramref name="name"/> on <paramref name="world"/>:
    /// looked up when the sharing service is next free, unless the player hid it. False, with nothing
    /// asked, for a name or a World that isn't one. The framework thread only: the character logged
    /// in now, whose key signs when it shares, is read here, never on the session's thread.
    /// </summary>
    internal bool Open(string name, string world)
    {
        if (!IsName(name) || !IsWorld(world))
        {
            return false;
        }

        var target = new PlateTarget(Clean(name), world.Trim());
        lock (gate)
        {
            var generation = view.Generation + 1;
            if (hidden.IsHidden(target.Name, target.World))
            {
                pending = null;
                view = new PlateViewerView(target, ViewStage.Hidden, ViewFailure.None, null, generation, ReportStage.None);
                return true;
            }

            if (!CanView)
            {
                pending = null;
                view = new PlateViewerView(target, ViewStage.Failed, ViewFailure.NotSharing, null, generation, ReportStage.None);
                return true;
            }

            view = new PlateViewerView(target, ViewStage.Waiting, ViewFailure.None, null, generation, ReportStage.None);
            var current = currentCharacter();
            pendingName = "sharing view";
            pending = manager => Guarded(manager, generation, reporting: false, () => Look(manager, target, generation, current));
        }

        Looking?.Invoke();
        return true;
    }

    /// <summary>Looks the shown player's Plate up again.</summary>
    internal bool Refresh() => view.Target is { } target && Open(target.Name, target.World);

    /// <summary>Hides the shown player's Plate on this PC; it is dropped at once. False when that couldn't be saved.</summary>
    internal bool Hide()
    {
        if (view.Target is not { } target || !hidden.Hide(target.Name, target.World))
        {
            return false;
        }

        lock (gate)
        {
            if (view.Target == target)
            {
                pending = null;
                view = view with { Stage = ViewStage.Hidden, Plate = null, Failure = ViewFailure.None, Generation = view.Generation + 1 };
            }
        }

        return true;
    }

    /// <summary>Shows the shown player's Plate again, and looks it up. False when that couldn't be saved.</summary>
    internal bool Unhide() => view.Target is { } target && hidden.Show(target.Name, target.World) && Open(target.Name, target.World);

    /// <summary>Reports the shown player's Plate with one of <see cref="Reasons"/>, when the sharing service is next free. The framework thread only.</summary>
    internal bool Report(string reason)
    {
        if (view.Target is not { } target || view.Stage != ViewStage.Shown || !System.Linq.Enumerable.Contains(Reasons, reason) || view.Report is ReportStage.Sending or ReportStage.Sent)
        {
            return false;
        }

        lock (gate)
        {
            var generation = view.Generation;
            view = view with { Report = ReportStage.Sending };
            var current = currentCharacter();
            pendingName = "sharing report";
            pending = manager => Guarded(manager, generation, reporting: true, () => SendReport(manager, target, reason, generation, current));
        }

        return true;
    }

    /// <summary>Forgets what is shown: nothing of it stays in memory here.</summary>
    internal void Close()
    {
        lock (gate)
        {
            pending = null;
            view = PlateViewerView.Initial with { Generation = view.Generation + 1 };
        }
    }

    /// <summary>One frame's work: hands a waiting request to the sharing service when it is free. The framework thread only.</summary>
    internal void OnFrame()
    {
        Action<PersonaManager>? work;
        string? name;
        lock (gate)
        {
            work = pending;
            name = pendingName;
        }

        if (work is null || name is null)
        {
            return;
        }

        bool started;
        try
        {
            started = tryRun(name, work);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            started = false;
        }

        if (started)
        {
            lock (gate)
            {
                if (ReferenceEquals(pending, work))
                {
                    pending = null;
                }
            }
        }
    }

    /// <summary>Whether <paramref name="name"/> could be a character's full name: two words of letters, an apostrophe or a hyphen, as the game allows.</summary>
    internal static bool IsName(string? name)
    {
        if (name is null)
        {
            return false;
        }

        var parts = Clean(name).Split(' ');
        if (parts.Length != 2 || Clean(name).Length > 21)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length is < 2 or > 15 || !char.IsAsciiLetter(part[0]))
            {
                return false;
            }

            foreach (var c in part)
            {
                if (!char.IsAsciiLetter(c) && c is not '\'' and not '-')
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Whether <paramref name="world"/> could be a World's name: one word of ASCII letters.</summary>
    internal static bool IsWorld(string? world)
    {
        if (world is null)
        {
            return false;
        }

        var trimmed = world.Trim();
        if (trimmed.Length is < 2 or > 32)
        {
            return false;
        }

        foreach (var c in trimmed)
        {
            if (!char.IsAsciiLetter(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A name with its outer spaces removed and inner runs of spaces made one.</summary>
    private static string Clean(string name) => string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Runs one operation, then puts back the persona selection it found, as every sharing
    /// operation does; anything it didn't expect fails it, never leaving it looking forever.
    /// </summary>
    private void Guarded(PersonaManager manager, long generation, bool reporting, Action work)
    {
        var selected = manager.Active?.Slot;
        try
        {
            work();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log($"Sharing: {(reporting ? "a report" : "a lookup")} failed ({exception.GetType().Name}).");
            Fail(generation, ViewFailure.Refused, reporting);
        }
        finally
        {
            try
            {
                if (manager.Active?.Slot != selected)
                {
                    if (selected is { } slot && manager.TryGet(slot, out _))
                    {
                        manager.Select(slot);
                    }
                    else
                    {
                        manager.Deselect();
                    }
                }
            }
            catch (PersonaException exception)
            {
                log($"Sharing: the persona selection couldn't be put back ({exception.Error}).");
            }
        }
    }

    private void Look(PersonaManager manager, PlateTarget target, long generation, ulong? current)
    {
        Set(generation, v => v with { Stage = ViewStage.Looking });
        var body = Body(target, null, -1, null);
        var response = Send(manager, RequestProofKind.Lookup, body, generation, current);
        if (response is null)
        {
            return;
        }

        if (response.Status == HttpStatusCode.NotFound)
        {
            log("Sharing: a lookup found no Plate.");
            Set(generation, v => v with { Stage = ViewStage.NotFound, Plate = null, Failure = ViewFailure.None });
            return;
        }

        if (response.Status != HttpStatusCode.OK)
        {
            Fail(generation, Failure(response.Status));
            return;
        }

        ServedProfile profile;
        ServedPlate plate;
        try
        {
            profile = ServedProfile.Read(response.Body);
            plate = ServedPlate.From(profile);
        }
        catch (ProtocolException exception)
        {
            log($"Sharing: a served Plate was refused ({exception.Error}).");
            Fail(generation, ViewFailure.Refused);
            return;
        }

        var images = new byte[]?[profile.Images.Count];
        var refused = 0;
        var missing = 0;
        var marker = profile.Marker.ToString();
        for (var index = 0; index < images.Length; index++)
        {
            if (view.Generation != generation)
            {
                return;
            }

            var answer = Send(manager, RequestProofKind.Image, Body(target, marker, index, null), generation, current);
            if (answer is null)
            {
                return;
            }

            if (answer.Status != HttpStatusCode.OK)
            {
                // Most often a newer revision's marker: the player saved while this one loaded.
                missing++;
            }
            else if (Accepts(answer.Body, profile.Images[index]))
            {
                images[index] = answer.Body;
            }
            else
            {
                refused++;
            }
        }

        if (refused + missing > 0)
        {
            log($"Sharing: of a Plate's images, {refused.ToString(CultureInfo.InvariantCulture)} were refused and {missing.ToString(CultureInfo.InvariantCulture)} weren't sent.");
        }

        Set(generation, v => v with { Stage = ViewStage.Shown, Plate = new ViewedPlate(plate, images, refused, missing), Failure = ViewFailure.None });
    }

    /// <summary>An image's bytes are drawn only when section 8.2.1 allows them and they are the format and size their entry says (I1, section 8.6).</summary>
    internal static bool Accepts(byte[] bytes, ServedImage entry)
    {
        try
        {
            return ImageSniffer.Sniff(bytes) == new SniffedImage(entry.Format, entry.Width, entry.Height);
        }
        catch (ProtocolException)
        {
            return false;
        }
    }

    private void SendReport(PersonaManager manager, PlateTarget target, string reason, long generation, ulong? current)
    {
        var response = Send(manager, RequestProofKind.Report, Body(target, null, -1, reason), generation, current, reporting: true);
        if (response is null)
        {
            return;
        }

        log($"Sharing: a report was answered {((int)response.Status).ToString(CultureInfo.InvariantCulture)}.");
        Set(generation, v => v with { Report = response.Status == HttpStatusCode.NoContent ? ReportStage.Sent : ReportStage.Failed });
    }

    /// <summary>
    /// Sends one request, signed by the key of <paramref name="current"/>, the character logged in
    /// when it was asked for, when it shares, or else of another of the player's shared characters;
    /// null, with the view updated, when nothing could be sent, no answer came, or the answer was
    /// that another key's check took the signer over (410), which is recorded as for any request.
    /// </summary>
    private SharingResponse? Send(PersonaManager manager, RequestProofKind kind, byte[] body, long generation, ulong? current, bool reporting = false)
    {
        var signer = Signer(current);
        if (signer is null || !manager.TryGet(signer.Slot, out var persona) || !persona!.PublicKey.Id.Equals(signer.Key))
        {
            log($"Sharing: {SharingClient.PathOf(kind)} wasn't sent: no shared character's key.");
            Fail(generation, signer is null ? ViewFailure.NotSharing : ViewFailure.KeyUnavailable, reporting);
            return null;
        }

        try
        {
            // A key signs only while it is the selected one; the selection is put back afterwards.
            if (manager.Active?.Slot != signer.Slot)
            {
                manager.Select(signer.Slot);
            }

            var response = client.ActionAsync(kind, body, new LeasedSigner(manager, signer.Slot, persona.PublicKey), stopping).GetAwaiter().GetResult();
            if (kind != RequestProofKind.Image)
            {
                log($"Sharing: {SharingClient.PathOf(kind)} answered {((int)response.Status).ToString(CultureInfo.InvariantCulture)}.");
            }

            if (response.Status == HttpStatusCode.Gone)
            {
                takenOver(signer.ContentId, signer.Key);
                Fail(generation, ViewFailure.TakenOver, reporting);
                return null;
            }

            return response;
        }
        catch (SharingException exception)
        {
            log($"Sharing: {SharingClient.PathOf(kind)} got no usable answer ({(exception.Status is { } status ? ((int)status).ToString(CultureInfo.InvariantCulture) : "none")}).");
            Fail(generation, ViewFailure.Unreachable, reporting);
            return null;
        }
        catch (LeasedSignerException exception)
        {
            log($"Sharing: the character's key couldn't sign ({exception.Availability}).");
            Fail(generation, ViewFailure.KeyUnavailable, reporting);
            return null;
        }
    }

    /// <summary>The shared character whose key signs: <paramref name="current"/> when it shares, or else the first that does.</summary>
    private SharingCharacter? Signer(ulong? current)
    {
        var characters = sharing().Characters;
        if (current is not null)
        {
            foreach (var character in characters)
            {
                if (character.ContentId == current && character.IsBound)
                {
                    return character;
                }
            }
        }

        foreach (var character in characters)
        {
            if (character.IsBound)
            {
                return character;
            }
        }

        return null;
    }

    private static ViewFailure Failure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => ViewFailure.TooMany,
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError => ViewFailure.Unreachable,
        _ => ViewFailure.Refused,
    };

    private void Fail(long generation, ViewFailure failure, bool reporting = false) =>
        Set(generation, v => reporting ? v with { Report = ReportStage.Failed } : v with { Stage = ViewStage.Failed, Failure = failure, Plate = null });

    /// <summary>Applies <paramref name="change"/> when nothing else was asked for since <paramref name="generation"/>.</summary>
    private void Set(long generation, Func<PlateViewerView, PlateViewerView> change)
    {
        lock (gate)
        {
            if (view.Generation == generation)
            {
                view = change(view);
            }
        }
    }

    /// <summary>A request's JSON body: the name and World, and an image's marker and index or a report's reason.</summary>
    private static byte[] Body(PlateTarget target, string? marker, int index, string? reason)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("name", target.Name);
            writer.WriteString("world", target.World);
            if (marker is not null)
            {
                writer.WriteString("marker", marker);
                writer.WriteNumber("index", index);
            }

            if (reason is not null)
            {
                writer.WriteString("reason", reason);
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}
