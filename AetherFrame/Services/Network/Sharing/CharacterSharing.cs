using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using AetherFrame.Personas;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Requests;
using AetherFrame.Protocol.Signing;
using AetherFrame.Services.Network.Publishing;
using AetherFrame.Services.Network.Transport;

namespace AetherFrame.Services.Network.Sharing;

/// <summary>What happened last, for one character: the window says it in words (<see cref="SharingText"/>).</summary>
internal enum SharingNoticeKind
{
    CodeReady,
    CheckPassed,
    CheckFailed,
    TurnedOff,
    TakenOver,
    NoLongerBound,
    Renamed,
    TooMany,
    TryLater,
    Unreachable,
    UpdateNeeded,
    KeyUnavailable,
    Refused,
    SaveFailed,
    Failed,
    Published,
    PublishWaiting,
    PublishRefused,
    PublishStale,
    PublishNotStored,
    Paused,
    Resumed,
}

/// <summary>
/// A notice for one character, or for every character when <see cref="ContentId"/> is 0, with the
/// server's reason code for a refused publish, or the commit's result for one not stored.
/// </summary>
internal sealed record SharingNotice(ulong ContentId, SharingNoticeKind Kind, string? Detail = null);

/// <summary>A candidate waiting for the player to see it before it is first sent (C3's first showing).</summary>
internal sealed record PendingConsent(ulong ContentId, SnapshotCandidate Candidate);

/// <summary>A code the server issued for a character's Lodestone check, kept in memory only.</summary>
internal sealed record IssuedCode(ulong ContentId, string Code, DateTimeOffset Expires);

/// <summary>What the window reads each frame: one immutable value, replaced whole.</summary>
internal sealed class CharacterSharingView
{
    internal static readonly CharacterSharingView Initial = new(false, false, false, Array.Empty<SharingCharacter>(), null, null, null);

    internal CharacterSharingView(bool loaded, bool unreadable, bool busy, IReadOnlyList<SharingCharacter> characters, IssuedCode? code, SharingNotice? notice, PendingConsent? consent)
    {
        Loaded = loaded;
        Unreadable = unreadable;
        Busy = busy;
        Characters = characters;
        Code = code;
        Notice = notice;
        Consent = consent;
    }

    /// <summary>Whether the sharing file was read.</summary>
    internal bool Loaded { get; }

    /// <summary>Whether it couldn't be: sharing then stays off, and nothing is written over it.</summary>
    internal bool Unreadable { get; }

    /// <summary>Whether an operation was handed to the persona session and hasn't ended.</summary>
    internal bool Busy { get; }

    internal IReadOnlyList<SharingCharacter> Characters { get; }

    /// <summary>The latest code issued, for the character it names.</summary>
    internal IssuedCode? Code { get; }

    internal SharingNotice? Notice { get; }

    /// <summary>A Plate this character hasn't shared before, waiting to be shown before it is sent.</summary>
    internal PendingConsent? Consent { get; }

    /// <summary>The character with <paramref name="contentId"/>, if the file names it.</summary>
    internal SharingCharacter? Find(ulong contentId)
    {
        foreach (var character in Characters)
        {
            if (character.ContentId == contentId)
            {
                return character;
            }
        }

        return null;
    }

    internal CharacterSharingView With(bool? busy = null, IReadOnlyList<SharingCharacter>? characters = null, IssuedCode? code = null, bool clearCode = false, SharingNotice? notice = null, bool clearNotice = false, bool? loaded = null, bool? unreadable = null, PendingConsent? consent = null, bool clearConsent = false) =>
        new(loaded ?? Loaded, unreadable ?? Unreadable, busy ?? Busy, characters ?? Characters, clearCode ? null : code ?? Code, clearNotice ? null : notice ?? Notice, clearConsent ? null : consent ?? Consent);
}

