using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using AetherFrame.Personas;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Services.Network.Publishing;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// NETWORK2's commit (N2-6c; N2-6's design, section 2): the persona shown signs the candidate shown,
/// once, and the signed revision is kept in its outbox with its images, with the publication index as
/// the commit point. Every refusal leaves the files as they were, and no failure deletes an entry the
/// index names. Signed with throwaway keys only.
/// </summary>
public sealed class PublicationCommitTests : IDisposable
{
    private readonly PublicationFixture fixture = new();

    public void Dispose() => fixture.Dispose();

    [Fact]
    public void ACommit_StoresTheRevisionSignedAsShown_AndTheSavedIndexNamesIt()
    {
        var candidate = PublicationCandidates.Simple();

        var outcome = fixture.Commit(candidate);

        Assert.Equal(PublishResult.Stored, outcome.Result);
        var saved = fixture.SavedIndex();
        Assert.Equal(outcome.Index!.Entries, saved.Entries);
        var entry = Assert.Single(saved.Entries);
        Assert.Equal((candidate.PlateId, outcome.Profile, outcome.Revision, PublicationState.Pending, 0L), (entry.PlateId, entry.ProfileId, entry.LatestRevision, entry.State, entry.LastPublishedAt));
        Assert.Equal(new[] { entry.PendingEntry }, fixture.OutboxEntries());

        var stored = OutboxEntryCodec.Decode(fixture.Files.ReadEntry(fixture.Persona.Slot, entry.PendingEntry)!, fixture.Persona.PublicKey);
        Assert.True(SnapshotComparer.Same(candidate.ToSnapshot(outcome.Profile, outcome.Revision, fixture.Now.ToUnixTimeSeconds()), stored.Snapshot));
        Assert.Equal(candidate.ImageBytes.Select(b => b.ToArray()), stored.Images);
    }

    [Fact]
    public void SharingAgain_KeepsTheProfile_SignsANewRevision_AndOnlyThenDropsTheOldOne()
    {
        var plate = Guid.NewGuid();
        var first = fixture.Commit(PublicationCandidates.Simple(plate, "First"));
        var firstEntry = fixture.SavedIndex().Entries.Single().PendingEntry;

        fixture.Now = fixture.Now.AddMinutes(5);
        var second = fixture.Commit(PublicationCandidates.Simple(plate, "Second"));

        Assert.Equal(PublishResult.Stored, second.Result);
        Assert.Equal(first.Profile, second.Profile);
        Assert.NotEqual(first.Revision, second.Revision);
        var entry = fixture.SavedIndex().Entries.Single();
        Assert.Equal(second.Revision, entry.LatestRevision);
        Assert.NotEqual(firstEntry, entry.PendingEntry);
        Assert.Equal(new[] { entry.PendingEntry }, fixture.OutboxEntries());
        var stored = OutboxEntryCodec.Decode(fixture.Files.ReadEntry(fixture.Persona.Slot, entry.PendingEntry)!, fixture.Persona.PublicKey);
        Assert.Contains(stored.Snapshot.Items, item => item is LayoutText { Text: "Second" });
        Assert.Equal(fixture.Now.ToUnixTimeSeconds(), stored.Snapshot.CreatedAtUnixSeconds);
    }

    [Fact]
    public void AnUpdateToAPublishedProfile_StaysPublished_WithItsTime()
    {
        var plate = Guid.NewGuid();
        var published = new PublicationEntry(plate, ProfileId.NewId(), RevisionId.NewId(), PublicationState.Published, 1_789_000_000, OutboxEntryName.None);
        fixture.Save(new PublicationIndex([published]));

        var outcome = fixture.Commit(PublicationCandidates.Simple(plate));

        Assert.Equal(PublishResult.Stored, outcome.Result);
        var entry = fixture.SavedIndex().Entries.Single();
        Assert.Equal((published.ProfileId, PublicationState.Published, 1_789_000_000L), (entry.ProfileId, entry.State, entry.LastPublishedAt));
        Assert.NotEqual(published.LatestRevision, entry.LatestRevision);
        Assert.False(entry.PendingEntry.IsNone);
    }

