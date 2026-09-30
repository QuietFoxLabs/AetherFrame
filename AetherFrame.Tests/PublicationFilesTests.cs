using System;
using System.IO;
using System.Linq;
using AetherFrame.Personas;
using AetherFrame.Services.Network.Publishing;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// NETWORK2's publication files (N2-6c; decisions P1 and P4): each persona's index beside the
/// registry, named by its slot, and its outbox, each entry under a random name. Writes are whole or
/// not at all, reads are bounded, and nothing missing is ever confused with something unreadable.
/// </summary>
public sealed class PublicationFilesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aetherframe-publication-files-" + Guid.NewGuid().ToString("N"));
    private readonly PersonaSlotId slot = PersonaSlotId.NewId();
    private readonly PublicationFiles files;

    public PublicationFilesTests()
    {
        files = new PublicationFiles(root);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AnIndex_IsNamedByItsSlot_BesideTheRegistry_AndItsOutboxBelowIt()
    {
        Assert.Equal(Path.Combine(root, slot + ".afpub"), files.IndexPath(slot));
        Assert.Equal(Path.Combine(root, "outbox", slot.ToString()), files.OutboxPath(slot));
        Assert.Throws<ArgumentException>(() => files.IndexPath(default));
    }

    [Fact]
    public void NoIndex_ReadsAsNone_WhetherTheFileOrTheFolderIsMissing()
    {
        Assert.Null(files.ReadIndex(slot));
        Directory.CreateDirectory(root);
        Assert.Null(files.ReadIndex(slot));
    }

    [Fact]
    public void AnIndex_IsReplacedWhole_AndAStaleTemporaryFileIsNeverRead()
    {
        files.ReplaceIndex(slot, [1, 2, 3]);
        Assert.Equal(new byte[] { 1, 2, 3 }, files.ReadIndex(slot));

        File.WriteAllBytes(files.IndexPath(slot) + ".tmp", [9, 9]);
        Assert.Equal(new byte[] { 1, 2, 3 }, files.ReadIndex(slot));

        files.ReplaceIndex(slot, [4, 5]);
        Assert.Equal(new byte[] { 4, 5 }, files.ReadIndex(slot));
        Assert.False(File.Exists(files.IndexPath(slot) + ".tmp"));
    }

    [Fact]
    public void AnIndexLargerThanAnyIndex_IsRefused_NeverReadAsNone()
    {
        Directory.CreateDirectory(root);
        File.WriteAllBytes(files.IndexPath(slot), new byte[PublicationIndexCodec.MaxBytes + 1]);
        Assert.Throws<IOException>(() => files.ReadIndex(slot));

        File.WriteAllBytes(files.IndexPath(slot), new byte[PublicationIndexCodec.MaxBytes]);
        Assert.Equal(PublicationIndexCodec.MaxBytes, files.ReadIndex(slot)!.Length);
    }

    [Fact]
    public void AnEntry_IsWrittenOnceUnderItsName_AndNeverReplaced()
    {
        var name = OutboxEntryName.NewName();
        files.WriteEntry(slot, name, [7, 8, 9]);
        Assert.Equal(new byte[] { 7, 8, 9 }, files.ReadEntry(slot, name));
        Assert.Equal(3, files.EntrySize(slot, name));
        Assert.Equal(Path.Combine(files.OutboxPath(slot), name.FileName), Directory.GetFiles(files.OutboxPath(slot)).Single());

        Assert.ThrowsAny<IOException>(() => files.WriteEntry(slot, name, [1]));
        Assert.Equal(new byte[] { 7, 8, 9 }, files.ReadEntry(slot, name));
        Assert.Single(Directory.GetFiles(files.OutboxPath(slot)));
        Assert.Throws<ArgumentException>(() => files.WriteEntry(slot, OutboxEntryName.None, [1]));
    }

    [Fact]
    public void AMissingEntry_ReadsAsNone_HasNoSize_AndIsGoneAlready()
    {
        var name = OutboxEntryName.NewName();
        Assert.Null(files.ReadEntry(slot, name));
        Assert.Null(files.ReadEntry(slot, OutboxEntryName.None));
        Assert.Equal(0, files.EntrySize(slot, name));
        Assert.True(files.TryDeleteEntry(slot, name));

        files.WriteEntry(slot, name, [1]);
        Assert.True(files.TryDeleteEntry(slot, name));
        Assert.Null(files.ReadEntry(slot, name));
    }

    [Fact]
    public void AnOutboxListing_TellsEntriesFromTemporariesAndOthers()
    {
        Assert.Same(OutboxListing.Empty, files.ListOutbox(slot));

        var entry = OutboxEntryName.NewName();
        files.WriteEntry(slot, entry, [1]);
        var outbox = files.OutboxPath(slot);
        var temporary = OutboxEntryName.NewName().FileName + ".tmp";
        File.WriteAllBytes(Path.Combine(outbox, temporary), [2]);
        File.WriteAllBytes(Path.Combine(outbox, "notes.txt"), [3]);
        File.WriteAllBytes(Path.Combine(outbox, entry.FileName.ToUpperInvariant() + "x"), [4]);

        var listing = files.ListOutbox(slot);
        Assert.Equal(new[] { entry }, listing.Entries);
        Assert.Equal(new[] { temporary }, listing.Temporaries);
        Assert.Equal(2, listing.Others);

        Assert.True(files.TryDeleteTemporary(slot, temporary));
        Assert.False(files.TryDeleteTemporary(slot, "notes.txt"));
        Assert.False(files.TryDeleteTemporary(slot, ".." + Path.DirectorySeparatorChar + "x.afpo.tmp"));
        Assert.True(File.Exists(Path.Combine(outbox, "notes.txt")));
    }
}
