using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using AetherFrame.Personas;
using AetherFrame.Protocol;
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
    TurnedOffAll,
    TurnOffIncomplete,
    NewKeyDropped,
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
    PublishUnrecorded,
    PublishStopped,
    PublishWithdrawn,
    LodestoneRefused,
}

/// <summary>
/// A notice for one character, or for every character when <see cref="ContentId"/> is 0, with the
/// server's reason code for a refused publish, or the commit's result for one not stored. For a
/// revision left waiting, <see cref="Plate"/> is the local Plate it is of, so it is offered to be
/// sent again only while that is still the Active Plate.
/// </summary>
internal sealed record SharingNotice(ulong ContentId, SharingNoticeKind Kind, string? Detail = null, Guid Plate = default);

/// <summary>Where a publish of a character's Active Plate stands.</summary>
internal enum PublishStep
{
    /// <summary>Checking the key and the server, then signing the Plate into the outbox on this PC.</summary>
    Signing,

    /// <summary>Sending the signed Plate to the sharing server.</summary>
    Sending,

    /// <summary>It ended: <see cref="PublishStatus.Outcome"/> says how, or nothing when it had nothing to do (a newer build took its place, or sharing stopped).</summary>
    Ended,
}

/// <summary>
/// One publish of a character's Active Plate (a new build signed and sent, or a waiting revision
/// sent again): where it stands and, once it ended, the notice it left. <see cref="Share"/> is its
/// number, taken when it is handed over; builds (<see cref="CharacterSharing.BuildGeneration"/>)
/// and publishes take their numbers from one count, so a higher number started later.
/// <see cref="Build"/> is the number of the build its candidate came from, the same share carried
/// on, or 0 for a waiting revision sent again. Replaced whole at each step, so a window that keeps
/// one can tell it from the next.
/// </summary>
internal sealed record PublishStatus(ulong ContentId, long Share, PublishStep Step, SharingNotice? Outcome = null, long Build = 0);

/// <summary>A code the server issued for a character's Lodestone check, for the key in <see cref="Slot"/>, kept in memory only.</summary>
internal sealed record IssuedCode(ulong ContentId, PersonaSlotId Slot, string Code, DateTimeOffset Expires);

/// <summary>What the window reads each frame: one immutable value, replaced whole.</summary>
internal sealed class CharacterSharingView
{
    internal static readonly CharacterSharingView Initial = new(false, false, false, Array.Empty<SharingCharacter>(), null, null, null);

    internal CharacterSharingView(bool loaded, bool unreadable, bool busy, IReadOnlyList<SharingCharacter> characters, IssuedCode? code, SharingNotice? notice, PublishStatus? publish, bool connectionNotice = false, bool onlineNotice = false)
    {
        ConnectionNotice = connectionNotice;
        OnlineNotice = onlineNotice;
        Loaded = loaded;
        Unreadable = unreadable;
        Busy = busy;
        Characters = characters;
        Code = code;
        Notice = notice;
        Publish = publish;
    }

    /// <summary>Whether the sharing file was read.</summary>
    internal bool Loaded { get; }

    /// <summary>Whether it couldn't be: nothing more is sent from here, and nothing is written over it.</summary>
    internal bool Unreadable { get; }

    /// <summary>Whether an operation was handed to the persona session and hasn't ended.</summary>
    internal bool Busy { get; }

    internal IReadOnlyList<SharingCharacter> Characters { get; }

    /// <summary>The latest code issued, for the character and key it names.</summary>
    internal IssuedCode? Code { get; }

    internal SharingNotice? Notice { get; }

    /// <summary>The latest publish of an Active Plate, under way or ended; none before the first.</summary>
    internal PublishStatus? Publish { get; }

    /// <summary>
    /// Whether the one-time notice about checking through the player's own connection is due: a
    /// character was already shared from this PC when this build first read the sharing file, and
    /// the player hasn't dismissed it.
    /// </summary>
    internal bool ConnectionNotice { get; }

    /// <summary>
    /// Whether the one-time notice about the online count is due: a character was already shared
    /// from this PC when this build first read the sharing file, and the player hasn't dismissed
    /// it. Until it is, nothing of the online count is sent ("The online count").
    /// </summary>
    internal bool OnlineNotice { get; }

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

    internal CharacterSharingView With(bool? busy = null, IReadOnlyList<SharingCharacter>? characters = null, IssuedCode? code = null, bool clearCode = false, SharingNotice? notice = null, bool clearNotice = false, bool? loaded = null, bool? unreadable = null, PublishStatus? publish = null, bool? connectionNotice = null, bool? onlineNotice = null) =>
        new(loaded ?? Loaded, unreadable ?? Unreadable, busy ?? Busy, characters ?? Characters, clearCode ? null : code ?? Code, clearNotice ? null : notice ?? Notice, publish ?? Publish, connectionNotice ?? ConnectionNotice, onlineNotice ?? OnlineNotice);
}

/// <summary>
/// Opting a character in and out of sharing (NETWORK2's N2-9b; decision batch C, C1 to C4, and V4):
/// the character's own key, made behind the scenes as a persona; the Lodestone code and check; and
/// turning sharing off, which deletes what the server holds. Each change is one persona-session
/// operation (the session's <c>TryRun</c>): one at a time, under the persona files' lock, off the
/// framework thread, and waited for by unloading. Requests go through <see cref="SharingClient"/>,
/// each signed by the character's key through a lease opened for that one signature and released
/// at once, never held across a request (L10). The key is selected only for the operation: the
/// selection it found is put back when the operation ends.
/// <para>
/// The sharing file is saved before a request that depends on it is sent, so a key the server may
/// bind is always recorded first. A key that can't sign sends nothing at all. Opting out is never
/// held back by the server's version check. The log gets operation names and outcome kinds only:
/// never a Content ID, a Lodestone id, a code, a name, a World or a key. Compiled only in the
/// networking preview flavour.
/// </para>
/// </summary>
internal sealed class CharacterSharing
{
    /// <summary>The label every character's key gets in the persona registry: nothing about the character (C1).</summary>
    internal const string KeyLabel = "Character key";