    [Fact]
    public void ARetractingProfile_IsNeverUsedAgain()
    {
        var plate = Guid.NewGuid();
        var retracting = new PublicationEntry(plate, ProfileId.NewId(), RevisionId.NewId(), PublicationState.Retracting, 1_789_000_000, OutboxEntryName.None);
        fixture.Save(new PublicationIndex([retracting]));

        var outcome = fixture.Commit(PublicationCandidates.Simple(plate));

        Assert.Equal(PublishResult.Stored, outcome.Result);
        Assert.NotEqual(retracting.ProfileId, outcome.Profile);
        var entries = fixture.SavedIndex().Entries;
        Assert.Equal(retracting, entries[0]);
        Assert.Equal((plate, outcome.Profile, PublicationState.Pending), (entries[1].PlateId, entries[1].ProfileId, entries[1].State));
    }

    [Fact]
    public void AFullIndex_RefusesANewProfile_BeforeSigning_ButUpdatesALiveOne()
    {
        var entries = Enumerable.Range(0, PublicationIndex.MaxEntries)
            .Select(_ => new PublicationEntry(Guid.NewGuid(), ProfileId.NewId(), RevisionId.NewId(), PublicationState.Published, 1_789_000_000, OutboxEntryName.None))
            .ToList();
        fixture.Save(new PublicationIndex(entries));
        var before = fixture.AllFiles();

        Assert.Equal(PublishResult.IndexFull, fixture.Commit(PublicationCandidates.Simple()).Result);
        Assert.Equal(before, fixture.AllFiles());

        Assert.Equal(PublishResult.Stored, fixture.Commit(PublicationCandidates.Simple(entries[17].PlateId)).Result);
        Assert.Equal(entries[17].ProfileId, fixture.SavedIndex().Entries[17].ProfileId);
    }

    [Fact]
    public void OnlyThePersonaShown_Signs_AndOnlyWhileItIsActive()
    {
        var candidate = PublicationCandidates.Simple();
        var other = fixture.Personas.Acknowledge(fixture.Personas.Create("Other").Slot);

        // Another persona selected: the one shown doesn't sign, and the one selected isn't asked.
        fixture.Personas.Select(other.Slot);
        Assert.Equal(PublishResult.ActivePersonaChanged, fixture.Commit(candidate).Result);

        // None selected.
        fixture.Personas.Deselect();
        Assert.Equal(PublishResult.ActivePersonaChanged, fixture.Commit(candidate).Result);

        // A key that isn't the one shown, under the slot shown.
        fixture.Personas.Select(fixture.Persona.Slot);
        var forged = PublicationCommit.Commit(fixture.Personas, fixture.Files, new PublishConsent(candidate, fixture.Persona.Slot, other.PublicKey), () => fixture.Now);
        Assert.Equal(PublishResult.ActivePersonaChanged, forged.Result);

        Assert.Empty(fixture.AllFiles());
        Assert.Equal(PublishResult.Stored, fixture.Commit(candidate).Result);
    }

    [Fact]
    public void APersonaWithoutTheAcknowledgement_NeverPublishes()
    {
        using var unacknowledged = new PublicationFixture(acknowledged: false);

        Assert.Equal(PublishResult.NotAcknowledged, unacknowledged.Commit(PublicationCandidates.Simple()).Result);
        Assert.Empty(unacknowledged.AllFiles());
    }

    [Fact]
    public void AKeyThatWontOpen_SignsNothing()
    {
        fixture.Blobs.Forget(fixture.Persona.Slot);

        Assert.Equal(PublishResult.KeyUnavailable, fixture.Commit(PublicationCandidates.Simple()).Result);
        Assert.Empty(fixture.AllFiles());
    }