/// <summary>
/// Opting a character in and out of sharing (NETWORK2's N2-9b; decision batch C, C1 to C4, and V4):
/// the character's own key, made behind the scenes as a persona; the Lodestone code and check; and
/// turning sharing off, which deletes what the server holds. Each change is one persona-session
/// operation (the session's <c>TryRun</c>): one at a time, under the persona files' lock, off the
/// framework thread, and waited for by unloading. Requests go through <see cref="SharingClient"/>,
/// each signed by the character's key through a lease opened for that one signature and released
/// at once, never held across a request.
/// <para>
/// The sharing file is saved before a request that depends on it is sent, so a key the server may
/// bind is always recorded first. The log gets operation names and outcome kinds only: never a
/// Content ID, a Lodestone id, a code, a name, a World or a key. Compiled only in the networking
/// preview flavour.
/// </para>
/// </summary>
internal sealed class CharacterSharing
{
    /// <summary>The label every character's key gets in the persona registry: nothing about the character (C1).</summary>
    internal const string KeyLabel = "Character key";

    private readonly Func<string, Action<PersonaManager>, bool> tryRun;
    private readonly SharingStateFile file;
    private readonly PublicationFiles publications;
    private readonly SharingClient client;
    private readonly Version pluginVersion;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly CancellationToken stopping;
    private readonly Action<string> log;
    private readonly object gate = new();
    private volatile CharacterSharingView view = CharacterSharingView.Initial;
    private bool statusChecked;