    /// <summary>
    /// How many days after the binding's last read a player's action re-reads the character's page
    /// ("Checking a character through the player's own connection" in the decision register): half
    /// the 30 after which the server stops showing a binding not read since.
    /// </summary>
    internal const int RereadAfterDays = 15;

    /// <summary>The marker, in the persona folder, that the one-time notice about the player's own connection was seen or isn't needed.</summary>
    internal const string ConnectionNoticeFileName = "lodestone-connection-notice.afsh";

    /// <summary>The marker, in the persona folder, that the one-time notice about the online count was seen or isn't needed.</summary>
    internal const string OnlineNoticeFileName = "online-count-notice.afsh";

    /// <summary>The server API version this build speaks (ServerApi-v1.md).</summary>
    private const int Api = 1;

    /// <summary>How long after a re-read started, answered or not, a player's action asks for another.</summary>
    private static readonly TimeSpan RereadRetry = TimeSpan.FromHours(1);

    private readonly Func<string, Action<PersonaManager>, bool> tryRun;
    private readonly SharingStateFile file;
    private readonly PublicationFiles publications;
    private readonly SharingClient client;
    private readonly Version pluginVersion;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<ulong, Guid?> activePlateOf;
    private readonly CancellationToken stopping;
    private readonly Action<string> log;
    private readonly object gate = new();
    private volatile CharacterSharingView view = CharacterSharingView.Initial;

    // The send under way, if any. Guarded by gate.
    private Upload? upload;

    // Each character's build generation: the share number of the latest build of its Active Plate
    // (or of the moment there was nothing left to build), so a candidate built under an older one
    // is out of date. Builds and sends of waiting revisions take their numbers from one count, so
    // shares are numbered in the order they start. Guarded by gate.
    private readonly Dictionary<ulong, long> builds = new();
    private long shares;
    private bool statusChecked;

    // Each character's day of last read, as the server's answers through the pipe gave it this
    // session (UTC days since the Unix epoch); when a re-read last started for it; and the name and
    // World the game showed when a re-read answered with others, which the Lodestone hasn't caught
    // up with yet. Kept in memory only: a day not known yet is due at the player's next action.
    // Guarded by gate.
    private readonly Dictionary<ulong, long> readDays = new();
    private readonly Dictionary<ulong, DateTimeOffset> rereadsStarted = new();
    private readonly Dictionary<ulong, (string Name, string World)> notYetOnTheLodestone = new();

    internal CharacterSharing(
        Func<string, Action<PersonaManager>, bool> tryRun,
        SharingStateFile file,
        PublicationFiles publications,
        SharingClient client,
        Version pluginVersion,
        Func<DateTimeOffset> utcNow,
        CancellationToken stopping,
        Action<string> log,
        Func<ulong, Guid?> activePlateOf)
    {
        this.tryRun = tryRun ?? throw new ArgumentNullException(nameof(tryRun));
        this.file = file ?? throw new ArgumentNullException(nameof(file));
        this.publications = publications ?? throw new ArgumentNullException(nameof(publications));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.pluginVersion = pluginVersion ?? throw new ArgumentNullException(nameof(pluginVersion));
        this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        this.stopping = stopping;
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.activePlateOf = activePlateOf ?? throw new ArgumentNullException(nameof(activePlateOf));
    }

    internal CharacterSharingView View => view;