    [Fact]
    public void AnIndexThatCantBeRead_RefusesTheCommit_AndIsLeftExactlyAsItWas()
    {
        Directory.CreateDirectory(fixture.Root);
        var path = fixture.Files.IndexPath(fixture.Persona.Slot);
        foreach (var (bytes, expected) in new[]
        {
            (Encoding.ASCII.GetBytes("not an index"), PublishResult.IndexUnreadable),
            (Newer(), PublishResult.IndexNewerVersion),
            (new byte[PublicationIndexCodec.MaxBytes + 1], PublishResult.IndexUnreadable),
        })
        {
            File.WriteAllBytes(path, bytes);

            Assert.Equal(expected, fixture.Commit(PublicationCandidates.Simple()).Result);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Empty(fixture.OutboxEntries());
        }
    }

    [Fact]
    public void TheOutboxBound_CountsWhatTheIndexWillName_AndNotTheRevisionSuperseded()
    {
        var plate = Guid.NewGuid();
        var own = OutboxEntryName.NewName();
        var others = OutboxEntryName.NewName();
        fixture.Save(new PublicationIndex(
        [
            new PublicationEntry(plate, ProfileId.NewId(), RevisionId.NewId(), PublicationState.Pending, 0, own),
            new PublicationEntry(Guid.NewGuid(), ProfileId.NewId(), RevisionId.NewId(), PublicationState.Pending, 0, others),
        ]));

        // Files of the sizes the index's entries would have: only their size is read here.
        Directory.CreateDirectory(fixture.Files.OutboxPath(fixture.Persona.Slot));
        SetSize(own, PublicationCommit.MaxOutboxBytes);
        SetSize(others, PublicationCommit.MaxOutboxBytes - 100);

        Assert.Equal(PublishResult.OutboxFull, fixture.Commit(PublicationCandidates.Simple(plate)).Result);
        Assert.True(new HashSet<OutboxEntryName> { own, others }.SetEquals(fixture.OutboxEntries()));

        SetSize(others, 1024);
        var outcome = fixture.Commit(PublicationCandidates.Simple(plate));

        Assert.Equal(PublishResult.Stored, outcome.Result);
        Assert.DoesNotContain(own, fixture.OutboxEntries());
        Assert.Contains(others, fixture.OutboxEntries());
    }

    [Fact]
    public void AnEntryThatCantBeWritten_ChangesNothing()
    {
        var plate = Guid.NewGuid();
        Assert.Equal(PublishResult.Stored, fixture.Commit(PublicationCandidates.Simple(plate)).Result);
        var index = File.ReadAllBytes(fixture.Files.IndexPath(fixture.Persona.Slot));
        var entries = fixture.OutboxEntries();

        // The outbox folder can't take a new file: a file stands where its folder would be.
        var outbox = fixture.Files.OutboxPath(fixture.Persona.Slot);
        Directory.Move(outbox, outbox + ".aside");
        File.WriteAllBytes(outbox, [1]);

        var outcome = fixture.Commit(PublicationCandidates.Simple(plate));

        Assert.Equal(PublishResult.NotSaved, outcome.Result);
        Assert.False(outcome.Indeterminate);
        Assert.NotNull(outcome.Failure);
        Assert.Equal(index, File.ReadAllBytes(fixture.Files.IndexPath(fixture.Persona.Slot)));

        File.Delete(outbox);
        Directory.Move(outbox + ".aside", outbox);
        Assert.Equal(entries, fixture.OutboxEntries());
    }

