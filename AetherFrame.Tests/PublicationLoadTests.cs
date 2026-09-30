using System;
using System.IO;
using System.Linq;
using System.Text;
using AetherFrame.Protocol.Identity;
using AetherFrame.Services.Network.Publishing;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// NETWORK2's load of a persona's publications (N2-6c; N2-6's design, sections 2 and 9): the index
/// decides. What it names is checked in full; what fails reads as not stored, never as pending, and
/// is kept; what it doesn't name is deleted, and only once an index was read. An index that can't be
/// read is never taken for an empty one, and nothing is deleted then. Signed with throwaway keys only.
/// </summary>
public sealed class PublicationLoadTests : IDisposable
{
    private readonly PublicationFixture fixture = new();

    public void Dispose() => fixture.Dispose();

    [Fact]
    public void ACommittedRevision_LoadsAsWaiting()
    {
        var outcome = fixture.Commit(PublicationCandidates.Simple());

        var loaded = Load();

        Assert.Equal(PublicationLoadResult.Loaded, loaded.Result);
        var entry = Assert.Single(loaded.Entries);
        Assert.Equal((outcome.Revision, OutboxState.Waiting), (entry.Entry.LatestRevision, entry.Outbox));
        Assert.Equal(0, loaded.Removed);
    }

    [Fact]
    public void AnEntryNamingNoOutboxEntry_HasNothingWaiting()
    {
        var published = new PublicationEntry(Guid.NewGuid(), ProfileId.NewId(), RevisionId.NewId(), PublicationState.Published, 1_789_000_000, OutboxEntryName.None);
        fixture.Save(new PublicationIndex([published]));

        Assert.Equal(OutboxState.Nothing, Assert.Single(Load().Entries).Outbox);
    }

    [Fact]
    public void AnEntryMissingDamagedOrNotTheIndexs_ReadsAsNotStored_AndIsKept()
    {
        fixture.Commit(PublicationCandidates.Simple());
        var entry = fixture.SavedIndex().Entries.Single();
        var path = Path.Combine(fixture.Files.OutboxPath(fixture.Persona.Slot), entry.PendingEntry.FileName);
        var good = File.ReadAllBytes(path);

        // Damaged: one bit.
        var damaged = (byte[])good.Clone();
        damaged[damaged.Length / 2] ^= 1;
        File.WriteAllBytes(path, damaged);
        Assert.Equal(OutboxState.NotStored, Load().Entries.Single().Outbox);
        Assert.Equal(damaged, File.ReadAllBytes(path));

        // Whole, but of another revision than the index names.
        File.WriteAllBytes(path, good);
        fixture.Save(new PublicationIndex([entry with { LatestRevision = RevisionId.NewId() }]));
        Assert.Equal(OutboxState.NotStored, Load().Entries.Single().Outbox);
        Assert.True(File.Exists(path));

        // Signed by another persona than the one loading.
        fixture.Save(new PublicationIndex([entry]));
        Assert.Equal(OutboxState.Waiting, Load().Entries.Single().Outbox);
        var other = fixture.Personas.Acknowledge(fixture.Personas.Create("Other").Slot);
        Assert.Equal(PublicationLoadResult.Loaded, PublicationLoad.Load(fixture.Files, fixture.Persona.Slot, other.PublicKey).Result);
        Assert.Equal(OutboxState.NotStored, PublicationLoad.Load(fixture.Files, fixture.Persona.Slot, other.PublicKey).Entries.Single().Outbox);

        // Missing.
        File.Delete(path);
        Assert.Equal(OutboxState.NotStored, Load().Entries.Single().Outbox);
    }

    [Fact]
    public void WhatTheIndexDoesntName_IsDeleted_AndNothingElse()
    {
        fixture.Commit(PublicationCandidates.Simple());
        var named = fixture.SavedIndex().Entries.Single().PendingEntry;
        var outbox = fixture.Files.OutboxPath(fixture.Persona.Slot);
        var stray = OutboxEntryName.NewName();
        File.WriteAllBytes(Path.Combine(outbox, stray.FileName), [1, 2, 3]);
        var temporary = OutboxEntryName.NewName().FileName + ".tmp";
        File.WriteAllBytes(Path.Combine(outbox, temporary), [4]);
        File.WriteAllBytes(Path.Combine(outbox, "keep.txt"), [5]);

        var loaded = Load();

        Assert.Equal(2, loaded.Removed);
        Assert.Equal(new[] { named }, fixture.OutboxEntries());
        Assert.False(File.Exists(Path.Combine(outbox, temporary)));
        Assert.True(File.Exists(Path.Combine(outbox, "keep.txt")));
    }

    [Fact]
    public void NoIndex_IsAPersonaThatPublishedNothing_AndDeletesNothing()
    {
        var outbox = fixture.Files.OutboxPath(fixture.Persona.Slot);
        Directory.CreateDirectory(outbox);
        var stray = OutboxEntryName.NewName();
        File.WriteAllBytes(Path.Combine(outbox, stray.FileName), [1]);

        var loaded = Load();

        Assert.Equal(PublicationLoadResult.Loaded, loaded.Result);
        Assert.Empty(loaded.Entries);
        Assert.Equal(0, loaded.Removed);
        Assert.Equal(new[] { stray }, fixture.OutboxEntries());
    }

    [Fact]
    public void AnIndexThatCantBeRead_IsNeverTakenForAnEmptyOne_AndNothingIsDeleted()
    {
        fixture.Commit(PublicationCandidates.Simple());
        var outbox = fixture.OutboxEntries();
        var path = fixture.Files.IndexPath(fixture.Persona.Slot);
        var newer = File.ReadAllBytes(path);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(newer.AsSpan(4), 2);

        foreach (var (bytes, expected) in new[]
        {
            (Encoding.ASCII.GetBytes("damaged"), PublicationLoadResult.IndexUnreadable),
            (newer, PublicationLoadResult.IndexNewerVersion),
        })
        {
            File.WriteAllBytes(path, bytes);
            var loaded = Load();

            Assert.Equal(expected, loaded.Result);
            Assert.Null(loaded.Index);
            Assert.Empty(loaded.Entries);
            Assert.NotNull(loaded.Failure);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(outbox, fixture.OutboxEntries());
        }
    }

    [Fact]
    public void AnIndexOfAnotherSlot_IsRefused_AndNoIndexIsEverDeleted()
    {
        fixture.Commit(PublicationCandidates.Simple());
        var other = fixture.Personas.Acknowledge(fixture.Personas.Create("Other").Slot);
        File.Copy(fixture.Files.IndexPath(fixture.Persona.Slot), fixture.Files.IndexPath(other.Slot));

        Assert.Equal(PublicationLoadResult.IndexUnreadable, PublicationLoad.Load(fixture.Files, other.Slot, other.PublicKey).Result);
        Assert.True(File.Exists(fixture.Files.IndexPath(other.Slot)));
        Assert.True(File.Exists(fixture.Files.IndexPath(fixture.Persona.Slot)));
    }

    private LoadedPublications Load() => PublicationLoad.Load(fixture.Files, fixture.Persona.Slot, fixture.Persona.PublicKey);
}