    internal CharacterSharing(
        Func<string, Action<PersonaManager>, bool> tryRun,
        SharingStateFile file,
        PublicationFiles publications,
        SharingClient client,
        Version pluginVersion,
        Func<DateTimeOffset> utcNow,
        CancellationToken stopping,
        Action<string> log)
    {
        this.tryRun = tryRun ?? throw new ArgumentNullException(nameof(tryRun));
        this.file = file ?? throw new ArgumentNullException(nameof(file));
        this.publications = publications ?? throw new ArgumentNullException(nameof(publications));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.pluginVersion = pluginVersion ?? throw new ArgumentNullException(nameof(pluginVersion));
        this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        this.stopping = stopping;
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal CharacterSharingView View => view;

    /// <summary>Reads the sharing file, once it hasn't been; false when the session couldn't start it.</summary>
    internal bool TryLoad() => Run("sharing load", _ =>
    {
        var characters = file.Read();
        Publish(view.With(characters: characters, loaded: true, unreadable: false));
    }, loading: true);

    /// <summary>
    /// Turns sharing on for the character, after the player agreed (C3's consent, with K4's
    /// acknowledgement): its key, made now unless it has one (or <paramref name="newKey"/> asks for a
    /// fresh one, when its key can't be opened), then a Lodestone code.
    /// </summary>
    internal bool TryStart(ulong contentId, bool newKey) => Run("sharing start", manager =>
    {
        var existing = view.Find(contentId);
        if (existing is { IsBound: true } && !newKey)
        {
            return;
        }

        PersonaRecord? persona = null;
        if (existing is not null && !newKey && manager.TryGet(existing.Slot, out var held) && held!.PublicKey.Id.Equals(existing.Key))
        {
            persona = held;
        }

        persona ??= manager.Create(KeyLabel);
        persona = manager.Acknowledge(persona.Slot);
        var entry = new SharingCharacter(contentId, persona.Slot, persona.PublicKey.Id, SharingStage.Checking);
        if (Save(Replaced(entry), contentId) && Key(manager, entry) is { } key)
        {
            RequestCode(manager, entry, key);
        }
    });

    /// <summary>Asks for a new code for a character whose check hasn't passed yet.</summary>
    internal bool TryNewCode(ulong contentId) => Run("sharing code", manager =>
    {
        if (view.Find(contentId) is { Stage: SharingStage.Checking } entry && Key(manager, entry) is { } key)
        {
            RequestCode(manager, entry, key);
        }
    });

    /// <summary>Sends the Lodestone check: the id the player's address named, and the code issued for this character.</summary>
    internal bool TryCheck(ulong contentId, string lodestoneId) => Run("sharing check", manager =>
    {
        if (view.Find(contentId) is not { Stage: SharingStage.Checking } entry || view.Code is not { } issued || issued.ContentId != contentId
            || !LodestoneAddress.IsId(lodestoneId) || Key(manager, entry) is not { } key)
        {
            return;
        }

        var response = Send(manager, entry, key, RequestProofKind.LodestoneCheck, SharingWire.Check(lodestoneId, issued.Code));
        if (response is null)
        {
            return;
        }

        if (response.Status != HttpStatusCode.OK)
        {
            Notify(contentId, response.Status == HttpStatusCode.UnprocessableEntity ? SharingNoticeKind.CheckFailed : Failure(response.Status));
            return;
        }

        CheckAnswer answer;
        try
        {
            answer = SharingWire.ReadCheck(response.Body);
        }
        catch (InvalidDataException)
        {
            Notify(contentId, SharingNoticeKind.Refused);
            return;
        }

        var bound = entry with { Stage = SharingStage.Shared, LodestoneId = lodestoneId, ProfileId = answer.ProfileId, Name = answer.Name, World = answer.World };
        if (Save(Replaced(bound), contentId))
        {
            Publish(view.With(clearCode: true, notice: new SharingNotice(contentId, SharingNoticeKind.CheckPassed)));
        }
    });

    /// <summary>
    /// Turns sharing off for the character (C4): the server deletes the binding and everything
    /// published for it. The character keeps its key, for next time.
    /// </summary>
    internal bool TryTurnOff(ulong contentId) => Run("sharing off", manager =>
    {
        if (view.Find(contentId) is { } entry)
        {
            TurnOff(manager, entry);
        }
    });

    /// <summary>Turns sharing off for every character that has it on, or has a check under way.</summary>
    internal bool TryTurnOffAll() => Run("sharing off all", manager =>
    {
        foreach (var entry in view.Characters)
        {
            if ((entry.IsBound || entry.Stage == SharingStage.Checking) && !TurnOff(manager, entry))
            {
                return;
            }
        }
    });

    /// <summary>
    /// Publishes <paramref name="candidate"/>, built from the character's Active Plate as it was
    /// saved (C3): signed under the character's key with its binding's profile id (C4) into the
    /// outbox, then sent. A Plate other than the one this character last signed is first shown to
    /// the player (<see cref="CharacterSharingView.Consent"/>), unless <paramref name="approved"/>
    /// says the player just approved exactly this candidate on that screen.
    /// </summary>
    internal bool TryPublish(ulong contentId, SnapshotCandidate candidate, bool approved)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return Run("sharing publish", manager =>
        {
            if (view.Find(contentId) is not { Stage: SharingStage.Shared, ProfileId: { } binding } entry)
            {
                Publish(view.With(clearConsent: true));
                return;
            }

            if (!approved && LastSigned(entry.Slot, binding) != candidate.PlateId)
            {
                Publish(view.With(consent: new PendingConsent(contentId, candidate)));
                return;
            }

            if (!StatusAllows(contentId) || Key(manager, entry) is not { } key)
            {
                return;
            }

            var outcome = PublicationCommit.Commit(manager, publications, new PublishConsent(candidate, entry.Slot, key, binding), utcNow);
            log($"Sharing: signing the Active Plate came to {outcome.Result}.");
            Publish(view.With(clearConsent: true));
            if (outcome.Result != PublishResult.Stored)
            {
                Notify(contentId, outcome.Result == PublishResult.KeyUnavailable ? SharingNoticeKind.KeyUnavailable : SharingNoticeKind.PublishNotStored, outcome.Result.ToString());
                return;
            }

            SendWaiting(manager, entry, key, binding);
        });
    }

