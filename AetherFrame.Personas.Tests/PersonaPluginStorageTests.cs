using System;
using System.IO;
using System.Linq;
using AetherFrame.Services.Network.Personas;
using Xunit;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// The plugin's persona files beside the key files (decision P3): the written-through move, the
/// registry file, the single-writer lock, and this build's backup codec, which has no format. Every
/// file is written to a temporary directory the test deletes. The Windows CI leg runs the native
/// move; the Linux leg runs its fallback.
/// </summary>
public sealed class PersonaPluginStorageTests : IDisposable
{
    private readonly TemporaryDirectory directory = new();

    public void Dispose() => directory.Dispose();

    private string In(string name) => Path.Combine(directory.Path, name);

    [Fact]
    public void Replace_PutsTheSourceInPlaceOfTheDestination_WhetherOrNotItExists()
    {
        File.WriteAllBytes(In("a.tmp"), [1, 2, 3]);
        WrittenThroughMove.Replace(In("a.tmp"), In("a"));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(In("a")));
        Assert.False(File.Exists(In("a.tmp")));

        File.WriteAllBytes(In("a.tmp"), [4]);
        WrittenThroughMove.Replace(In("a.tmp"), In("a"));
        Assert.Equal(new byte[] { 4 }, File.ReadAllBytes(In("a")));
        Assert.False(File.Exists(In("a.tmp")));
    }

    [Fact]
    public void MoveNew_RefusesAnExistingDestination_AndLeavesBothFilesAsTheyWere()
    {
        File.WriteAllBytes(In("b.tmp"), [1]);
        File.WriteAllBytes(In("b"), [2]);
        Assert.ThrowsAny<IOException>(() => WrittenThroughMove.MoveNew(In("b.tmp"), In("b")));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(In("b.tmp")));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(In("b")));

        File.Delete(In("b"));
        WrittenThroughMove.MoveNew(In("b.tmp"), In("b"));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(In("b")));
        Assert.False(File.Exists(In("b.tmp")));
    }

    [Fact]
    public void AMoveThatFails_Throws_NamingTheErrorAndNeverThePath()
    {
        var failure = Assert.ThrowsAny<IOException>(() => WrittenThroughMove.Replace(In("missing"), In("c")));
        Assert.False(File.Exists(In("c")));
        if (OperatingSystem.IsWindows())
        {
            // ERROR_FILE_NOT_FOUND, which is not retried.
            Assert.Equal("The file could not be moved (Windows error 2).", failure.Message);
            Assert.Equal(unchecked((int)0x80070002), failure.HResult);
        }
    }

    [Fact]
    public void OnWindows_APathLongerThanTheOldLimit_StillMoves()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var deep = directory.Path;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        Directory.CreateDirectory(deep);
        File.WriteAllBytes(Path.Combine(deep, "e.tmp"), [7]);
        WrittenThroughMove.Replace(Path.Combine(deep, "e.tmp"), Path.Combine(deep, "e"));
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(Path.Combine(deep, "e")));
    }

    [Fact]
    public void OnWindows_ThePathTakesTheExtendedLengthPrefix_OnceOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Built from the separator, as the class builds its prefixes.
        var s = Path.DirectorySeparatorChar.ToString();
        var prefix = s + s + "?" + s;
        Assert.Equal(prefix + "C:" + s + "x" + s + "y", WrittenThroughMove.Extended("C:" + s + "x" + s + "y"));
        Assert.Equal(prefix + "UNC" + s + "server" + s + "share" + s + "f", WrittenThroughMove.Extended(s + s + "server" + s + "share" + s + "f"));
        Assert.Equal(prefix + "C:" + s + "x", WrittenThroughMove.Extended(prefix + "C:" + s + "x"));
    }

    [Fact]
    public void TheRegistry_IsAFirstRunUntilSaved_EvenWithoutItsDirectory()
    {
        var storage = new PersonaRegistryFileStorage(In("later"));
        Assert.Null(storage.Read());
        Assert.False(Directory.Exists(storage.Directory));

        storage.Replace([1, 2, 3]);
        Assert.Equal(new byte[] { 1, 2, 3 }, storage.Read());
        storage.Replace([4, 5]);
        Assert.Equal(new byte[] { 4, 5 }, storage.Read());
        Assert.Equal(new[] { PersonaRegistryFileStorage.RegistryName }, Directory.GetFiles(storage.Directory).Select(Path.GetFileName));
    }

    [Fact]
    public void TheRegistry_NeverReadsATemporaryFile_AndReplacesAStaleOne()
    {
        // What an interrupted first save leaves: the temporary file alone. It is not a registry.
        var storage = new PersonaRegistryFileStorage(directory.Path);
        File.WriteAllBytes(In(PersonaRegistryFileStorage.RegistryName + ".tmp"), [9, 9, 9]);
        Assert.Null(storage.Read());

        storage.Replace([1]);
        Assert.Equal(new byte[] { 1 }, storage.Read());
        Assert.False(File.Exists(In(PersonaRegistryFileStorage.RegistryName + ".tmp")));
    }

    [Fact]
    public void TheRegistry_RefusesAFileLargerThanAnyRegistry()
    {
        var storage = new PersonaRegistryFileStorage(directory.Path);
        File.WriteAllBytes(In(PersonaRegistryFileStorage.RegistryName), new byte[PersonaManager.MaxRegistryBytes]);
        Assert.Equal(PersonaManager.MaxRegistryBytes, storage.Read()!.Length);

        File.WriteAllBytes(In(PersonaRegistryFileStorage.RegistryName), new byte[PersonaManager.MaxRegistryBytes + 1]);
        Assert.Throws<IOException>(() => storage.Read());
    }

    [Fact]
    public void TheRegistry_ThatIsADirectory_IsNeverAFirstRun_AndNeverReplaced()
    {
        var storage = new PersonaRegistryFileStorage(directory.Path);
        Directory.CreateDirectory(In(PersonaRegistryFileStorage.RegistryName));

        var read = Record.Exception(() => storage.Read());
        Assert.NotNull(read);
        Assert.IsNotType<FileNotFoundException>(read);
        Assert.ThrowsAny<IOException>(() => storage.Replace([1]));
        Assert.True(Directory.Exists(In(PersonaRegistryFileStorage.RegistryName)));
        Assert.False(File.Exists(In(PersonaRegistryFileStorage.RegistryName + ".tmp")));
    }

    [Fact]
    public void OnWindows_ARegistryThatCannotBeReplaced_KeepsItsBytes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A read-only registry refuses the replace (after the move's short retries): the old bytes stay.
        var storage = new PersonaRegistryFileStorage(directory.Path);
        storage.Replace([1, 2]);
        var registry = In(PersonaRegistryFileStorage.RegistryName);
        File.SetAttributes(registry, FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsAny<IOException>(() => storage.Replace([3]));
            Assert.Equal(new byte[] { 1, 2 }, storage.Read());
            Assert.False(File.Exists(registry + ".tmp"));
        }
        finally
        {
            File.SetAttributes(registry, FileAttributes.Normal);
        }
    }

    [Fact]
    public void TheRegistryFile_KeepsTheManagersRecordsAcrossALoad()
    {
        var store = new InMemoryPersonaKeyStore();
        var codec = new HandleBackupCodec();
        var first = PersonaManager.Load(store, codec, new PersonaRegistryFileStorage(directory.Path));
        var main = first.Create("Main");
        first.Acknowledge(main.Slot);
        first.Select(main.Slot);

        var second = PersonaManager.Load(store, codec, new PersonaRegistryFileStorage(directory.Path));
        var record = Assert.Single(second.Personas);
        Assert.Equal(main.Slot, record.Slot);
        Assert.Equal("Main", record.Label);
        Assert.True(record.Acknowledged);
        Assert.Equal(main.Slot, second.Active!.Slot);
    }

    [Fact]
    public void TheLock_IsHeldUntilDisposed_ThenFree_AndItsFileHoldsNothing()
    {
        Assert.Equal(PersonaLockOutcome.Acquired, PersonaInstanceLock.TryAcquire(directory.Path, out var held));
        var second = PersonaInstanceLock.TryAcquire(directory.Path, out var none);
        Assert.Null(none);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(PersonaLockOutcome.HeldElsewhere, second);
        }
        else
        {
            Assert.NotEqual(PersonaLockOutcome.Acquired, second);
        }

        held!.Dispose();
        held.Dispose();
        Assert.Equal(PersonaLockOutcome.Acquired, PersonaInstanceLock.TryAcquire(directory.Path, out var again));
        again!.Dispose();
        Assert.Equal(0, new FileInfo(In(PersonaInstanceLock.LockName)).Length);
    }

    [Fact]
    public void TheLock_WhereTheDirectoryCannotBe_IsUnusable()
    {
        File.WriteAllBytes(In("blocked"), [1]);
        Assert.Equal(PersonaLockOutcome.Unusable, PersonaInstanceLock.TryAcquire(In("blocked"), out var held));
        Assert.Null(held);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(In("blocked")));
    }

    [Fact]
    public void NoBackupCodec_ReadsNothingAsABackup_AndRefusesToWriteOrOpen()
    {
        var codec = new NoBackupCodec();
        var inspection = codec.Inspect([1, 2, 3]);
        Assert.Equal(PersonaBackupStatus.Malformed, inspection.Status);
        Assert.Equal(0, inspection.FormatVersion);

        using var material = PersonaKeyMaterial.Generate();
        using var secret = PersonaBackupSecret.FromText("correct horse battery staple");
        Assert.Equal(PersonaError.BackupUnsupported, Assert.Throws<PersonaException>(() => codec.Write(material, secret)).Error);
        Assert.Equal(PersonaError.BackupUnsupported, Assert.Throws<PersonaException>(() => codec.Open([1], secret)).Error);

        // Through the manager: no export, and nothing restores.
        var manager = PersonaManager.Load(new InMemoryPersonaKeyStore(), codec, new InMemoryRegistryStorage());
        var main = manager.Create("Main");
        Assert.Equal(PersonaError.BackupUnsupported, Assert.Throws<PersonaException>(() => manager.ExportBackup(main.Slot, secret)).Error);
        Assert.Equal(PersonaRestoreStatus.Malformed, manager.RestoreBackup([1, 2, 3], secret, "Other").Status);
        Assert.Single(manager.Personas);
    }
}
