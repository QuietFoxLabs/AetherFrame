using System;
using System.Collections.Generic;
using System.IO;
using AetherFrame.Personas;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Services.Network.Personas;

/// <summary>What the player asked the persona window to do. Each maps to one session operation with a fixed name for the log.</summary>
public enum PersonaAction
{
    /// <summary>Make a persona.</summary>
    Create,

    /// <summary>Give a persona another name.</summary>
    Rename,

    /// <summary>Make a persona the one in use.</summary>
    Use,

    /// <summary>Leave no persona in use.</summary>
    StopUsing,

    /// <summary>Record K4's acknowledgement for a persona.</summary>
    Acknowledge,

    /// <summary>Open a key without a persona, to see whether and as what it opens here.</summary>
    CheckKey,

    /// <summary>Make a key without a persona into a persona.</summary>
    RestoreKey,

    /// <summary>Look at the key files again.</summary>
    Refresh,
}

/// <summary>What checking a key without a persona found: the public key it opened as, or none.</summary>
public sealed class PersonaKeyCheck
{
    /// <summary>A check that opened <paramref name="opened"/>, or found the key doesn't open here (null).</summary>
    public PersonaKeyCheck(PersonaPublicKey? opened)
    {
        Opened = opened;
    }

    /// <summary>The public key the key opened as; null when it doesn't open here.</summary>
    public PersonaPublicKey? Opened { get; }
}

/// <summary>One row of the window's "Keys without a persona" section.</summary>
public sealed class PersonaOrphanRow
{
    internal PersonaOrphanRow(PersonaSlotId slot, string keyFile, string description, bool canCheck, bool canRestore)
    {
        Slot = slot;
        KeyFile = keyFile;
        Description = description;
        CanCheck = canCheck;
        CanRestore = canRestore;
    }

    /// <summary>The slot the key is held under.</summary>
    public PersonaSlotId Slot { get; }

    /// <summary>The key's file name.</summary>
    public string KeyFile { get; }

    /// <summary>What is known about it, in plain words; it may hold a label, so it is drawn unformatted.</summary>
    public string Description { get; }

    /// <summary>Whether offering Check makes sense.</summary>
    public bool CanCheck { get; }

    /// <summary>Whether offering "Restore as persona" makes sense; the restore checks everything again anyway.</summary>
    public bool CanRestore { get; }
}

/// <summary>
/// The persona window's words and rules, without ImGui, so the persona suite can test them (NETWORK2's
/// N2-5c; docs/networking/DecisionRegister.md, D3, D9a, K2, K4 and L12). A persona's label is the
/// player's own text: the window draws every string made here unformatted, and none of it reaches
/// the log. Operations reach the manager only through the session, off the framework thread.
/// </summary>
public static class PersonaWindowModel
{
    /// <summary>What a persona is, at the top of the window.</summary>
    public const string Intro = "A persona is the identity you share Plates under. It isn't tied to your character, and only you see its name here.";

    /// <summary>While the session starts.</summary>
    public const string Starting = "Checking what works on this system...";

    /// <summary>When no persona is in use.</summary>
    public const string NoneInUse = "No persona is in use. Nothing is shared either way.";

    /// <summary>
    /// K4's acknowledgement: the ordinary ways a key is lost, that only the server's operator can then
    /// remove what the persona shared (S3), and what keeping the persona folder does and doesn't do (P4).
    /// </summary>
    public const string K4Text = "If this persona's key is lost, you can never update or unpublish what it shared, and no account can recover it; only the server's operator can then remove it, on your request. "
        + "Keys are lost by reinstalling Windows, moving to a new PC, deleting AetherFrame's data, or an administrator resetting your Windows password. "
        + "AetherFrame's Network\\Personas folder holds your keys and the list of what each persona shared: keeping it keeps that list. "
        + "It isn't a backup, though: on a new PC or a reinstalled Windows, the keys in it normally won't open.";

    /// <summary>What K2 says the plugin tells the player about the key's protection.</summary>
    public const string K2Disclosure = "Windows protects the key for your account. A copy of your Windows profile opens it wherever your Windows password is known, "
        + "and at once if your account has no password. Any program running as you, other Dalamud plugins included, can use it. "
        + "On a work or school PC, your organisation may be able to recover it.";

    /// <summary>Beside the key folder's path.</summary>
    public const string KeysWarning = "A key removed from this folder can never be used again.";

    /// <summary>Beside an unreadable registry's path.</summary>
    public const string RegistryAside = "Moving this file aside brings every key back as one to restore.";

    /// <summary>Both causes of a key that doesn't open, since the protector can't tell them apart (K2).</summary>
    public const string UnreadableCauses = "damaged, or made on another Windows account or PC";

