using System;
using System.IO;
using System.Linq;
using AetherFrame.Personas.Storage;
using AetherFrame.Protocol.Identity;
using AetherFrame.Services.Network.Personas;
using Xunit;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// The persona window's words and rules (N2-5c): what each state and row says, that a key's claim
/// stays a claim until it is checked (L12), that the texts K4 and K2 require are there, that a
/// failed save says what didn't happen and what did, and that a path under the application data
/// folder never shows the Windows user name. Every operation runs on a real manager.
/// </summary>
public sealed class PersonaWindowModelTests
{
    private readonly InMemoryPersonaKeyStore store = new();
    private readonly InMemoryRegistryStorage registry = new();

    private PersonaManager Manager() => PersonaManager.Load(store, new NoBackupCodec(), registry);

    [Fact]
    public void TheK4StepAndK2sDisclosure_SayWhatTheDecisionsRequire()
    {
        foreach (var way in new[] { "reinstalling Windows", "moving to a new PC", "deleting AetherFrame's data", "resetting your Windows password" })
        {
            Assert.Contains(way, PersonaWindowModel.K4Text, StringComparison.Ordinal);
        }

        Assert.Contains("never update or unpublish", PersonaWindowModel.K4Text, StringComparison.Ordinal);
        Assert.Contains("no account can recover it", PersonaWindowModel.K4Text, StringComparison.Ordinal);
        Assert.Contains("Any program running as you, other Dalamud plugins included, can use it", PersonaWindowModel.K2Disclosure, StringComparison.Ordinal);
        Assert.Contains("wherever your Windows password is known", PersonaWindowModel.K2Disclosure, StringComparison.Ordinal);
        Assert.Contains("your organisation may be able to recover it", PersonaWindowModel.K2Disclosure, StringComparison.Ordinal);

        var main = Manager().Create("Main");
        Assert.Equal("Before Main shares anything", PersonaWindowModel.K4Title(main));
    }

    [Fact]
    public void TheLineAtTheTop_NamesThePersonaInUse_OrSaysNoneIs()
    {
        var manager = Manager();
        Assert.Equal(PersonaWindowModel.NoneInUse, PersonaWindowModel.InUse(null));
        var main = manager.Create("Main");
        Assert.Equal("In use: Main", PersonaWindowModel.InUse(main));
    }

    [Fact]
    public void AnIdentity_IsShortenedForARow_AndAKeyFileIsNamedBySlot()
    {
        var main = Manager().Create("Main");
        var shortened = PersonaWindowModel.ShortIdentity(main.Id);
        Assert.Equal(main.Id.ToString()[..12] + "...", shortened);
        Assert.StartsWith(PersonaId.Prefix, shortened, StringComparison.Ordinal);
        Assert.Equal(main.Slot + ".afkey", PersonaWindowModel.KeyFileName(main.Slot));
    }

    [Fact]
    public void APathUnderApplicationData_HidesTheUserName_AndAnyOtherPathIsShownAsItIs()
    {
        var root = Path.Combine("C:", "Users", "SomeoneSecret", "AppData", "Roaming");
        var keys = Path.Combine(root, "XIVLauncher", "pluginConfigs", "AetherFrame", "Network", "Personas", "keys");
        var shown = PersonaWindowModel.DisplayPath(keys, root);
        Assert.Equal("%APPDATA%" + keys[root.Length..], shown);
        Assert.DoesNotContain("SomeoneSecret", shown, StringComparison.Ordinal);

        // A sibling folder that merely starts with the same text is not under it.
        var sibling = root + "Other" + Path.DirectorySeparatorChar + "x";
        Assert.Equal(sibling, PersonaWindowModel.DisplayPath(sibling, root));
        Assert.Equal(keys, PersonaWindowModel.DisplayPath(keys, ""));
        Assert.Equal("%APPDATA%" + keys[root.Length..], PersonaWindowModel.DisplayPath(keys, root + Path.DirectorySeparatorChar));

        // A launcher installed elsewhere under the profile, in Documents say: from %USERPROFILE% on.
        var profile = Path.Combine("C:", "Users", "SomeoneSecret");
        var documents = Path.Combine(profile, "Documents", "Launcher", "pluginConfigs", "AetherFrame");
        var shownFromProfile = PersonaWindowModel.DisplayPath(documents, root, profile);
        Assert.Equal("%USERPROFILE%" + documents[profile.Length..], shownFromProfile);
        Assert.DoesNotContain("SomeoneSecret", shownFromProfile, StringComparison.Ordinal);
        Assert.Equal("%APPDATA%" + keys[root.Length..], PersonaWindowModel.DisplayPath(keys, root, profile));
    }