    /// <summary>
    /// Reads the sharing file, when it hasn't been read or couldn't be; false when the session
    /// couldn't start it. Once it is read, what the share check signed and kept on this PC under a
    /// persona that is no character's key is dropped: it is never sent (C3).
    /// </summary>
    internal bool TryLoad() => Run("sharing load", manager =>
    {
        var characters = file.Read();
        var noticeDue = false;
        var checking = false;
        foreach (var character in characters)
        {
            noticeDue |= character.IsBound;

            // A check a build before the online count started counts too: its consent never said
            // what the count sends, so the notice is due before that check can pass (K4).
            checking |= character.Checking;
        }

        var onlineDue = (noticeDue || checking) && !File.Exists(OnlineNoticePath);
        noticeDue &= !File.Exists(ConnectionNoticePath);
        Update(v => v.With(characters: characters, loaded: true, unreadable: false, connectionNotice: noticeDue, onlineNotice: onlineDue));

        var keys = new HashSet<PersonaSlotId>();
        foreach (var character in characters)
        {
            keys.Add(character.Slot);
            if (character.ReplacingKey)
            {
                keys.Add(character.NewSlot);
            }
        }

        var dropped = 0;
        foreach (var persona in manager.Personas)
        {
            // Each persona on its own: a file that can't be read is logged and skipped, and never
            // makes the sharing file itself read as unreadable.
            try
            {
                if (keys.Contains(persona.Slot))
                {
                    // A character's key: outbox files its index doesn't name, and temporary ones,
                    // are deleted, as a load deletes them (P1, N2).
                    var loaded = PublicationLoad.Load(publications, persona.Slot, persona.PublicKey);
                    if (loaded.Result != PublicationLoadResult.Loaded)
                    {
                        log($"Sharing: a character key's publications came to {loaded.Result}.");
                    }
                }
                else if (publications.ReadIndex(persona.Slot) is { } bytes)
                {
                    // The share check's signings go, once: an index already empty stays as it is.
                    var empty = false;
                    try
                    {
                        empty = PublicationIndexCodec.Decode(bytes, persona.Slot).Entries.Count == 0;
                    }
                    catch (PublicationFileException)
                    {
                        // One that can't be read is emptied all the same (C4).
                    }

                    if (!empty && PublicationSend.Clear(publications, persona.Slot))
                    {
                        dropped++;
                    }

                    // And its outbox files no index names, as for a character's key.
                    PublicationLoad.Load(publications, persona.Slot, persona.PublicKey);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PublicationFileException or ArgumentException)
            {
                log($"Sharing: a persona's publications couldn't be tidied ({exception.GetType().Name}).");
            }
        }

        if (dropped > 0)
        {
            log($"Sharing: the share check's signings were dropped for {dropped} persona(s).");
        }
    }, loading: true);

    /// <summary>
    /// Turns sharing on for the character, after the player agreed (C3's consent, with K4's
    /// acknowledgement): its key, made now unless it has one, then a Lodestone code. With
    /// <paramref name="newKey"/>, when its key can't be opened, a new key is made; a character the
    /// server binds keeps that binding recorded until a check with the new key passes (C1).
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

        // Always a new key, never one lying about: a key no entry names may still be bound on the
        // server (a sharing file moved aside), and a key per failed start is harmless.
        persona ??= manager.Create(KeyLabel);
        persona = manager.Acknowledge(persona.Slot);
        var entry = existing is { IsBound: true }
            ? existing with { NewSlot = persona.Slot, NewKey = persona.PublicKey.Id }
            : new SharingCharacter(contentId, persona.Slot, persona.PublicKey.Id, SharingStage.Checking);
        if (Save(Replaced(entry), contentId))
        {
            // The consent the player just agreed to says what the online count sends, so the
            // one-time notice about it is seen. It is dismissed here and not when the check passes:
            // a check already under way when this build arrived agreed to a consent that never
            // mentioned the count, and its player still has to choose Got it (K4).
            DismissOnlineNotice();
            RequestCode(manager, entry);
        }
    });

    /// <summary>Asks for a new code for a character whose check hasn't passed yet.</summary>
    internal bool TryNewCode(ulong contentId) => Run("sharing code", manager =>
    {
        if (view.Find(contentId) is { Checking: true } entry)
        {
            RequestCode(manager, entry);
        }
    });

    /// <summary>
    /// Sends the Lodestone check: the id the player's address named, the code issued for this
    /// character's key, and the <paramref name="name"/> and <paramref name="world"/> the game shows
    /// for the character logged in, which the server requires the page to show.
    /// </summary>
    internal bool TryCheck(ulong contentId, string lodestoneId, string name, string world) => Run("sharing check", manager =>
    {
        if (view.Find(contentId) is not { Checking: true } entry || view.Code is not { } issued || issued.ContentId != contentId || issued.Slot != entry.CheckingSlot
            || !LodestoneAddress.IsId(lodestoneId) || !SharingStateCodec.IsText(name) || !SharingStateCodec.IsText(world))
        {
            return;
        }

        var response = Send(manager, entry, entry.CheckingSlot, entry.CheckingKey, RequestProofKind.LodestoneCheck, SharingWire.Check(lodestoneId, issued.Code, name, world), piped: true);
        if (response is null)
        {
            return;
        }

        if (response.Status != HttpStatusCode.OK)
        {
            Notify(contentId, response.Status == HttpStatusCode.UnprocessableEntity ? SharingNoticeKind.CheckFailed : Failure(response));
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

        var bound = new SharingCharacter(contentId, entry.CheckingSlot, entry.CheckingKey, SharingStage.Shared, lodestoneId, answer.ProfileId, answer.Name, answer.World);
        if (!SameCharacter(bound, name, world))
        {
            // The server checks this too; a binding to another character is undone, not kept. A new
            // key had taken this character's binding over, so the opt-out deleted that too.
            var undone = Send(manager, entry, entry.CheckingSlot, entry.CheckingKey, RequestProofKind.OptOut, SharingWire.Empty());
            if (entry.ReplacingKey && entry.NewKey is { } newKey && undone?.Status == HttpStatusCode.NoContent)
            {
                PublicationSend.Clear(publications, entry.Slot);
                PublicationSend.Clear(publications, entry.NewSlot);
                Save(Replaced(new SharingCharacter(contentId, entry.NewSlot, newKey, SharingStage.Off)), contentId);
            }

            Notify(contentId, SharingNoticeKind.CheckFailed);
            return;
        }

        if (Save(Replaced(bound), contentId))
        {
            Read(contentId, response);

            // The check just read the Lodestone page through the player's own connection, which is
            // what that notice is about. The online count's notice is dismissed by the consent in
            // TryStart instead, since a check started before this build never saw it.
            DismissConnectionNotice();
            Update(v => v.With(clearCode: true, notice: new SharingNotice(contentId, SharingNoticeKind.CheckPassed)));
        }
    });

    /// <summary>The player dismissed the one-time notice about checking through their own connection: it isn't shown again on this PC.</summary>
    internal bool TryDismissConnectionNotice() => Run("sharing notice", _ => DismissConnectionNotice(), keepNotice: true);

    /// <summary>The player dismissed the one-time notice about the online count: it isn't shown again on this PC, and the count may be sent from now on.</summary>
    internal bool TryDismissOnlineNotice() => Run("sharing notice", _ => DismissOnlineNotice(), keepNotice: true);

    /// <summary>
    /// Stops a check under way: a new key that was replacing one that can't be opened is dropped
    /// here, and the binding stays as it was; a character not bound yet is turned off, in case the
    /// server bound it and its answer was lost.
    /// </summary>
    internal bool TryCancelCheck(ulong contentId) => Run("sharing cancel", manager =>
    {
        if (view.Find(contentId) is not { Checking: true } entry)
        {
            return;
        }

        if (!entry.ReplacingKey)
        {
            TurnOff(manager, entry, notify: true);
            return;
        }

        var code = view.Code is { } issued && issued.ContentId == contentId;
        if (Save(Replaced(entry with { NewSlot = default, NewKey = null }), contentId))
        {
            Update(v => v.With(clearCode: code, notice: new SharingNotice(contentId, SharingNoticeKind.NewKeyDropped)));
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
            TurnOff(manager, entry, notify: true);
        }
    });

    /// <summary>
    /// Turns sharing off for every character that has it on, or has a check under way, going on
    /// past any that fails, then says whether all of them were turned off.
    /// </summary>
    internal bool TryTurnOffAll() => Run("sharing off all", manager =>
    {
        var failed = 0;
        foreach (var entry in view.Characters)
        {
            if ((entry.IsBound || entry.Stage == SharingStage.Checking) && !TurnOff(manager, entry, notify: false))
            {
                failed++;
            }
        }

        log($"Sharing: turning every character off left {failed} still on.");
        Notify(0, failed == 0 ? SharingNoticeKind.TurnedOffAll : SharingNoticeKind.TurnOffIncomplete);
    });

    /// <summary>
    /// Publishes <paramref name="candidate"/>, built from the character's Active Plate as it was
    /// saved (C3, as the owner's direction of October 2, 2026 amends it): signed under the
    /// character's key with its binding's profile id (C4) into the outbox, then sent, with no
    /// screen before it. Only a candidate for the character's Active Plate (the service's own
    /// lookup) is signed, and only while the character shares; and like every revision, it is sent
    /// only while its Plate is still the Active Plate (<see cref="SendWaiting"/>). A candidate built
    /// under <paramref name="generation"/> (<see cref="BuildGeneration"/>) that a newer build has
    /// made out of date is never signed when that happened before its signing, and never sent when
    /// it happened before its send began: its revision then waits on this PC, and the newer build's
    /// replaces it. A send that has begun is stopped by <see cref="StopOlderSend"/>, once a newer
    /// candidate is ready to take its place, or by <see cref="StopStaleSends"/>.
    /// </summary>
    internal bool TryPublish(ulong contentId, SnapshotCandidate candidate, long? generation = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return Run("sharing publish", manager =>
        {
            if (!IsCurrent(contentId, generation) || activePlateOf(contentId) != candidate.PlateId)
            {
                return;
            }

            if (view.Find(contentId) is not { Stage: SharingStage.Shared, ReplacingKey: false, ProfileId: { } binding } entry)
            {
                return;
            }

            if (SigningKey(manager, entry) is not { } key || !StatusAllows(contentId))
            {
                return;
            }

            // The checks above may have asked the server: a newer build that started meanwhile
            // takes this candidate's place, and one whose Plate isn't Active any more is never
            // signed.
            if (!IsCurrent(contentId, generation) || activePlateOf(contentId) != candidate.PlateId)
            {
                log("Sharing: the Active Plate changed, so the candidate built before wasn't signed.");
                return;
            }

            var outcome = PublicationCommit.Commit(manager, publications, new PublishConsent(candidate, entry.Slot, key, binding), utcNow);
            log($"Sharing: signing the Active Plate came to {outcome.Result}.");
            if (outcome.Result != PublishResult.Stored)
            {
                Notify(contentId, outcome.Result == PublishResult.KeyUnavailable ? SharingNoticeKind.KeyUnavailable : SharingNoticeKind.PublishNotStored, outcome.Result.ToString());
                return;
            }

            SendWaiting(manager, entry, key, binding, generation);
        }, publishing: contentId, build: generation);
    }

    /// <summary>
    /// Sends the character's waiting revision again, after the server couldn't take it: only while
    /// its Plate is the character's Active Plate, checked at once and again just before the upload.
    /// One of a Plate that isn't Active any more is dropped, never sent.
    /// </summary>
    internal bool TrySendWaiting(ulong contentId) => Run("sharing send", manager =>
    {
        if (view.Find(contentId) is not { Stage: SharingStage.Shared, ReplacingKey: false, ProfileId: { } binding } entry)
        {
            return;
        }

        if (PublicationSend.WaitingPlate(publications, entry.Slot, binding) is { } waiting && waiting != activePlateOf(contentId))
        {
            Withdraw(entry);
            return;
        }

        if (SigningKey(manager, entry) is { } key && StatusAllows(contentId))
        {
            SendWaiting(manager, entry, key, binding, generation: null);
        }
    }, publishing: contentId);

    /// <summary>The character's waiting revision is of a Plate that isn't the Active Plate any more: it is dropped, and the notice says so.</summary>
    private void Withdraw(SharingCharacter entry)
    {
        log("Sharing: a waiting revision was dropped, since its Plate is no longer the Active Plate.");
        PublicationSend.DropAll(publications, entry.Slot);
        Notify(entry.ContentId, SharingNoticeKind.PublishWithdrawn);
    }

    /// <summary>Stops a send under way, whichever character it is for: the revision waits on this PC, and the next save or a try again sends it.</summary>
    internal void StopSending()
    {
        CancellationTokenSource? stop;
        lock (gate)
        {
            stop = upload?.Stop;
        }

        Cancel(stop);
    }

    /// <summary>
    /// A newer candidate for the character is ready, and waits for the service: a send under way
    /// for the character is of an older revision, so it stops and ends with nothing to say. The
    /// revision it leaves waiting is replaced when the newer candidate is signed, which is then sent
    /// next. A send for another character goes on.
    /// </summary>
    internal void StopOlderSend(ulong contentId)
    {
        CancellationTokenSource? stop = null;
        lock (gate)
        {
            if (upload is { GaveWay: false } older && older.ContentId == contentId)
            {
                older.GaveWay = true;
                stop = older.Stop;
            }
        }

        Cancel(stop);
    }

    /// <summary>
    /// Stops the send under way, whichever character it is for, when its Plate is no longer that
    /// character's Active Plate (there is none any more, or another Plate took its place, even
    /// while another character is logged in). Its revision is dropped rather than left to be sent
    /// later, and the notice says so. A send of the Active Plate goes on; a logout or a switch of
    /// characters alone never stops one. The live publisher calls this every frame.
    /// </summary>
    internal void StopStaleSends()
    {
        CancellationTokenSource? stop = null;
        lock (gate)
        {
            if (upload is { GaveWay: false, Withdrawn: false } stale && stale.Plate != activePlateOf(stale.ContentId))
            {
                stale.Withdrawn = true;
                stop = stale.Stop;
            }
        }

        Cancel(stop);
    }

    /// <summary>
    /// Makes every candidate built for the character so far out of date: a newer build of its
    /// Active Plate started, or there is nothing to build (another character, no Active Plate,
    /// sharing stopped). Only the live publisher moves it on; the service's own work never does, so
    /// a build started during a publish is never taken for out of date. The new generation is the
    /// next share number.
    /// </summary>
    internal void Supersede(ulong contentId)
    {
        lock (gate)
        {
            builds[contentId] = ++shares;
        }
    }

    /// <summary>The character's build generation now: a candidate built under it is signed and sent only while it holds.</summary>
    internal long BuildGeneration(ulong contentId)
    {
        lock (gate)
        {
            return builds.GetValueOrDefault(contentId);
        }
    }

    /// <summary>Whether a candidate built under <paramref name="generation"/> is still the character's latest; one handed over without a generation always is.</summary>
    private bool IsCurrent(ulong contentId, long? generation) => generation is not { } built || BuildGeneration(contentId) == built;

    private static void Cancel(CancellationTokenSource? stop)
    {
        try
        {
            stop?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The send ended meanwhile.
        }
    }

    /// <summary>
    /// Pauses sharing for the character (C3): the server deletes its Plate and keeps the binding,
    /// and nothing it signed waits to be sent afterwards.
    /// </summary>
    internal bool TryPause(ulong contentId) => Run("sharing pause", manager =>
    {
        if (view.Find(contentId) is not { Stage: SharingStage.Shared } entry)
        {
            return;
        }

        var response = Send(manager, entry, entry.Slot, entry.Key, RequestProofKind.OptOut, SharingWire.Pause());
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
        if (Save(Replaced(entry with { Stage = SharingStage.Paused, PublishedPlate = null }), contentId))
        {
            Notify(contentId, SharingNoticeKind.Paused);
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
    /// Whether a player's action should ask for the character's re-read now ("Checking a character
    /// through the player's own connection" in the decision register): it is bound, and the game
    /// shows it under another name or World than its binding's (C1) and a re-read this session
    /// hasn't already found the page without them, or its last read is <see cref="RereadAfterDays"/>
    /// days old or more, or not known this session. No re-read is asked for within the hour after
    /// the last one started, answered or not, and none before the player has seen the one-time
    /// notice about their own connection. Nothing is sent: a caller that finds it due calls
    /// <see cref="TryReread"/> during the player's action, never at login.
    /// </summary>
    internal bool RereadDue(ulong contentId, string name, string world)
    {
        var current = view;
        if (current.ConnectionNotice || current.Find(contentId) is not { IsBound: true } entry)
        {
            return false;
        }

        var now = utcNow();
        lock (gate)
        {
            if (rereadsStarted.TryGetValue(contentId, out var started) && now - started < RereadRetry)
            {
                return false;
            }

            var renamed = !SameCharacter(entry, name, world)
                && !(notYetOnTheLodestone.TryGetValue(contentId, out var shown) && SameCharacter(entry with { Name = shown.Name, World = shown.World }, name, world));
            return renamed || !readDays.TryGetValue(contentId, out var day) || DayOf(now) - day >= RereadAfterDays;
        }
    }

    /// <summary>
    /// Asks the server to read the character's Lodestone page again, through the player's own
    /// connection, when <see cref="RereadDue"/> says so; nothing is sent otherwise. Only during a
    /// player's action: a save that publishes, opening the Sharing window, or looking up another
    /// player's Plate. A rename or a World transfer the page shows is recorded (C1). When the
    /// server no longer finds the character bound to this key, the key opts out too, so nothing it
    /// held stays behind.
    /// </summary>
    internal bool TryReread(ulong contentId, string name, string world) => Run("sharing reread", manager =>
    {
        if (view.Find(contentId) is not { IsBound: true } entry || !RereadDue(contentId, name, world))
        {
            return;
        }

        lock (gate)
        {
            rereadsStarted[contentId] = utcNow();
        }

        var response = Send(manager, entry, entry.Slot, entry.Key, RequestProofKind.LodestoneReread, SharingWire.Empty(), piped: true);
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
                    Read(contentId, response);
                    lock (gate)
                    {
                        // A name or World the page doesn't show yet isn't asked about again this
                        // session: the Lodestone can take a while to catch up with the game.
                        if (SameCharacter(entry with { Name = readName, World = readWorld }, name, world))
                        {
                            notYetOnTheLodestone.Remove(contentId);
                        }
                        else
                        {
                            notYetOnTheLodestone[contentId] = (name, world);
                        }
                    }

                    if (entry.Name == readName && entry.World == readWorld)
                    {
                        // Read again, and found as it was: nothing changes here.
                        break;
                    }

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
                // The server also answers 404 for a character taken off the test's allowlist, whose
                // binding it keeps: opting out removes that too, before sharing is recorded as off.
                var optOut = Send(manager, entry, entry.Slot, entry.Key, RequestProofKind.OptOut, SharingWire.Empty());
                if (optOut?.Status == HttpStatusCode.NoContent)
                {
                    PublicationSend.Clear(publications, entry.Slot);
                    if (Save(Replaced(entry.Unbound(SharingStage.Off)), contentId))
                    {
                        Notify(contentId, SharingNoticeKind.NoLongerBound);
                    }
                }
                else if (optOut is { } answered && answered.Status != HttpStatusCode.NoContent)
                {
                    Notify(contentId, Failure(answered));
                }

                break;
            default:
                Notify(contentId, Failure(response));
                break;
        }
    }, keepNotice: true);

    /// <summary>Whether the game's <paramref name="name"/> and <paramref name="world"/> are the binding's, compared as the server compares them (C1).</summary>
    internal static bool SameCharacter(SharingCharacter entry, string name, string world) =>
        string.Equals(Canonical(entry.Name), Canonical(name), StringComparison.Ordinal)
        && string.Equals(entry.World, world, StringComparison.OrdinalIgnoreCase);

    private static string Canonical(string? name)
    {
        var parts = (name ?? "").Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }

    /// <summary>What a failed answer means: the Lodestone turning the player's own connection away, or what its status says.</summary>
    private static SharingNoticeKind Failure(SharingResponse response) =>
        response.LodestoneRefused ? SharingNoticeKind.LodestoneRefused : Failure(response.Status);

    private static SharingNoticeKind Failure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => SharingNoticeKind.TooMany,
        HttpStatusCode.ServiceUnavailable => SharingNoticeKind.TryLater,
        HttpStatusCode.Gone => SharingNoticeKind.TakenOver,
        _ => SharingNoticeKind.Refused,
    };

    /// <summary>Whether the key in <paramref name="slot"/> with <paramref name="key"/> signs now: selected, then a lease opened and released at once.</summary>
    private static bool KeyOpens(PersonaManager manager, PersonaSlotId slot, PersonaPublicKey key)
    {
        if (manager.Active?.Slot != slot)
        {
            manager.Select(slot);
        }

        if (manager.TryOpenSigner(slot, key, out var lease) != PersonaSignerAvailability.Available || lease is null)
        {
            return false;
        }

        lease.Dispose();
        return true;
    }

    /// <summary>The character's key, selected and able to sign, or null with a notice.</summary>
    private PersonaPublicKey? SigningKey(PersonaManager manager, SharingCharacter entry)
    {
        if (manager.TryGet(entry.Slot, out var persona) && persona!.PublicKey.Id.Equals(entry.Key) && KeyOpens(manager, entry.Slot, persona.PublicKey))
        {
            return persona.PublicKey;
        }

        Notify(entry.ContentId, SharingNoticeKind.KeyUnavailable);
        return null;
    }

    /// <summary>Sends the waiting revision and says what came of it; a takeover is recorded as <see cref="Send"/> records one.</summary>
    private void SendWaiting(PersonaManager manager, SharingCharacter entry, PersonaPublicKey key, ProfileId binding, long? generation)
    {
        SendOutcome sent;
        bool stopped;
        Upload? mine = null;
        using (var sending = CancellationTokenSource.CreateLinkedTokenSource(stopping))
        {
            // Asked just before the upload, with the Plate the waiting revision is of. Checked and
            // recorded together, under the lock: a revision is sent only while its Plate is the
            // character's Active Plate (otherwise it is dropped), and only while no newer build has
            // started (otherwise it waits, and the newer build's replaces it). Once recorded, a
            // newer candidate finds the send to stop (StopOlderSend), and so does its Plate no
            // longer being the Active Plate (StopStaleSends).
            SendAdmission Admit(Guid plate)
            {
                lock (gate)
                {
                    if (activePlateOf(entry.ContentId) != plate)
                    {
                        return SendAdmission.NotActive;
                    }

                    if (generation is { } built && builds.GetValueOrDefault(entry.ContentId) != built)
                    {
                        return SendAdmission.Superseded;
                    }

                    mine = new Upload(sending, entry.ContentId, plate);
                    upload = mine;
                    if (view.Publish is { Step: PublishStep.Signing } publish && publish.ContentId == entry.ContentId)
                    {
                        view = view.With(publish: publish with { Step = PublishStep.Sending });
                    }

                    return SendAdmission.Send;
                }
            }

            try
            {
                sent = PublicationSend.SendWaiting(manager, publications, client, entry.Slot, key, binding, utcNow, Admit, sending.Token);
            }
            finally
            {
                lock (gate)
                {
                    if (mine is not null && ReferenceEquals(upload, mine))
                    {
                        upload = null;
                    }
                }
            }

            stopped = sending.IsCancellationRequested && !stopping.IsCancellationRequested;
        }

        bool gaveWay;
        bool withdrawn;
        lock (gate)
        {
            gaveWay = mine?.GaveWay == true;
            withdrawn = mine?.Withdrawn == true;
        }

        // The server's answer, or none: a busy server and no answer in time look alike to the player.
        log($"Sharing: sending the Active Plate came to {sent.Result} (answer: {(sent.Status is { } status ? ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture) : "none")}).");
        if (sent.Result == SendResult.Superseded)
        {
            log("Sharing: a newer build of the Active Plate started, so the one just signed waits for it.");
            return;
        }

        if (sent.Result == SendResult.Withdrawn)
        {
            // Dropped unsent: its Plate isn't the Active Plate any more.
            Notify(entry.ContentId, SharingNoticeKind.PublishWithdrawn, plate: sent.Plate);
            return;
        }

        if (gaveWay && sent.Result == SendResult.TryLater)
        {
            // Stopped for a newer candidate, which replaces the revision left waiting: nothing to say.
            log("Sharing: the send gave way to a newer build of the Active Plate.");
            return;
        }

        if (withdrawn && sent.Result == SendResult.TryLater)
        {
            // Stopped because its Plate is no longer the Active Plate: it is never sent later.
            log("Sharing: the send stopped, since its Plate is no longer the Active Plate.");
            PublicationSend.DropAll(publications, entry.Slot);
            Notify(entry.ContentId, SharingNoticeKind.PublishWithdrawn, plate: sent.Plate);
            return;
        }

        switch (sent.Result)
        {
            case SendResult.Sent:
                if (Save(Replaced(entry with { PublishedPlate = sent.Plate }), entry.ContentId))
                {
                    Notify(entry.ContentId, SharingNoticeKind.Published);
                }

                break;
            case SendResult.NotSaved:
                Save(Replaced(entry with { PublishedPlate = sent.Plate }), entry.ContentId);
                Notify(entry.ContentId, SharingNoticeKind.PublishUnrecorded);
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
                Notify(entry.ContentId, stopped ? SharingNoticeKind.PublishStopped : SharingNoticeKind.PublishWaiting, plate: sent.Plate);
                break;
            case SendResult.KeyUnavailable:
                Notify(entry.ContentId, SharingNoticeKind.KeyUnavailable);
                break;
            default:
                Notify(entry.ContentId, SharingNoticeKind.Failed);
                break;
        }
    }

    /// <summary>
    /// A viewing request signed by <paramref name="key"/> for the character with
    /// <paramref name="contentId"/> was answered 410 (C1): it is recorded as taken over, as any other
    /// request's 410 is. Only ever called inside a persona-session operation.
    /// </summary>
    internal void ViewingTakenOver(ulong contentId, PersonaId key)
    {
        if (view.Find(contentId) is { IsBound: true } entry && entry.Key.Equals(key))
        {
            log("Sharing: a viewing request found the character taken over.");
            TakenOver(entry);
        }
    }

    /// <summary>Records that another key's check took the character over (C1): nothing this key signed is sent again.</summary>
    private void TakenOver(SharingCharacter entry)
    {
        PublicationSend.Clear(publications, entry.Slot);
        if (Save(Replaced(entry.Unbound(SharingStage.TakenOver)), entry.ContentId))
        {
            Notify(entry.ContentId, SharingNoticeKind.TakenOver);
        }
    }

    private void RequestCode(PersonaManager manager, SharingCharacter entry)
    {
        var response = Send(manager, entry, entry.CheckingSlot, entry.CheckingKey, RequestProofKind.LodestoneCode, SharingWire.Empty());
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
            var (code, seconds) = SharingWire.ReadCode(response.Body);
            var issued = new IssuedCode(entry.ContentId, entry.CheckingSlot, code, utcNow().AddSeconds(seconds));
            Update(v => v.With(code: issued, notice: new SharingNotice(entry.ContentId, SharingNoticeKind.CodeReady)));
        }
        catch (InvalidDataException)
        {
            Notify(entry.ContentId, SharingNoticeKind.Refused);
        }
    }

    /// <summary>Sends the opt-out request with the character's key and, once the server confirms, records sharing as off. False when it stopped.</summary>
    private bool TurnOff(PersonaManager manager, SharingCharacter entry, bool notify)
    {
        var response = Send(manager, entry, entry.Slot, entry.Key, RequestProofKind.OptOut, SharingWire.Empty());
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
        Update(v => notify ? v.With(clearCode: code, notice: new SharingNotice(entry.ContentId, SharingNoticeKind.TurnedOff)) : v.With(clearCode: code));
        return true;
    }

    /// <summary>
    /// Sends one signed action with the key in <paramref name="slot"/>. A key that isn't the
    /// registry's, or can't sign now, sends nothing at all. Before anything but an opt-out, the
    /// server's version is checked once a session. Null, with a notice, when it got no answer it
    /// can use; a <c>410</c> records the takeover (C1) before it is returned as null.
    /// </summary>
    private SharingResponse? Send(PersonaManager manager, SharingCharacter entry, PersonaSlotId slot, PersonaId keyId, RequestProofKind kind, byte[] body, bool piped = false)
    {
        if (!manager.TryGet(slot, out var persona) || !persona!.PublicKey.Id.Equals(keyId) || !KeyOpens(manager, slot, persona.PublicKey))
        {
            log($"Sharing: {SharingClient.PathOf(kind)} wasn't sent: the key can't sign.");
            Notify(entry.ContentId, SharingNoticeKind.KeyUnavailable);
            return null;
        }

        if (kind != RequestProofKind.OptOut && !StatusAllows(entry.ContentId))
        {
            return null;
        }

        try
        {
            var signer = new LeasedSigner(manager, slot, persona.PublicKey);
            var response = piped
                ? client.PipedActionAsync(kind, body, signer, stopping).GetAwaiter().GetResult()
                : client.ActionAsync(kind, body, signer, stopping).GetAwaiter().GetResult();
            log($"Sharing: {SharingClient.PathOf(kind)}{(piped ? " (piped)" : "")} answered {(int)response.Status}{(response.LodestoneRefused ? " (the Lodestone refused the connection)" : "")}.");
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
        catch (LeasedSignerException exception)
        {
            log($"Sharing: the character's key couldn't sign ({exception.Availability}).");
            Notify(entry.ContentId, SharingNoticeKind.KeyUnavailable);
            return null;
        }
    }

    /// <summary>Reads the server's status once a session; false, with a notice, when it can't be read or this AetherFrame is too old for it.</summary>
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

            var (protocol, api, minimum) = SharingWire.ReadStatus(status.Body);
            if (protocol != ProtocolConstants.ProtocolVersion || api != Api || minimum > pluginVersion)
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

        Update(v => v.With(characters: characters));
        return true;
    }

    private string ConnectionNoticePath => Path.Combine(file.Folder, ConnectionNoticeFileName);

    private string OnlineNoticePath => Path.Combine(file.Folder, OnlineNoticeFileName);

    /// <summary>The UTC day of <paramref name="time"/>, in days since the Unix epoch, as the server counts a binding's last read.</summary>
    internal static long DayOf(DateTimeOffset time) => (long)Math.Floor(time.ToUnixTimeSeconds() / 86400.0);

    /// <summary>Records a successful read's day for the character: the server's, or today's when its answer carries none.</summary>
    private void Read(ulong contentId, SharingResponse response)
    {
        var day = response.ReadDay ?? DayOf(utcNow());
        lock (gate)
        {
            readDays[contentId] = day;
        }
    }

    /// <summary>Marks the one-time notice as seen. A marker that can't be written only means it shows again next time.</summary>
    private void DismissConnectionNotice()
    {
        try
        {
            Directory.CreateDirectory(file.Folder);
            File.WriteAllBytes(ConnectionNoticePath, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log($"Sharing: the notice's marker couldn't be written ({exception.GetType().Name}).");
        }

        Update(v => v.With(connectionNotice: false));
    }

    /// <summary>
    /// Marks the one-time notice about the online count as seen. A marker that can't be written only
    /// means it shows again next time, and nothing of the count is sent until it is seen again.
    /// </summary>
    private void DismissOnlineNotice()
    {
        try
        {
            Directory.CreateDirectory(file.Folder);
            File.WriteAllBytes(OnlineNoticePath, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log($"Sharing: the online count notice's marker couldn't be written ({exception.GetType().Name}).");
        }

        Update(v => v.With(onlineNotice: false));
    }

    private void Notify(ulong contentId, SharingNoticeKind kind, string? detail = null, Guid plate = default) => Update(v => v.With(notice: new SharingNotice(contentId, kind, detail, plate)));

    /// <summary>Changes the view, inside the lock, from the view as it is then: a change made on another thread is never lost.</summary>
    private void Update(Func<CharacterSharingView, CharacterSharingView> change)
    {
        lock (gate)
        {
            view = change(view);
        }
    }

    /// <summary>
    /// Hands <paramref name="work"/> to the persona session, when nothing of this service runs and
    /// the file was read (or this is the read). The persona selected before the work is selected
    /// again after it. An exception the work didn't expect becomes a notice, and the log gets its
    /// type only. With <paramref name="publishing"/>, the work is a publish of that character's
    /// Active Plate: it takes the next share number and is shown as signing from the moment it is
    /// handed over, carrying on the share of <paramref name="build"/> (the build its candidate came
    /// from) when there is one, and it ends with the notice the work left for that character, or
    /// none.
    /// </summary>
    private bool Run(string name, Action<PersonaManager> work, bool loading = false, bool keepNotice = false, ulong? publishing = null, long? build = null)
    {
        lock (gate)
        {
            if (view.Busy || (!loading && (!view.Loaded || view.Unreadable)) || (loading && view.Loaded && !view.Unreadable))
            {
                return false;
            }

            var publish = publishing is { } contentId ? new PublishStatus(contentId, ++shares, PublishStep.Signing, Build: build ?? 0) : null;
            view = keepNotice ? view.With(busy: true, publish: publish) : view.With(busy: true, clearNotice: true, publish: publish);
        }

        bool started;
        try
        {
            started = tryRun(name, manager =>
            {
                var selected = manager.Active?.Slot;
                try
                {
                    work(manager);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    log($"Sharing: {name} failed ({exception.GetType().Name}).");
                    if (loading)
                    {
                        Update(v => v.With(loaded: true, unreadable: true));
                    }
                    else
                    {
                        Notify(0, SharingNoticeKind.Failed);
                    }
                }
                finally
                {
                    Reselect(manager, selected);
                    Update(Idle);
                }
            });
        }
        catch
        {
            started = false;
        }

        if (!started)
        {
            Update(Idle);
        }

        return started;
    }

    /// <summary>
    /// The view once an operation ended: no longer busy, and when it was a publish, that publish
    /// ended with the notice it left for its character (or for every character), or none. Only one
    /// operation runs at a time, so a publish still under way here is this operation's own.
    /// </summary>
    private static CharacterSharingView Idle(CharacterSharingView view)
    {
        if (view.Publish is not { Step: not PublishStep.Ended } publish)
        {
            return view.With(busy: false);
        }

        var outcome = view.Notice is { } notice && (notice.ContentId == publish.ContentId || notice.ContentId == 0) ? notice : null;
        return view.With(busy: false, publish: publish with { Step = PublishStep.Ended, Outcome = outcome });
    }

    /// <summary>
    /// A send under way: how to stop it, the character and the local Plate it is for, and whether
    /// a newer candidate, or its Plate no longer being the Active Plate, stopped it (both guarded by
    /// the service's lock).
    /// </summary>
    private sealed class Upload(CancellationTokenSource stop, ulong contentId, Guid plate)
    {
        internal CancellationTokenSource Stop { get; } = stop;

        internal ulong ContentId { get; } = contentId;

        internal Guid Plate { get; } = plate;

        internal bool GaveWay { get; set; }

        internal bool Withdrawn { get; set; }
    }

    /// <summary>Puts back the selection an operation found: the persona it named, or none.</summary>
    private void Reselect(PersonaManager manager, PersonaSlotId? selected) => Reselect(manager, selected, log);

    /// <summary>Puts back the selection an operation found, for any persona-session operation of sharing's (the online count's too): the persona it named, or none.</summary>
    internal static void Reselect(PersonaManager manager, PersonaSlotId? selected, Action<string> log)
    {
        try
        {
            if (manager.Active?.Slot == selected)
            {
                return;
            }

            if (selected is { } slot && manager.TryGet(slot, out _))
            {
                manager.Select(slot);
            }
            else
            {
                manager.Deselect();
            }
        }
        catch (PersonaException exception)
        {
            log($"Sharing: the persona selection couldn't be put back ({exception.Error}).");
        }
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