    /// <summary>
    /// The causes of a key file whose header can't be read: it doesn't decode, it is another
    /// protector's, or opening it failed (another program holding it, or no permission). A key made
    /// on another Windows account has a readable header, so it isn't among them; it shows only when
    /// the key is opened.
    /// </summary>
    public const string UnreadableHeaderCauses = "damaged, not a key file this version reads, or can't be opened (in use by another program, or no permission)";

    private const string SaveFailed = "Couldn't save your personas, so this change wasn't applied. If it shows after a restart, the save got through.";

    /// <summary>The line naming the persona in use, or saying none is.</summary>
    public static string InUse(PersonaRecord? active) => active is null ? NoneInUse : "In use: " + active.Label;

    /// <summary>The K4 step's title, naming the persona it is for.</summary>
    public static string K4Title(PersonaRecord persona) => "Before " + persona.Label + " shares anything";

    /// <summary>An identity short enough for a row: its prefix and 8 digits. The full one goes in a tooltip.</summary>
    public static string ShortIdentity(PersonaId id) => id.ToString()[..(PersonaId.Prefix.Length + 8)] + "...";

    /// <summary>The name of the file a persona's key is kept in.</summary>
    public static string KeyFileName(PersonaSlotId slot) => slot + PersonaKeyFileStorage.Extension;

    /// <summary>
    /// A path as the window shows it, so that a screenshot or a stream doesn't show the Windows user
    /// name: under the application data folder, from <c>%APPDATA%</c> on; elsewhere under the user's
    /// profile (a launcher installed in Documents, say), from <c>%USERPROFILE%</c> on; anywhere else,
    /// as it is. Both forms work in Explorer's address bar.
    /// </summary>
    public static string DisplayPath(string path, string applicationData, string userProfile = "")
    {
        ArgumentNullException.ThrowIfNull(path);
        return Under(path, applicationData, "%APPDATA%") ?? Under(path, userProfile, "%USERPROFILE%") ?? path;
    }

    /// <summary>
    /// Why a persona's key can't be used, in plain words. The audit reads headers only, so this says
    /// what a header names, never whose key it is (L12).
    /// </summary>
    public static string Describe(PersonaUnusableReason reason) => reason switch
    {
        PersonaUnusableReason.KeyMissing => "Its key file is missing.",
        PersonaUnusableReason.KeyUnreadable => "Its key file can't be read: " + UnreadableHeaderCauses + ".",
        PersonaUnusableReason.KeyNamesAnotherSlot => "Its key file is a copy of another key file.",
        _ => "Its key file names a different key than this persona's.",
    };

    /// <summary>
    /// A key without a persona, as its row shows it. What its header claims is unverified until
    /// Check opens it (L12): a key claiming the identity of a persona held here "claims to be a copy"
    /// until then, and reads as a spare copy only once it has opened as that identity.
    /// </summary>
    public static PersonaOrphanRow Describe(PersonaOrphanKey orphan, IReadOnlyList<PersonaRecord> personas, PersonaKeyCheck? check)
    {
        ArgumentNullException.ThrowIfNull(orphan);
        ArgumentNullException.ThrowIfNull(personas);
        var file = KeyFileName(orphan.Slot);
        switch (orphan.Status)
        {
            case PersonaKeyStatusOfOrphan.Unreadable:
                return new PersonaOrphanRow(orphan.Slot, file, "Can't be read: " + UnreadableHeaderCauses + ".", canCheck: false, canRestore: false);
            case PersonaKeyStatusOfOrphan.NamesAnotherSlot:
                return new PersonaOrphanRow(orphan.Slot, file, "A copy of another key file, which can't open under this name.", canCheck: false, canRestore: false);
        }

        if (check is not null)
        {
            if (check.Opened is not { } opened)
            {
                return new PersonaOrphanRow(orphan.Slot, file, "Doesn't open here: " + UnreadableCauses + ".", canCheck: true, canRestore: false);
            }

            return Holder(personas, opened.Id) is { } holder
                ? new PersonaOrphanRow(orphan.Slot, file, "A spare copy of " + holder.Label + "'s key.", canCheck: false, canRestore: false)
                : new PersonaOrphanRow(orphan.Slot, file, "Opens here as " + ShortIdentity(opened.Id) + ".", canCheck: false, canRestore: true);
        }

        if (orphan.ClaimedId is { } claimed && Holder(personas, claimed) is { } claimant)
        {
            return new PersonaOrphanRow(orphan.Slot, file, "Claims to be a copy of " + claimant.Label + "'s key (not checked yet).", canCheck: true, canRestore: false);
        }

        var claim = orphan.ClaimedId is { } id ? ShortIdentity(id) : "a persona";
        return new PersonaOrphanRow(orphan.Slot, file, "Claims to be " + claim + " (not checked yet).", canCheck: true, canRestore: true);
    }