    [Fact]
    public void AKeysClaim_StaysAClaimUntilChecked_AndASpareCopyIsNamedOnlyOnceItOpens()
    {
        var manager = Manager();
        var main = manager.Create("Main");

        // An orphan whose header claims Main's identity: a copy of Main's key under a new slot.
        var copy = PersonaSlotId.NewId();
        using (var material = store.OpenKey(main.Slot))
        {
            store.AddKey(copy, material!);
        }

        var orphan = Assert.Single(manager.Audit().Orphans);
        var claimed = PersonaWindowModel.Describe(orphan, manager.Personas, check: null);
        Assert.Equal("Claims to be a copy of Main's key (not checked yet).", claimed.Description);
        Assert.True(claimed.CanCheck);
        Assert.False(claimed.CanRestore);
        Assert.Equal(copy + ".afkey", claimed.KeyFile);

        var proven = PersonaWindowModel.Describe(orphan, manager.Personas, new PersonaKeyCheck(manager.VerifyOrphan(copy)));
        Assert.Equal("A spare copy of Main's key.", proven.Description);
        Assert.False(proven.CanCheck);
        Assert.False(proven.CanRestore);

        var failed = PersonaWindowModel.Describe(orphan, manager.Personas, new PersonaKeyCheck(null));
        Assert.Equal("Doesn't open here: damaged, or made on another Windows account or PC.", failed.Description);
        Assert.False(failed.CanRestore);
    }

    [Fact]
    public void AKeyOfNoPersonaHeldHere_OffersCheckAndRestore()
    {
        var manager = Manager();
        var slot = PersonaSlotId.NewId();
        using (var material = PersonaKeyMaterial.Generate())
        {
            store.AddKey(slot, material);
        }

        var orphan = Assert.Single(manager.Audit().Orphans);
        var row = PersonaWindowModel.Describe(orphan, manager.Personas, check: null);
        Assert.Equal("Claims to be " + PersonaWindowModel.ShortIdentity(orphan.ClaimedId!.Value) + " (not checked yet).", row.Description);
        Assert.True(row.CanCheck);
        Assert.True(row.CanRestore);

        var opened = PersonaWindowModel.Describe(orphan, manager.Personas, new PersonaKeyCheck(manager.VerifyOrphan(slot)));
        Assert.Equal("Opens here as " + PersonaWindowModel.ShortIdentity(orphan.ClaimedId.Value) + ".", opened.Description);
        Assert.True(opened.CanRestore);
    }