    /// <summary>Sends the character's waiting revision again, after the server couldn't take it.</summary>
    internal bool TrySendWaiting(ulong contentId) => Run("sharing send", manager =>
    {
        if (view.Find(contentId) is { Stage: SharingStage.Shared, ProfileId: { } binding } entry && StatusAllows(contentId) && Key(manager, entry) is { } key)
        {
            SendWaiting(manager, entry, key, binding);
        }
    });

    /// <summary>Forgets a Plate waiting to be shown, when the player chose not to share it.</summary>
    internal void DeclineConsent()
    {
        lock (gate)
        {
            view = view.With(clearConsent: true);
        }
    }

    /// <summary>
    /// Pauses sharing for the character (C3): the server deletes its Plate and keeps the binding,
    /// and nothing it signed waits to be sent afterwards.
    /// </summary>
    internal bool TryPause(ulong contentId) => Run("sharing pause", manager =>
    {
        if (view.Find(contentId) is not { Stage: SharingStage.Shared } entry || Key(manager, entry) is not { } key)
        {
            return;
        }

        var response = Send(manager, entry, key, RequestProofKind.OptOut, SharingWire.Pause());
        if (response is null)
        {
            return;
        }

        if (response.Status != HttpStatusCode.NoContent)
        {
            Notify(contentId, Failure(response.Status));
            return;
        }

        PublicationSend.DropAll(publications, entry.Slot);
        if (Save(Replaced(entry with { Stage = SharingStage.Paused }), contentId))
        {
            Publish(view.With(clearConsent: true, notice: new SharingNotice(contentId, SharingNoticeKind.Paused)));
        }
    });

    /// <summary>Resumes a paused character: its Active Plate is shared again at the next publish, as a new revision.</summary>
    internal bool TryResume(ulong contentId) => Run("sharing resume", _ =>
    {
        if (view.Find(contentId) is { Stage: SharingStage.Paused } entry && Save(Replaced(entry with { Stage = SharingStage.Shared }), contentId))
        {
            Notify(contentId, SharingNoticeKind.Resumed);
        }
    });

    /// <summary>
    /// Asks the server to read the character's Lodestone page again, when the game's name or World
    /// differs from the binding's (C1). Nothing is sent otherwise.
    /// </summary>
    internal bool TryReread(ulong contentId, string name, string world) => Run("sharing reread", manager =>
    {
        if (view.Find(contentId) is not { IsBound: true } entry || SameCharacter(entry, name, world) || Key(manager, entry) is not { } key)
        {
            return;
        }

        var response = Send(manager, entry, key, RequestProofKind.LodestoneReread, SharingWire.Empty());
        if (response is null)
        {
            return;
        }

        switch (response.Status)
        {
            case HttpStatusCode.OK:
                try
                {
                    var (readName, readWorld) = SharingWire.ReadReread(response.Body);
                    if (Save(Replaced(entry with { Name = readName, World = readWorld }), contentId))
                    {
                        Notify(contentId, SharingNoticeKind.Renamed);
                    }
                }
                catch (InvalidDataException)
                {
                    Notify(contentId, SharingNoticeKind.Refused);
                }

                break;
            case HttpStatusCode.NotFound:
                if (Save(Replaced(entry.Unbound(SharingStage.Off)), contentId))
                {
                    Notify(contentId, SharingNoticeKind.NoLongerBound);
                }

                break;
            default:
                Notify(contentId, Failure(response.Status));
                break;
        }
    });

    /// <summary>Whether the game's <paramref name="name"/> and <paramref name="world"/> are the binding's, compared as the server compares them (C1).</summary>
    internal static bool SameCharacter(SharingCharacter entry, string name, string world) =>
        string.Equals(Canonical(entry.Name), Canonical(name), StringComparison.Ordinal)
        && string.Equals(entry.World, world, StringComparison.OrdinalIgnoreCase);

