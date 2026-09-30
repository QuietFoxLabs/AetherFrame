using System;
using System.IO;
using System.Linq;
using AetherFrame.Personas.Storage;
using AetherFrame.Services.Network.Personas;
using Xunit;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// The plugin's directory-of-files storage against the storage contract: atomic, never replaces,
/// holds exactly what it was given or nothing, names files by slot alone, and trusts no temporary
/// file. Every file here is written to a temporary directory that the test deletes; the bytes are
/// arbitrary, never a key.
/// </summary>
public class PersonaKeyFileStorageTests : IDisposable
{
    private readonly TemporaryDirectory directory = new();

    public void Dispose() => directory.Dispose();

    private PersonaKeyFileStorage NewStorage(string? sub = null) => new(sub is null ? directory.Path : Path.Combine(directory.Path, sub));

    private static byte[] Bytes(int length, byte fill) => Enumerable.Repeat(fill, length).ToArray();

    [Fact]
    public void Read_IsNullForNothingHeld_EvenBeforeTheDirectoryExists()
    {
        var storage = NewStorage("later");
        Assert.Null(storage.Read(PersonaSlotId.NewId()));
        Assert.False(Directory.Exists(storage.Directory));
    }

    [Fact]
    public void WriteNew_HoldsExactlyTheBytes_UnderTheSlotsNameAlone()
    {
        var storage = NewStorage("keys");
        var slot = PersonaSlotId.NewId();
        var bytes = Bytes(300, 0x5A);
        storage.WriteNew(slot, bytes);

        Assert.Equal(bytes, storage.Read(slot));
        var files = Directory.GetFiles(storage.Directory).Select(f => Path.GetFileName(f)!).ToArray();
        Assert.Equal(new[] { slot + PersonaKeyFileStorage.Extension }, files);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(storage.Directory, slot + PersonaKeyFileStorage.Extension)));
    }

    [Fact]
    public void WriteNew_RefusesAHeldSlot_AndLeavesTheFileAsItWas()
    {
        var storage = NewStorage();
        var slot = PersonaSlotId.NewId();
        storage.WriteNew(slot, Bytes(10, 1));

        Assert.Throws<IOException>(() => storage.WriteNew(slot, Bytes(10, 2)));
        Assert.Equal(Bytes(10, 1), storage.Read(slot));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void WriteNew_LeavesNoTemporaryFile_AndReplacesAStaleOne()
    {
        var storage = NewStorage();
        var slot = PersonaSlotId.NewId();
        var stale = Path.Combine(directory.Path, slot + PersonaKeyFileStorage.Extension + ".tmp");
        File.WriteAllBytes(stale, Bytes(5, 0xFF));

        storage.WriteNew(slot, Bytes(20, 3));
        Assert.False(File.Exists(stale));
        Assert.Equal(Bytes(20, 3), storage.Read(slot));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void Read_NeverTrustsATemporaryFile_EvenWhenItIsTheOnlyOne()
    {
        // What an interrupted write leaves behind: complete-looking bytes under the temporary name
        // and nothing under the final one. Nothing is held.
        var storage = NewStorage();
        var slot = PersonaSlotId.NewId();
        File.WriteAllBytes(Path.Combine(directory.Path, slot + PersonaKeyFileStorage.Extension + ".tmp"), Bytes(20, 4));

        Assert.Null(storage.Read(slot));
    }

    [Fact]
    public void WriteNew_RefusesAFileThatIsAlreadyThere_WhateverItHolds()
    {
        // A file under the final name that this storage did not write (a foreign or damaged file)
        // is refused like a held slot and left exactly as it was. The move's own no-overwrite
        // refusal, for a file that appears between the check and the move, is the second barrier
        // and is not reachable from a test.
        var storage = NewStorage();
        var slot = PersonaSlotId.NewId();
        var final = Path.Combine(directory.Path, slot + PersonaKeyFileStorage.Extension);
        var foreign = Bytes(7, 4);
        File.WriteAllBytes(final, foreign);

        Assert.Throws<IOException>(() => storage.WriteNew(slot, Bytes(4, 9)));
        Assert.Equal(foreign, File.ReadAllBytes(final));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void WriteNew_WhenTheDirectoryCannotBeMade_HoldsNothing()
    {
        // A file where the directory should be.
        var blocker = Path.Combine(directory.Path, "blocked");
        File.WriteAllBytes(blocker, Bytes(1, 0));
        var storage = new PersonaKeyFileStorage(blocker);

        Assert.ThrowsAny<IOException>(() => storage.WriteNew(PersonaSlotId.NewId(), Bytes(3, 1)));
        Assert.Equal(Bytes(1, 0), File.ReadAllBytes(blocker));
    }

    [Fact]
    public void Read_RefusesAFileLargerThanAnyEnvelope()
    {
        var storage = NewStorage();
        var slot = PersonaSlotId.NewId();
        File.WriteAllBytes(Path.Combine(directory.Path, slot + PersonaKeyFileStorage.Extension), new byte[64 * 1024 + 1]);
        Assert.Throws<IOException>(() => storage.Read(slot));
    }

    [Fact]
    public void TheEmptySlot_IsRefusedEverywhere()
    {
        var storage = NewStorage();
        Assert.Throws<ArgumentException>(() => storage.Read(default));
        Assert.Throws<ArgumentException>(() => storage.WriteNew(default, Bytes(1, 1)));
        Assert.Throws<ArgumentException>(() => new PersonaKeyFileStorage(" "));
    }

    [Fact]
    public void List_IsEmptyBeforeTheDirectoryExists_AndMakesNothing()
    {
        var storage = NewStorage("later");
        var listing = storage.List();
        Assert.Empty(listing.Slots);
        Assert.Equal(0, listing.Skipped);
        Assert.False(Directory.Exists(storage.Directory));
    }

    [Fact]
    public void List_NamesEverySlotHeld_AndCountsEverythingElseAsSkipped_WithoutReadingIt()
    {
        var storage = NewStorage();
        var first = PersonaSlotId.NewId();
        var second = PersonaSlotId.NewId();
        storage.WriteNew(first, Bytes(10, 1));
        storage.WriteNew(second, Bytes(10, 2));

        // Not a slot's key file: what an interrupted write leaves, a stray file, a key file whose
        // name is not a slot's text form, and a folder named like a key file.
        var temporary = Path.Combine(directory.Path, PersonaSlotId.NewId() + PersonaKeyFileStorage.Extension + ".tmp");
        var stray = Path.Combine(directory.Path, "notes.txt");
        var misnamed = Path.Combine(directory.Path, "slot_nothex" + PersonaKeyFileStorage.Extension);
        File.WriteAllBytes(temporary, Bytes(3, 3));
        File.WriteAllBytes(stray, Bytes(3, 4));
        File.WriteAllBytes(misnamed, Bytes(3, 5));
        Directory.CreateDirectory(Path.Combine(directory.Path, PersonaSlotId.NewId() + PersonaKeyFileStorage.Extension));

        // Every file is held open with no sharing while the directory is listed, so listing that
        // opened any of them would throw: a listing reads names, never contents.
        var held = new[] { temporary, stray, misnamed, Path.Combine(directory.Path, first + PersonaKeyFileStorage.Extension) }
            .Select(file => new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            .ToList();
        try
        {
            var listing = storage.List();
            Assert.Equal(new[] { first, second }.OrderBy(s => s.ToString()), listing.Slots.OrderBy(s => s.ToString()));
            Assert.Equal(4, listing.Skipped);
        }
        finally
        {
            held.ForEach(stream => stream.Dispose());
        }
    }

    [Fact]
    public void List_NamesAKeyFileAsReadWouldFindIt()
    {
        // On Windows a name differing only in case is the same file, which Read opens; List names
        // it as its slot too, so the audit never counts a key it can open as a stray entry. Other
        // systems tell the names apart, and Read would not find it: there it is skipped.
        var storage = NewStorage();
        var slot = PersonaSlotId.NewId();
        File.WriteAllBytes(Path.Combine(directory.Path, slot.ToString().ToUpperInvariant() + PersonaKeyFileStorage.Extension), Bytes(3, 6));

        var listing = storage.List();
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(slot, Assert.Single(listing.Slots));
            Assert.Equal(0, listing.Skipped);
            Assert.Equal(Bytes(3, 6), storage.Read(slot));
        }
        else
        {
            Assert.Empty(listing.Slots);
            Assert.Equal(1, listing.Skipped);
            Assert.Null(storage.Read(slot));
        }
    }

    [Fact]
    public void List_ThatCannotReadTheDirectory_Throws_RatherThanListNothing()
    {
        // Only a directory that doesn't exist lists as empty; the audit reports anything else as a
        // failed listing. Each system is given a failure it reports as one: Windows refuses to list
        // a file that stands where the directory should be (Linux reports that as "not found"), and
        // Linux refuses a directory its owner may not read.
        if (OperatingSystem.IsWindows())
        {
            var blocker = Path.Combine(directory.Path, "blocked");
            File.WriteAllBytes(blocker, Bytes(1, 0));
            Assert.ThrowsAny<IOException>(() => new PersonaKeyFileStorage(blocker).List());
            return;
        }

        var storage = NewStorage("keys");
        storage.WriteNew(PersonaSlotId.NewId(), Bytes(3, 1));
        File.SetUnixFileMode(storage.Directory, UnixFileMode.None);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => storage.List());
        }
        finally
        {
            File.SetUnixFileMode(storage.Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void TheStoreOverFilesRoundTripsAKey()
    {
        var storage = NewStorage("keys");
        var protector = new FakeProtector();
        var store = new ProtectedPersonaKeyStore(storage, protector);
        var slot = PersonaSlotId.NewId();
        using var material = store.GenerateKey();
        store.AddKey(slot, material);

        using var opened = new ProtectedPersonaKeyStore(new PersonaKeyFileStorage(storage.Directory), new FakeProtector()).OpenKey(slot);
        Assert.NotNull(opened);
        Assert.Equal(material.PublicKey, opened!.PublicKey);
        var names = Directory.GetFiles(storage.Directory).Select(f => Path.GetFileName(f)!).ToArray();
        Assert.Equal(new[] { slot + PersonaKeyFileStorage.Extension }, names);
        Assert.DoesNotContain("psn_", string.Join(",", names), StringComparison.Ordinal);
    }
}
