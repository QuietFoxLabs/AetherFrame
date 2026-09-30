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
}

/// <summary>A notice for one character, or for every character when <see cref="ContentId"/> is 0.</summary>
internal sealed record SharingNotice(ulong ContentId, SharingNoticeKind Kind);

/// <summary>A code the server issued for a character's Lodestone check, for the key in <see cref="Slot"/>, kept in memory only.</summary>
internal sealed record IssuedCode(ulong ContentId, PersonaSlotId Slot, string Code, DateTimeOffset Expires);

/// <summary>What the window reads each frame: one immutable value, replaced whole.</summary>
internal sealed class CharacterSharingView
{
    internal static readonly CharacterSharingView Initial = new(false, false, false, Array.Empty<SharingCharacter>(), null, null);

    internal CharacterSharingView(bool loaded, bool unreadable, bool busy, IReadOnlyList<SharingCharacter> characters, IssuedCode? code, SharingNotice? notice)
    {
        Loaded = loaded;
        Unreadable = unreadable;
        Busy = busy;
        Characters = characters;
        Code = code;
        Notice = notice;
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

    internal CharacterSharingView With(bool? busy = null, IReadOnlyList<SharingCharacter>? characters = null, IssuedCode? code = null, bool clearCode = false, SharingNotice? notice = null, bool clearNotice = false, bool? loaded = null, bool? unreadable = null) =>
        new(loaded ?? Loaded, unreadable ?? Unreadable, busy ?? Busy, characters ?? Characters, clearCode ? null : code ?? Code, clearNotice ? null : notice ?? Notice);
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

    /// <summary>The server API version this build speaks (ServerApi-v1.md).</summary>
    private const int Api = 1;

    private readonly Func<string, Action<PersonaManager>, bool> tryRun;
    private readonly SharingStateFile file;
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
        SharingClient client,
        Version pluginVersion,
        Func<DateTimeOffset> utcNow,
        CancellationToken stopping,
        Action<string> log)
    {
        this.tryRun = tryRun ?? throw new ArgumentNullException(nameof(tryRun));
        this.file = file ?? throw new ArgumentNullException(nameof(file));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.pluginVersion = pluginVersion ?? throw new ArgumentNullException(nameof(pluginVersion));
        this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        this.stopping = stopping;
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal CharacterSharingView View => view;

    /// <summary>Reads the sharing file, when it hasn't been read or couldn't be; false when the session couldn't start it.</summary>
    internal bool TryLoad() => Run("sharing load", _ =>
    {
        var characters = file.Read();
        Publish(view.With(characters: characters, loaded: true, unreadable: false));
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

        persona ??= FreshKey(manager);
        persona = manager.Acknowledge(persona.Slot);
        var entry = existing is { IsBound: true }
            ? existing with { NewSlot = persona.Slot, NewKey = persona.PublicKey.Id }
            : new SharingCharacter(contentId, persona.Slot, persona.PublicKey.Id, SharingStage.Checking);
        if (Save(Replaced(entry), contentId))
        {
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

        var response = Send(manager, entry, entry.CheckingSlot, entry.CheckingKey, RequestProofKind.LodestoneCheck, SharingWire.Check(lodestoneId, issued.Code, name, world));
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

        var bound = new SharingCharacter(contentId, entry.CheckingSlot, entry.CheckingKey, SharingStage.Shared, lodestoneId, answer.ProfileId, answer.Name, answer.World);
        if (!SameCharacter(bound, name, world))
        {
            // The server checks this too; a binding to another character is undone, not kept.
            Send(manager, entry, entry.CheckingSlot, entry.CheckingKey, RequestProofKind.OptOut, SharingWire.Empty());
            Notify(contentId, SharingNoticeKind.CheckFailed);
            return;
        }

        if (Save(Replaced(bound), contentId))
        {
            Publish(view.With(clearCode: true, notice: new SharingNotice(contentId, SharingNoticeKind.CheckPassed)));
        }
    });

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
            Publish(view.With(clearCode: code, notice: new SharingNotice(contentId, SharingNoticeKind.NewKeyDropped)));
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
    /// Asks the server to read the character's Lodestone page again, when the game's name or World
    /// differs from the binding's (C1). Nothing is sent otherwise. When the server no longer finds
    /// the character bound to this key, the key opts out too, so nothing it held stays behind.
    /// </summary>
    internal bool TryReread(ulong contentId, string name, string world) => Run("sharing reread", manager =>
    {
        if (view.Find(contentId) is not { IsBound: true } entry || SameCharacter(entry, name, world))
        {
            return;
        }

        var response = Send(manager, entry, entry.Slot, entry.Key, RequestProofKind.LodestoneReread, SharingWire.Empty());
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
                // The server also answers 404 for a character taken off the test's allowlist, whose
                // binding it keeps: opting out removes that too, before sharing is recorded as off.
                var optOut = Send(manager, entry, entry.Slot, entry.Key, RequestProofKind.OptOut, SharingWire.Empty());
                if (optOut?.Status == HttpStatusCode.NoContent && Save(Replaced(entry.Unbound(SharingStage.Off)), contentId))
                {
                    Notify(contentId, SharingNoticeKind.NoLongerBound);
                }
                else if (optOut is { } answered && answered.Status != HttpStatusCode.NoContent)
                {
                    Notify(contentId, Failure(answered.Status));
                }

                break;
            default:
                Notify(contentId, Failure(response.Status));
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

    /// <summary>
    /// A key for a character: a "Character key" persona no character names, when one signs (left by
    /// a start whose save failed, say), or else a new one. A key that can't be opened is never reused.
    /// </summary>
    private PersonaRecord FreshKey(PersonaManager manager)
    {
        var named = new HashSet<PersonaSlotId>();
        foreach (var character in view.Characters)
        {
            named.Add(character.Slot);
            if (character.ReplacingKey)
            {
                named.Add(character.NewSlot);
            }
        }

        foreach (var persona in manager.Personas)
        {
            if (string.Equals(persona.Label, KeyLabel, StringComparison.Ordinal) && !named.Contains(persona.Slot) && KeyOpens(manager, persona.Slot, persona.PublicKey))
            {
                return persona;
            }
        }

        return manager.Create(KeyLabel);
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
            Publish(view.With(code: issued, notice: new SharingNotice(entry.ContentId, SharingNoticeKind.CodeReady)));
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

        if (!Save(Replaced(entry.Unbound(SharingStage.Off)), entry.ContentId))
        {
            return false;
        }

        var code = view.Code is { } issued && issued.ContentId == entry.ContentId;
        Publish(notify ? view.With(clearCode: code, notice: new SharingNotice(entry.ContentId, SharingNoticeKind.TurnedOff)) : view.With(clearCode: code));
        return true;
    }

    /// <summary>
    /// Sends one signed action with the key in <paramref name="slot"/>. A key that isn't the
    /// registry's, or can't sign now, sends nothing at all. Before anything but an opt-out, the
    /// server's version is checked once a session. Null, with a notice, when it got no answer it
    /// can use; a <c>410</c> records the takeover (C1) before it is returned as null.
    /// </summary>
    private SharingResponse? Send(PersonaManager manager, SharingCharacter entry, PersonaSlotId slot, PersonaId keyId, RequestProofKind kind, byte[] body)
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
            var response = client.ActionAsync(kind, body, signer, stopping).GetAwaiter().GetResult();
            log($"Sharing: {SharingClient.PathOf(kind)} answered {(int)response.Status}.");
            if (response.Status == HttpStatusCode.Gone)
            {
                if (Save(Replaced(entry.Unbound(SharingStage.TakenOver)), entry.ContentId))
                {
                    Notify(entry.ContentId, SharingNoticeKind.TakenOver);
                }

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

        Publish(view.With(characters: characters));
        return true;
    }

    private void Notify(ulong contentId, SharingNoticeKind kind) => Publish(view.With(notice: new SharingNotice(contentId, kind)));

    private void Publish(CharacterSharingView next)
    {
        lock (gate)
        {
            view = next;
        }
    }

    /// <summary>
    /// Hands <paramref name="work"/> to the persona session, when nothing of this service runs and
    /// the file was read (or this is the read). The persona selected before the work is selected
    /// again after it. An exception the work didn't expect becomes a notice, and the log gets its
    /// type only.
    /// </summary>
    private bool Run(string name, Action<PersonaManager> work, bool loading = false, bool keepNotice = false)
    {
        lock (gate)
        {
            if (view.Busy || (!loading && (!view.Loaded || view.Unreadable)) || (loading && view.Loaded && !view.Unreadable))
            {
                return false;
            }

            view = keepNotice ? view.With(busy: true) : view.With(busy: true, clearNotice: true);
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
                        Publish(view.With(loaded: true, unreadable: true));
                    }
                    else
                    {
                        Notify(0, SharingNoticeKind.Failed);
                    }
                }
                finally
                {
                    Reselect(manager, selected);
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

    /// <summary>Puts back the selection an operation found: the persona it named, or none.</summary>
    private void Reselect(PersonaManager manager, PersonaSlotId? selected)
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