    /// <summary>
    /// What the window says once an operation ends: a line naming what happened, or, when it
    /// failed, what it means for the player. <paramref name="active"/> is the persona in use after
    /// it, and <paramref name="label"/> the name the player typed, when there was one.
    /// </summary>
    public static string? Message(PersonaAction action, PersonaOperationOutcome outcome, PersonaRecord? active, string? label)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Succeeded)
        {
            var name = outcome.Persona?.Label ?? label ?? "";
            return action switch
            {
                PersonaAction.Create => "Created " + name + ".",
                PersonaAction.Rename => "Renamed to " + name + ".",
                PersonaAction.Use => name + " is in use.",
                PersonaAction.StopUsing => NoneInUse,
                PersonaAction.Acknowledge => "Noted for " + name + ".",
                PersonaAction.RestoreKey => "Restored as " + name + ".",
                _ => null,
            };
        }

        return outcome.Error switch
        {
            PersonaError.RegistryWriteFailed => action switch
            {
                PersonaAction.Use or PersonaAction.StopUsing => SaveFailed + " " + (active is null ? NoneInUse : active.Label + " is still in use."),
                PersonaAction.Create or PersonaAction.RestoreKey => SaveFailed + " The key was kept: you can restore it under Keys without a persona.",
                _ => SaveFailed,
            },
            PersonaError.RegistryFull => $"You have the most personas AetherFrame keeps ({PersonaManager.MaxPersonas}).",
            PersonaError.InvalidLabel => $"A persona's name can't be empty, hold control characters or run past {PersonaLabel.MaxLength} characters, where some emoji and symbols count as two or more.",
            PersonaError.NotAnOrphan => "That key can't be restored: it doesn't open here, or a persona you have already uses it.",
            PersonaError.CustodyFailed => "AetherFrame couldn't store the new key safely, so no persona was added. If its key file was left behind, it shows under Keys without a persona.",
            PersonaError.KeyUnavailable => "That persona's key can't be opened here.",
            PersonaError.UnknownPersona => "That persona isn't there any more.",
            _ => "Something went wrong. AetherFrame's log names what kind.",
        };
    }

    /// <summary>The operation's fixed name in the log: never a label.</summary>
    public static string LogName(PersonaAction action) => action switch
    {
        PersonaAction.Create => "create",
        PersonaAction.Rename => "rename",
        PersonaAction.Use => "use",
        PersonaAction.StopUsing => "stop using",
        PersonaAction.Acknowledge => "acknowledge",
        PersonaAction.CheckKey => "check key",
        PersonaAction.RestoreKey => "restore key",
        _ => "refresh",
    };

    /// <summary>Whether a failure should audit the key files again: when it may have left a key without a persona.</summary>
    public static bool AuditsAfterFailure(PersonaAction action) => action is PersonaAction.Create or PersonaAction.RestoreKey;

    /// <summary>The work an action does on the manager, off the framework thread, through the session.</summary>
    public static Func<PersonaManager, PersonaOperationOutcome> Work(PersonaAction action, PersonaSlotId slot, string? label) => action switch
    {
        PersonaAction.Create => manager =>
        {
            var created = manager.Create(label ?? "");
            return PersonaOperationOutcome.Done(manager.Audit(), created);
        },
        PersonaAction.Rename => manager => PersonaOperationOutcome.Done(persona: manager.Rename(slot, label ?? "")),
        PersonaAction.Use => manager => PersonaOperationOutcome.Done(persona: manager.Select(slot)),
        PersonaAction.StopUsing => manager =>
        {
            manager.Deselect();
            return PersonaOperationOutcome.Done();
        },
        PersonaAction.Acknowledge => manager => PersonaOperationOutcome.Done(persona: manager.Acknowledge(slot)),
        PersonaAction.CheckKey => manager =>
        {
            // The audit after the check drops a file removed since the last one, which would
            // otherwise read as a key that doesn't open.
            var opened = manager.VerifyOrphan(slot);
            return PersonaOperationOutcome.Done(manager.Audit(), opened: opened);
        },
        PersonaAction.RestoreKey => manager =>
        {
            var restored = manager.RestoreOrphan(slot, label ?? "");
            return PersonaOperationOutcome.Done(manager.Audit(), restored);
        },
        _ => manager => PersonaOperationOutcome.Done(manager.Audit()),
    };

    private static string? Under(string path, string? folder, string name)
    {
        var root = (folder ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length > 0
            && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && (path.Length == root.Length || path[root.Length] == Path.DirectorySeparatorChar || path[root.Length] == Path.AltDirectorySeparatorChar))
        {
            return name + path[root.Length..];
        }

        return null;
    }

    private static PersonaRecord? Holder(IReadOnlyList<PersonaRecord> personas, PersonaId id)
    {
        for (var index = 0; index < personas.Count; index++)
        {
            if (personas[index].Id == id)
            {
                return personas[index];
            }
        }

        return null;
    }
}