    [Fact]
    public void AnIndexThatCantBeSaved_NeverLosesTheEntryItNames()
    {
        var plate = Guid.NewGuid();
        var first = fixture.Commit(PublicationCandidates.Simple(plate));
        var named = fixture.SavedIndex().Entries.Single().PendingEntry;

        // The index's temporary file can't be made: a folder stands in its place.
        Directory.CreateDirectory(fixture.Files.IndexPath(fixture.Persona.Slot) + ".tmp");

        var outcome = fixture.Commit(PublicationCandidates.Simple(plate));

        Assert.Equal(PublishResult.NotSaved, outcome.Result);
        Assert.True(outcome.Indeterminate);
        Assert.Equal(first.Revision, fixture.SavedIndex().Entries.Single().LatestRevision);
        Assert.Contains(named, fixture.OutboxEntries());
        Assert.Equal(2, fixture.OutboxEntries().Count);

        // The next load keeps the entry the index names, and deletes the one it doesn't.
        Directory.Delete(fixture.Files.IndexPath(fixture.Persona.Slot) + ".tmp");
        var loaded = PublicationLoad.Load(fixture.Files, fixture.Persona.Slot, fixture.Persona.PublicKey);
        Assert.Equal(OutboxState.Waiting, loaded.Entries.Single().Outbox);
        Assert.Equal(1, loaded.Removed);
        Assert.Equal(new[] { named }, fixture.OutboxEntries());
    }