    /// <summary>Forgets the latest notice.</summary>
    internal void DismissNotice()
    {
        lock (gate)
        {
            view = view.With(clearNotice: true);
        }
    }

    private static string Canonical(string? name)
    {
        var parts = (name ?? "").Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }

    private static SharingNoticeKind Failure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => SharingNoticeKind.TooMany,
        HttpStatusCode.ServiceUnavailable => SharingNoticeKind.TryLater,
        HttpStatusCode.Gone => SharingNoticeKind.TakenOver,
        _ => SharingNoticeKind.Refused,
    };

    /// <summary>The Plate this character's key last signed under its binding, from its publication index; empty when none or unreadable.</summary>
    private Guid LastSigned(PersonaSlotId slot, ProfileId binding)
    {
        try
        {
            if (publications.ReadIndex(slot) is not { } bytes)
            {
                return Guid.Empty;
            }

            foreach (var entry in PublicationIndexCodec.Decode(bytes, slot).Entries)
            {
                if (entry.ProfileId == binding && entry.IsLive)
                {
                    return entry.PlateId;
                }
            }
        }
        catch (Exception exception) when (exception is PublicationFileException or IOException or UnauthorizedAccessException)
        {
            log($"Sharing: a publication index couldn't be read ({exception.GetType().Name}).");
        }

        return Guid.Empty;
    }

    /// <summary>Sends the waiting revision and says what came of it; a takeover is recorded as <see cref="Send"/> records one.</summary>
    private void SendWaiting(PersonaManager manager, SharingCharacter entry, PersonaPublicKey key, ProfileId binding)
    {
        var sent = PublicationSend.SendWaiting(manager, publications, client, entry.Slot, key, binding, utcNow, stopping);
        log($"Sharing: sending the Active Plate came to {sent.Result}.");
        switch (sent.Result)
        {
            case SendResult.Sent:
                Notify(entry.ContentId, SharingNoticeKind.Published);
                break;
            case SendResult.NothingWaiting:
                break;
            case SendResult.Stale:
                Notify(entry.ContentId, SharingNoticeKind.PublishStale);
                break;
            case SendResult.Refused:
                Notify(entry.ContentId, SharingNoticeKind.PublishRefused, sent.Reason);
                break;
            case SendResult.TakenOver:
                TakenOver(entry);
                break;
            case SendResult.TryLater:
                Notify(entry.ContentId, SharingNoticeKind.PublishWaiting);
                break;
            case SendResult.KeyUnavailable:
                Notify(entry.ContentId, SharingNoticeKind.KeyUnavailable);
                break;
            default:
                Notify(entry.ContentId, SharingNoticeKind.Failed);
                break;
        }
    }

    /// <summary>Records that another key's check took the character over (C1): nothing this key signed is sent again.</summary>
    private void TakenOver(SharingCharacter entry)
    {
        PublicationSend.Clear(publications, entry.Slot);
        if (Save(Replaced(entry.Unbound(SharingStage.TakenOver)), entry.ContentId))
        {
            Publish(view.With(clearConsent: true, notice: new SharingNotice(entry.ContentId, SharingNoticeKind.TakenOver)));
        }
    }

    /// <summary>Reads the server's status once a session; false, with a notice, when it can't be read or needs a newer AetherFrame.</summary>
    private bool StatusAllows(ulong contentId)
    {
        if (statusChecked)
        {
            return true;
        }

        try
        {
            var status = client.StatusAsync(stopping).GetAwaiter().GetResult();
            if (status.Status != HttpStatusCode.OK)
            {
                Notify(contentId, Failure(status.Status));
                return false;
            }

            if (SharingWire.ReadMinimumPlugin(status.Body) > pluginVersion)
            {
                Notify(contentId, SharingNoticeKind.UpdateNeeded);
                return false;
            }
        }
        catch (SharingException)
        {
            Notify(contentId, SharingNoticeKind.Unreachable);
            return false;
        }
        catch (InvalidDataException)
        {
            Notify(contentId, SharingNoticeKind.Refused);
            return false;
        }

        statusChecked = true;
        return true;
    }

    private void RequestCode(PersonaManager manager, SharingCharacter entry, PersonaPublicKey key)
    {
        var response = Send(manager, entry, key, RequestProofKind.LodestoneCode, SharingWire.Empty());
        if (response is null)
        {
            return;
        }

        if (response.Status != HttpStatusCode.OK)
        {
            Notify(entry.ContentId, Failure(response.Status));
            return;
        }

        try
        {
            var code = SharingWire.ReadCode(response.Body);
            Publish(view.With(code: new IssuedCode(entry.ContentId, code, utcNow().AddHours(1)), notice: new SharingNotice(entry.ContentId, SharingNoticeKind.CodeReady)));
        }
        catch (InvalidDataException)
        {
            Notify(entry.ContentId, SharingNoticeKind.Refused);
        }
    }

    /// <summary>Sends the opt-out request and, once the server confirms, records sharing as off. False when it stopped.</summary>
    private bool TurnOff(PersonaManager manager, SharingCharacter entry)
    {
        if (Key(manager, entry) is not { } key)
        {
            return false;
        }

        var response = Send(manager, entry, key, RequestProofKind.OptOut, SharingWire.Empty());
        if (response is null)
        {
            return false;
        }

        if (response.Status != HttpStatusCode.NoContent)
        {
            Notify(entry.ContentId, Failure(response.Status));
            return false;
        }

        PublicationSend.Clear(publications, entry.Slot);
        if (!Save(Replaced(entry.Unbound(SharingStage.Off)), entry.ContentId))
        {
            return false;
        }

        var code = view.Code is { } issued && issued.ContentId == entry.ContentId;
        Publish(view.With(clearCode: code, clearConsent: true, notice: new SharingNotice(entry.ContentId, SharingNoticeKind.TurnedOff)));
        return true;
    }

    /// <summary>
    /// The character's key, selected so it can sign (L10), or null with a notice when it can't be:
    /// the registry no longer holds it, or holds another key under its slot.
    /// </summary>
    private PersonaPublicKey? Key(PersonaManager manager, SharingCharacter entry)
    {
        if (!manager.TryGet(entry.Slot, out var persona) || !persona!.PublicKey.Id.Equals(entry.Key))
        {
            Notify(entry.ContentId, SharingNoticeKind.KeyUnavailable);
            return null;
        }

        if (manager.Active?.Slot != entry.Slot)
        {
            manager.Select(entry.Slot);
        }

        return persona.PublicKey;
    }

    /// <summary>
    /// Checks the server's version once a session, then sends one signed action. Null, with a notice,
    /// when it got no answer it can use, when this AetherFrame is too old, or when the key can't sign;
    /// a <c>410</c> records the takeover (C1) before it is returned.
    /// </summary>
    private SharingResponse? Send(PersonaManager manager, SharingCharacter entry, PersonaPublicKey key, RequestProofKind kind, byte[] body)
    {
        if (!StatusAllows(entry.ContentId))
        {
            return null;
        }

        try
        {
            var signer = new LeasedSigner(manager, entry.Slot, key);
            var response = client.ActionAsync(kind, body, signer, stopping).GetAwaiter().GetResult();
            log($"Sharing: {SharingClient.PathOf(kind)} answered {(int)response.Status}.");
            if (response.Status == HttpStatusCode.Gone)
            {
                TakenOver(entry);
                return null;
            }

            return response;
        }
        catch (SharingException exception)
        {
            log($"Sharing: {SharingClient.PathOf(kind)} got no usable answer ({(exception.Status is { } status ? ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture) : "none")}).");
            Notify(entry.ContentId, SharingNoticeKind.Unreachable);
            return null;
        }
        catch (InvalidDataException)
        {
            Notify(entry.ContentId, SharingNoticeKind.Refused);
            return null;
        }
        catch (LeasedSignerException exception)
        {
            log($"Sharing: the character's key couldn't sign ({exception.Availability}).");
            Notify(entry.ContentId, SharingNoticeKind.KeyUnavailable);
            return null;
        }
    }

    /// <summary>The file's characters with <paramref name="entry"/> in place of its character's, or added.</summary>
    private IReadOnlyList<SharingCharacter> Replaced(SharingCharacter entry)
    {
        var characters = new List<SharingCharacter>();
        var replaced = false;
        foreach (var character in view.Characters)
        {
            if (character.ContentId == entry.ContentId)
            {
                characters.Add(entry);
                replaced = true;
            }
            else
            {
                characters.Add(character);
            }
        }

        if (!replaced)
        {
            characters.Add(entry);
        }

        return characters;
    }

    /// <summary>Saves <paramref name="characters"/> and shows them; false, with a notice, when the save failed.</summary>
    private bool Save(IReadOnlyList<SharingCharacter> characters, ulong contentId)
    {
        try
        {
            file.Replace(characters);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log($"Sharing: the sharing file couldn't be saved ({exception.GetType().Name}).");
            Notify(contentId, SharingNoticeKind.SaveFailed);
            return false;
        }

        Publish(view.With(characters: characters));
        return true;
    }

    private void Notify(ulong contentId, SharingNoticeKind kind, string? detail = null) => Publish(view.With(notice: new SharingNotice(contentId, kind, detail)));

    private void Publish(CharacterSharingView next)
    {
        lock (gate)
        {
            view = next;
        }
    }

    /// <summary>
    /// Hands <paramref name="work"/> to the persona session, when nothing of this service runs and
    /// the file was read (or this is the read). An exception the work didn't expect becomes a
    /// notice, and the log gets its type only.
    /// </summary>
    private bool Run(string name, Action<PersonaManager> work, bool loading = false)
    {
        lock (gate)
        {
            if (view.Busy || (!loading && (!view.Loaded || view.Unreadable)) || (loading && view.Loaded && !view.Unreadable))
            {
                return false;
            }

            view = view.With(busy: true, clearNotice: true);
        }

        bool started;
        try
        {
            started = tryRun(name, manager =>
            {
                try
                {
                    work(manager);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    log($"Sharing: {name} failed ({exception.GetType().Name}).");
                    if (loading)
                    {
                        Publish(view.With(loaded: true, unreadable: true));
                    }
                    else
                    {
                        Notify(0, SharingNoticeKind.Failed);
                    }
                }
                finally
                {
                    Publish(view.With(busy: false));
                }
            });
        }
        catch
        {
            started = false;
        }

        if (!started)
        {
            Publish(view.With(busy: false));
        }

        return started;
    }
}

/// <summary>
/// A signer for one character's key that opens a lease for each signature and releases it at once
/// (L10: a lease is never held across a request). It signs only while that key is the selected one.
/// </summary>
internal sealed class LeasedSigner(PersonaManager manager, PersonaSlotId slot, PersonaPublicKey key) : IPersonaSigner
{
    public PersonaPublicKey PublicKey => key;

    public ProtocolSignature Sign(SigningInput input)
    {
        var availability = manager.TryOpenSigner(slot, key, out var lease);
        if (availability != PersonaSignerAvailability.Available || lease is null)
        {
            throw new LeasedSignerException(availability);
        }

        using (lease)
        {
            return lease.Signer.Sign(input);
        }
    }
}

/// <summary>The character's key couldn't sign: <see cref="Availability"/> says why.</summary>
internal sealed class LeasedSignerException(PersonaSignerAvailability availability) : Exception("The character's key can't sign now.")
{
    internal PersonaSignerAvailability Availability { get; } = availability;
}