    [Fact]
    public void EveryUnusableReason_HasPlainWords_AndAHeaderThatCantBeReadNamesItsOwnCauses()
    {
        foreach (var reason in Enum.GetValues<PersonaUnusableReason>())
        {
            Assert.False(string.IsNullOrWhiteSpace(PersonaWindowModel.Describe(reason)));
        }

        // A key made on another Windows account has a readable header, so it is never among the
        // causes of one that can't be read; it shows only when the key is opened (Check).
        var unreadable = PersonaWindowModel.Describe(PersonaUnusableReason.KeyUnreadable);
        Assert.Contains(PersonaWindowModel.UnreadableHeaderCauses, unreadable, StringComparison.Ordinal);
        Assert.DoesNotContain("another Windows account", unreadable, StringComparison.Ordinal);
        Assert.Contains("in use by another program", unreadable, StringComparison.Ordinal);

        // A header's claim is unverified (L12): the words say what it names, never whose key it is.
        Assert.Equal("Its key file is a copy of another key file.", PersonaWindowModel.Describe(PersonaUnusableReason.KeyNamesAnotherSlot));
        Assert.Equal("Its key file names a different key than this persona's.", PersonaWindowModel.Describe(PersonaUnusableReason.KeyNamesAnotherKey));
        foreach (var reason in Enum.GetValues<PersonaUnusableReason>())
        {
            Assert.DoesNotContain("another persona", PersonaWindowModel.Describe(reason), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AKeyFileThatCantBeRead_IsDescribedByItsHeadersCauses_AndACopiedOneAsACopy()
    {
        var slot = PersonaSlotId.NewId();
        var unreadable = PersonaWindowModel.Describe(new PersonaOrphanKey(slot, PersonaKeyStatusOfOrphan.Unreadable, null), [], check: null);
        Assert.Equal("Can't be read: " + PersonaWindowModel.UnreadableHeaderCauses + ".", unreadable.Description);
        Assert.False(unreadable.CanCheck);
        Assert.False(unreadable.CanRestore);
        Assert.Equal(slot, unreadable.Slot);

        var copied = PersonaWindowModel.Describe(new PersonaOrphanKey(slot, PersonaKeyStatusOfOrphan.NamesAnotherSlot, null), [], check: null);
        Assert.Equal("A copy of another key file, which can't open under this name.", copied.Description);
        Assert.False(copied.CanCheck);
        Assert.False(copied.CanRestore);
    }

    [Fact]
    public void AFailedSave_SaysTheChangeWasntApplied_AndWhatStill()
    {
        var manager = Manager();
        var main = manager.Create("Main");
        var failed = PersonaOperationOutcome.Failed(PersonaError.RegistryWriteFailed);

        var switched = PersonaWindowModel.Message(PersonaAction.Use, failed, main, "Alt")!;
        Assert.StartsWith("Couldn't save your personas, so this change wasn't applied. If it shows after a restart, the save got through.", switched, StringComparison.Ordinal);
        Assert.EndsWith("Main is still in use.", switched, StringComparison.Ordinal);
        Assert.EndsWith(PersonaWindowModel.NoneInUse, PersonaWindowModel.Message(PersonaAction.StopUsing, failed, null, null)!, StringComparison.Ordinal);
        Assert.EndsWith("The key was kept: you can restore it under Keys without a persona.", PersonaWindowModel.Message(PersonaAction.Create, failed, null, "Alt")!, StringComparison.Ordinal);
        Assert.EndsWith("The key was kept: you can restore it under Keys without a persona.", PersonaWindowModel.Message(PersonaAction.RestoreKey, failed, null, "Found")!, StringComparison.Ordinal);
        Assert.EndsWith("the save got through.", PersonaWindowModel.Message(PersonaAction.Rename, failed, null, "New")!, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryOtherRefusal_HasItsOwnWords()
    {
        var failures = new[] { PersonaError.RegistryFull, PersonaError.InvalidLabel, PersonaError.NotAnOrphan, PersonaError.CustodyFailed, PersonaError.KeyUnavailable, PersonaError.UnknownPersona };
        var messages = failures.Select(error => PersonaWindowModel.Message(PersonaAction.Create, PersonaOperationOutcome.Failed(error), null, "x")).ToList();
        Assert.Equal(failures.Length, messages.Distinct().Count());
        Assert.Contains("256", messages[0], StringComparison.Ordinal);
        Assert.Equal("Something went wrong. AetherFrame's log names what kind.", PersonaWindowModel.Message(PersonaAction.Create, PersonaOperationOutcome.Failed(null), null, "x"));

        // The limit counts UTF-16 code units, so the words say some characters count as more than one.
        Assert.Contains("64 characters", messages[1], StringComparison.Ordinal);
        Assert.Contains("count as two or more", messages[1], StringComparison.Ordinal);

        // A failed custody may leave the key file behind (L12), where the audit that follows lists it.
        Assert.Equal(
            "AetherFrame couldn't store the new key safely, so no persona was added. If its key file was left behind, it shows under Keys without a persona.",
            messages[3]);
    }

    [Fact]
    public void TheActions_DoWhatTheySay_OnTheManager_AndCarryWhatTheWindowNeeds()
    {
        var manager = Manager();
        var created = PersonaWindowModel.Work(PersonaAction.Create, default, "Main")(manager);
        Assert.True(created.Succeeded);
        var main = created.Persona!;
        Assert.NotNull(created.Audit);
        Assert.Equal("Created Main.", PersonaWindowModel.Message(PersonaAction.Create, created, null, "Main"));

        Assert.Equal("Main is in use.", PersonaWindowModel.Message(PersonaAction.Use, PersonaWindowModel.Work(PersonaAction.Use, main.Slot, "Main")(manager), manager.Active, "Main"));
        Assert.Equal(main.Slot, manager.Active!.Slot);

        var renamed = PersonaWindowModel.Work(PersonaAction.Rename, main.Slot, "  Main character ")(manager);
        Assert.Equal("Renamed to Main character.", PersonaWindowModel.Message(PersonaAction.Rename, renamed, manager.Active, "  Main character "));

        Assert.True(PersonaWindowModel.Work(PersonaAction.Acknowledge, main.Slot, null)(manager).Persona!.Acknowledged);
        PersonaWindowModel.Work(PersonaAction.StopUsing, default, null)(manager);
        Assert.Null(manager.Active);

        var orphan = PersonaSlotId.NewId();
        using (var material = PersonaKeyMaterial.Generate())
        {
            store.AddKey(orphan, material);
        }

        var checkedKey = PersonaWindowModel.Work(PersonaAction.CheckKey, orphan, null)(manager);
        Assert.NotNull(checkedKey.Opened);
        Assert.Equal(orphan, Assert.Single(checkedKey.Audit!.Orphans).Slot);

        // A check audits again, so a key file gone since the last audit leaves the list rather than
        // reading as one that doesn't open.
        var gone = PersonaWindowModel.Work(PersonaAction.CheckKey, PersonaSlotId.NewId(), null)(manager);
        Assert.Null(gone.Opened);
        Assert.Equal(orphan, Assert.Single(gone.Audit!.Orphans).Slot);
        var restored = PersonaWindowModel.Work(PersonaAction.RestoreKey, orphan, "Found")(manager);
        Assert.Equal(orphan, restored.Persona!.Slot);
        Assert.Empty(restored.Audit!.Orphans);
        Assert.Empty(PersonaWindowModel.Work(PersonaAction.Refresh, default, null)(manager).Audit!.Orphans);
    }

    [Fact]
    public void EveryAction_HasAFixedLogName_AndOnlyCreateAndRestoreAuditAfterAFailure()
    {
        var names = Enum.GetValues<PersonaAction>().Select(PersonaWindowModel.LogName).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(names, name => Assert.Matches("^[a-z ]+$", name));
        Assert.Equal(
            new[] { PersonaAction.Create, PersonaAction.RestoreKey },
            Enum.GetValues<PersonaAction>().Where(PersonaWindowModel.AuditsAfterFailure));
    }
}