    [Fact]
    public void NothingButTheLocalPlateId_ReachesTheOutboxOrTheIndex()
    {
        var plate = ComponentDocuments.WithAnchors();
        var hidden = new TextProfileElement { Text = "CanaryHiddenText", Visible = false, ZIndex = 10 };
        var outside = new TextProfileElement { Text = "CanaryOutsideText", Position = new Vector2(-100_000f, -100_000f), ZIndex = 11 };
        var transparent = new ImageProfileElement { AssetId = Guid.NewGuid(), Opacity = 0f, ZIndex = 12 };
        var drawn = new ImageProfileElement { AssetId = Guid.NewGuid(), Position = new Vector2(30f, 30f), Size = new Vector2(64f, 32f), ZIndex = 13 };
        plate.Elements.AddRange([hidden, outside, transparent, drawn]);
        var stale = Guid.NewGuid();
        plate.Background = new ProfileBackground { Mode = ProfileBackgroundMode.SolidColor, ImageAssetId = stale, ExtensionData = Canary("background") };
        plate.OwnerContentId = 0x1122334455667788UL;
        plate.Revision = 0x5A6B7C8D;
        plate.CreatedAtUtc = new DateTime(2031, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        plate.UpdatedAtUtc = new DateTime(2032, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        plate.ExtensionData = Canary("document");
        plate.BasicIdentity = new BasicIdentityHeader { RegionWidth = 1234.5f, ExtensionData = Canary("identity") };
        plate.BasicPlate = new BasicPlateSettings { FavoriteJobId = 0x6E6F7071, Level = 0x10203040, ExtensionData = Canary("basic") };
        var frame = ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameDouble);
        frame.ExtensionData = Canary("component");
        plate.Components = [frame];
        for (var index = 0; index < plate.Elements.Count; index++)
        {
            plate.Elements[index].Name = "CanaryName" + index.ToString(CultureInfo.InvariantCulture);
            plate.Elements[index].ExtensionData = Canary("element" + index.ToString(CultureInfo.InvariantCulture));
        }

        var candidate = PublicationCandidates.Build(plate);
        Assert.Equal(PublishResult.Stored, fixture.Commit(candidate).Result);
        var saved = File.ReadAllBytes(fixture.Files.IndexPath(fixture.Persona.Slot));
        var entry = File.ReadAllBytes(Directory.GetFiles(fixture.Files.OutboxPath(fixture.Persona.Slot)).Single());

        var texts = new List<string> { "CanaryHiddenText", "CanaryOutsideText", "Tester" };
        texts.AddRange(plate.Elements.Select(e => e.Name));
        texts.AddRange(new[] { "background", "document", "identity", "basic", "component" }.Concat(Enumerable.Range(0, plate.Elements.Count).Select(i => "element" + i.ToString(CultureInfo.InvariantCulture))).Select(CanaryValue));
        foreach (var text in texts)
        {
            foreach (var encoding in new Encoding[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode })
            {
                Assert.False(Contains(saved, encoding.GetBytes(text)), text);
                Assert.False(Contains(entry, encoding.GetBytes(text)), text);
            }
        }

        var ids = new List<Guid> { stale, frame.Id };
        ids.AddRange(plate.Elements.Select(e => e.Id));
        ids.AddRange(plate.Elements.OfType<ImageProfileElement>().Select(e => e.AssetId));
        foreach (var id in ids)
        {
            foreach (var bytes in new[] { id.ToByteArray(), id.ToByteArray(bigEndian: true) })
            {
                Assert.False(Contains(saved, bytes), $"local id {id}");
                Assert.False(Contains(entry, bytes), $"local id {id}");
            }
        }

        // The Plate's own id is in the index, by design (P1), and nowhere in what is shared.
        Assert.True(Contains(saved, plate.ProfileId.ToByteArray(bigEndian: true)));
        Assert.False(Contains(entry, plate.ProfileId.ToByteArray()));
        Assert.False(Contains(entry, plate.ProfileId.ToByteArray(bigEndian: true)));

        // No slot and no key reach a name: the index is named by slot, and the entry by chance.
        Assert.False(Contains(saved, fixture.Persona.PublicKey.ToArray()));
        foreach (var number in new[]
        {
            BitConverter.GetBytes(plate.OwnerContentId),
            BitConverter.GetBytes(plate.Revision),
            BitConverter.GetBytes(new DateTimeOffset(plate.CreatedAtUtc).ToUnixTimeSeconds()),
            BitConverter.GetBytes(new DateTimeOffset(plate.UpdatedAtUtc).ToUnixTimeSeconds()),
            BitConverter.GetBytes(plate.CreatedAtUtc.Ticks),
            BitConverter.GetBytes(plate.UpdatedAtUtc.Ticks),
            BitConverter.GetBytes(0x6E6F7071u),
            BitConverter.GetBytes(0x10203040),
        })
        {
            foreach (var bytes in new[] { number, number.Reverse().ToArray() })
            {
                Assert.False(Contains(saved, bytes));
                Assert.False(Contains(entry, bytes));
            }
        }
    }

    [Fact]
    public void EveryAttempt_DrawsItsOwnRevision_EvenAfterAFailure()
    {
        var plate = Guid.NewGuid();
        Directory.CreateDirectory(fixture.Files.IndexPath(fixture.Persona.Slot) + ".tmp");
        Assert.Equal(PublishResult.NotSaved, fixture.Commit(PublicationCandidates.Simple(plate)).Result);
        Directory.Delete(fixture.Files.IndexPath(fixture.Persona.Slot) + ".tmp");

        // The failed attempt's entry was never named: its revision is never sent, and never signed again.
        var abandoned = fixture.OutboxEntries().Single();
        var abandonedRevision = OutboxEntryCodec.Decode(fixture.Files.ReadEntry(fixture.Persona.Slot, abandoned)!, fixture.Persona.PublicKey).Snapshot.RevisionId;

        var outcome = fixture.Commit(PublicationCandidates.Simple(plate));

        Assert.Equal(PublishResult.Stored, outcome.Result);
        Assert.NotEqual(abandonedRevision, outcome.Revision);
    }

    private byte[] Newer()
    {
        var bytes = PublicationIndexCodec.Encode(fixture.Persona.Slot, PublicationIndex.Empty);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 2);
        return bytes;
    }

    private void SetSize(OutboxEntryName name, long size)
    {
        using var stream = new FileStream(Path.Combine(fixture.Files.OutboxPath(fixture.Persona.Slot), name.FileName), FileMode.OpenOrCreate);
        stream.SetLength(size);
    }

    private static Dictionary<string, JsonElement> Canary(string where) => new() { ["canary" + where] = JsonSerializer.SerializeToElement(CanaryValue(where)) };

    private static string CanaryValue(string where) => "CanaryValue" + where;

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}
