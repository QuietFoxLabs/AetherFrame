using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Protocol.Requests;
using AetherFrame.Server.Endpoints;
using AetherFrame.Server.Images;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AetherFrame.Server.Tests;

/// <summary>
/// Publishing and viewing, end to end (ServerApi-v1.md, sections 2.1 and 2.2; decisions N2, N6, D6,
/// I2 and C3 to C8). The image worker is a fake that returns what it's given, which the server still
/// checks as untrusted output.
/// </summary>
public class PlateEndpointTests
{
    private const long Aria = 12345678;
    private const long Bram = 23456789;
    private const long Off = 45678901;

    [Fact]
    public async Task APublishedPlate_IsServedToAnotherOptedInPlayer_WithItsImagesByMarker()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        using var bram = server.NewPlayer();
        var profile = ProfileId.Parse((await aria.BindAsync(Aria, "Aria Starfall", "Gilgamesh")).GetProperty("profileId").GetString()!);
        await bram.BindAsync(Bram, "Bram Oakes", "Gilgamesh");

        var png = Plates.Png(4, 3);
        using (var published = await aria.PublishAsync(Plates.Snapshot(profile, "My Plate", png), png))
        {
            Assert.Equal(HttpStatusCode.NoContent, published.StatusCode);
        }

        using var lookup = await bram.SendAsync("/v1/lookup", RequestProofKind.Lookup, "{\"name\":\"aria  starfall\",\"world\":\"GILGAMESH\"}");
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
        var served = ServedProfile.Read(await lookup.Content.ReadAsByteArrayAsync());
        Assert.Equal("My Plate", served.Name);
        Assert.Equal((ImageFormat.Png, 4, 3), (served.Images[0].Format, served.Images[0].Width, served.Images[0].Height));

        using var image = await bram.SendAsync("/v1/image", RequestProofKind.Image, $"{{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\",\"marker\":\"{served.Marker}\",\"index\":0}}");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, await image.Content.ReadAsByteArrayAsync());

        // A new revision replaces it: the old marker gets nothing, and the old revision's rows are gone.
        using (var republished = await aria.PublishAsync(Plates.Snapshot(profile, "My Plate, again", png), png))
        {
            Assert.Equal(HttpStatusCode.NoContent, republished.StatusCode);
        }

        using var stale = await bram.SendAsync("/v1/image", RequestProofKind.Image, $"{{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\",\"marker\":\"{served.Marker}\",\"index\":0}}");
        Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
        Assert.Equal(1L, await server.CountAsync("SELECT COUNT(*) FROM latest;"));
        Assert.Equal(1L, await server.CountAsync("SELECT COUNT(*) FROM images;"));
        Assert.Equal(2L, await server.CountAsync("SELECT COUNT(*) FROM revisions;"));
    }

    [Fact]
    public async Task ARevision_IsIdempotentWithTheSameBytes_AndRefusedWithOthers()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        var profile = ProfileId.Parse((await aria.BindAsync(Aria)).GetProperty("profileId").GetString()!);
        var revision = RevisionId.NewId();
        var document = SignedDocumentCodec.Sign(Plates.Snapshot(profile, "One", revision: revision), aria.Key);
        using (var first = await aria.PublishDocumentAsync(document))
        {
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        }

        using (var again = await aria.PublishDocumentAsync(document))
        {
            Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        }

        using var other = await aria.PublishDocumentAsync(SignedDocumentCodec.Sign(Plates.Snapshot(profile, "Two", revision: revision), aria.Key));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, other.StatusCode);
        Assert.Equal("revision-conflict", await other.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("wrong-profile")]
    [InlineData("clock-ahead")]
    [InlineData("not-a-layout")]
    [InlineData("not-bound")]
    public async Task APublish_IsRefusedWithItsReason(string reason)
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        var profile = reason == "not-bound" ? ProfileId.NewId() : ProfileId.Parse((await aria.BindAsync(Aria)).GetProperty("profileId").GetString()!);
        RemoteDocument model = reason switch
        {
            "wrong-profile" => Plates.Snapshot(ProfileId.NewId(), "Plate"),
            "clock-ahead" => Plates.Snapshot(profile, "Plate", createdAt: server.Time.Now.ToUnixTimeSeconds() + 301),
            "not-a-layout" => new ProfileRetraction(profile, server.Time.Now.ToUnixTimeSeconds()),
            _ => Plates.Snapshot(profile, "Plate"),
        };

        using var response = await aria.PublishDocumentAsync(SignedDocumentCodec.Sign(model, aria.Key));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(reason, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ASnapshotAtTheClockBound_IsAccepted()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        var profile = ProfileId.Parse((await aria.BindAsync(Aria)).GetProperty("profileId").GetString()!);
        using var response = await aria.PublishDocumentAsync(SignedDocumentCodec.Sign(Plates.Snapshot(profile, "Plate", createdAt: server.Time.Now.ToUnixTimeSeconds() + 300), aria.Key));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task AnotherKeysDocument_OrAProofOfAnotherDocument_IsRefused()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        using var bram = server.NewPlayer();
        var profile = ProfileId.Parse((await aria.BindAsync(Aria)).GetProperty("profileId").GetString()!);
        var document = SignedDocumentCodec.Sign(Plates.Snapshot(profile, "Plate"), aria.Key);

        // Bram submits Aria's document under his own proof: a persona proves only its own documents.
        var challenge = await bram.ChallengeAsync();
        Assert.Throws<ProtocolException>(() => RequestProofCodec.Sign(document, DeploymentName.Parse(TestServer.Deployment), challenge, bram.Key));

        // Aria's proof of one document, sent with another.
        var otherDocument = SignedDocumentCodec.Sign(Plates.Snapshot(profile, "Other"), aria.Key);
        var ariaChallenge = await aria.ChallengeAsync();
        var proof = RequestProofCodec.Sign(document, DeploymentName.Parse(TestServer.Deployment), ariaChallenge, aria.Key);
        using var swapped = await aria.PostRawAsync("/v1/publish", Player.Envelope(proof, Plates.PublishPayload(otherDocument)));
        Assert.Equal(HttpStatusCode.Forbidden, swapped.StatusCode);
    }

    [Fact]
    public async Task Images_MustMatchTheirDeclaration_AndPassTheWorker()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        var profile = ProfileId.Parse((await aria.BindAsync(Aria)).GetProperty("profileId").GetString()!);
        var png = Plates.Png(4, 3);

        // Bytes that differ from the declaration.
        var other = Plates.Png(4, 3, fill: 0x22);
        using (var mismatch = await aria.PublishAsync(Plates.Snapshot(profile, "Plate", png), other))
        {
            Assert.Equal("image-refused", await mismatch.Content.ReadAsStringAsync());
        }

        // A worker that answers with something else: its output is untrusted.
        server.Images.Answer = _ => Plates.Png(5, 3);
        using (var wrongOutput = await aria.PublishAsync(Plates.Snapshot(profile, "Plate", png), png))
        {
            Assert.Equal("image-refused", await wrongOutput.Content.ReadAsStringAsync());
        }

        // A worker that refuses.
        server.Images.Answer = _ => null;
        using (var refused = await aria.PublishAsync(Plates.Snapshot(profile, "Plate", png), png))
        {
            Assert.Equal("image-refused", await refused.Content.ReadAsStringAsync());
        }

        // Too few images for the snapshot.
        server.Images.Answer = bytes => bytes;
        using var missing = await aria.PublishDocumentAsync(SignedDocumentCodec.Sign(Plates.Snapshot(profile, "Plate", png), aria.Key));
        Assert.Equal("image-refused", await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WithoutTheWorker_EveryImageIsRefused()
    {
        var processor = new NoImageProcessor();
        var png = Plates.Png(4, 3);
        var processed = await processor.ProcessAsync(Plates.Snapshot(ProfileId.NewId(), "Plate", png).Images[0], png, CancellationToken.None);
        Assert.Null(processed.Bytes);
        Assert.False(processed.IsBusy);
    }

    [Fact]
    public async Task ABusyWorker_IsTryAgainLater()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        var profile = ProfileId.Parse((await aria.BindAsync(Aria)).GetProperty("profileId").GetString()!);
        var png = Plates.Png(2, 2);
        server.Images.Busy = true;
        using var response = await aria.PublishAsync(Plates.Snapshot(profile, "Plate", png), png);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task APublishIsAuthenticated_BeforeItWaitsForASlot()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        using var stranger = server.NewPlayer();
        var profile = ProfileId.Parse((await aria.BindAsync(Aria)).GetProperty("profileId").GetString()!);
        var slots = server.Services.GetRequiredService<PublishSlots>();

        // Every slot held, as slow uploads by other characters from other addresses would hold them.
        var held = Enumerable.Range(0, PublishSlots.Total)
            .Select(index => slots.TryTake(90_000_000 + index, System.Net.IPAddress.Parse("198.51.100." + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))))
            .ToList();
        Assert.All(held, Assert.NotNull);
        try
        {
            // A stranger is refused at its proof and signer, whatever the slots: it can never hold one.
            using (var strangerPublish = await stranger.PublishDocumentAsync(SignedDocumentCodec.Sign(Plates.Snapshot(ProfileId.NewId(), "Plate"), stranger.Key)))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, strangerPublish.StatusCode);
                Assert.Equal("not-bound", await strangerPublish.Content.ReadAsStringAsync());
            }

            // A proof with a challenge the server never issued doesn't reach the slots either.
            var document = SignedDocumentCodec.Sign(Plates.Snapshot(profile, "Plate"), aria.Key);
            var unissued = RequestProofCodec.Sign(document, DeploymentName.Parse(TestServer.Deployment), RequestChallenge.NewRandom(), aria.Key);
            using (var stale = await aria.PostRawAsync("/v1/publish", Player.Envelope(unissued, Plates.PublishPayload(document))))
            {
                Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            }

            // The tester, authenticated, is told to retry.
            using var tester = await aria.PublishDocumentAsync(document);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, tester.StatusCode);
        }
        finally
        {
            held.ForEach(slot => slot!.Dispose());
        }

        using var afterwards = await aria.PublishDocumentAsync(SignedDocumentCodec.Sign(Plates.Snapshot(profile, "Plate"), aria.Key));
        Assert.Equal(HttpStatusCode.NoContent, afterwards.StatusCode);
    }

    [Fact]
    public async Task ThePublishStore_RechecksTheBindingAndTheRevisionInItsTransaction()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        var profile = ProfileId.Parse((await aria.BindAsync(Aria)).GetProperty("profileId").GetString()!);
        var content = server.Services.GetRequiredService<AetherFrame.Server.Storage.ContentStore>();
        var persona = aria.Key.PublicKey.Id;
        var revision = RevisionId.NewId();
        var sha = new byte[32];
        sha[0] = 1;
        var marker = RevisionMarker.NewMarker();

        // A binding that changed while the revision was checked: nothing is stored.
        Assert.Equal(AetherFrame.Server.Storage.PublishResult.NotBound, await content.PublishAsync(persona, ProfileId.NewId(), revision, [1], sha, [1], marker, [], default));
        Assert.Equal(AetherFrame.Server.Storage.PublishResult.Published, await content.PublishAsync(persona, profile, revision, [1], sha, [1], marker, [], default));

        // A revision that turned up meanwhile: the same bytes are known, other bytes conflict.
        Assert.Equal(AetherFrame.Server.Storage.PublishResult.AlreadyKnown, await content.PublishAsync(persona, profile, revision, [1], sha, [1], marker, [], default));
        var other = (byte[])sha.Clone();
        other[1] = 2;
        Assert.Equal(AetherFrame.Server.Storage.PublishResult.Conflict, await content.PublishAsync(persona, profile, revision, [2], other, [1], marker, [], default));
        Assert.Equal(1L, await server.CountAsync("SELECT COUNT(*) FROM revisions;"));
    }

    [Fact]
    public async Task ACharacterOffTheAllowlist_NeitherViewsNorIsViewed()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        using var bram = server.NewPlayer();
        var ariaProfile = ProfileId.Parse((await aria.BindAsync(Aria)).GetProperty("profileId").GetString()!);
        var bramProfile = ProfileId.Parse((await bram.BindAsync(Bram, "Bram Oakes", "Gilgamesh")).GetProperty("profileId").GetString()!);
        using (await aria.PublishDocumentAsync(SignedDocumentCodec.Sign(Plates.Snapshot(ariaProfile, "Plate"), aria.Key)))
        {
        }

        using (await bram.PublishDocumentAsync(SignedDocumentCodec.Sign(Plates.Snapshot(bramProfile, "Plate"), bram.Key)))
        {
        }

        // The operator takes Bram's id off the allowlist; the configuration reloads.
        var configuration = (Microsoft.Extensions.Configuration.IConfigurationRoot)server.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        configuration["AetherFrame:AllowedLodestoneIds:1"] = "99999999";
        configuration.Reload();

        using (var bramViews = await bram.SendAsync("/v1/lookup", RequestProofKind.Lookup, "{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, bramViews.StatusCode);
        }

        using (var bramViewed = await aria.SendAsync("/v1/lookup", RequestProofKind.Lookup, "{\"name\":\"Bram Oakes\",\"world\":\"Gilgamesh\"}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, bramViewed.StatusCode);
        }

        using var bramPublishes = await bram.PublishDocumentAsync(SignedDocumentCodec.Sign(Plates.Snapshot(bramProfile, "Plate again"), bram.Key));
        Assert.Equal("not-bound", await bramPublishes.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AHiddenBindingsPlate_IsNotServed()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        using var namesake = server.NewPlayer();
        using var bram = server.NewPlayer();
        var ariaProfile = ProfileId.Parse((await aria.BindAsync(Aria, "Aria Starfall", "Gilgamesh")).GetProperty("profileId").GetString()!);
        using (await aria.PublishDocumentAsync(SignedDocumentCodec.Sign(Plates.Snapshot(ariaProfile, "Plate"), aria.Key)))
        {
        }

        // A newer check reads the same name and World for another character: Aria's binding is
        // hidden, and the name now finds the newer one, which has published nothing.
        await namesake.BindAsync(34567890, "Aria Starfall", "Gilgamesh");
        await bram.BindAsync(Bram, "Bram Oakes", "Gilgamesh");
        using var lookup = await bram.SendAsync("/v1/lookup", RequestProofKind.Lookup, "{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}");
        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0 })]
    [InlineData(new byte[] { 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 0, 0, 1, 7 })]
    public void APublishPayload_IsReadStrictly(byte[] payload) => Assert.False(PlateEndpoints.TryReadPublish(payload, out _, out _));

    [Fact]
    public async Task Viewing_NeedsTheViewerBoundAndAllowed_AndAnswersNotFoundForEveryOtherCause()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        using var stranger = server.NewPlayer();
        var profile = ProfileId.Parse((await aria.BindAsync(Aria)).GetProperty("profileId").GetString()!);
        using (await aria.PublishDocumentAsync(SignedDocumentCodec.Sign(Plates.Snapshot(profile, "Plate"), aria.Key)))
        {
        }

        // Not opted in: nothing, although the Plate exists.
        using (var notBound = await stranger.SendAsync("/v1/lookup", RequestProofKind.Lookup, "{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, notBound.StatusCode);
        }

        // Opted in: found; an unknown name, a World off the list, or a character with nothing published: not found.
        await stranger.BindAsync(Bram, "Bram Oakes", "Gilgamesh");
        foreach (var (name, world, expected) in new[]
        {
            ("Aria Starfall", "Gilgamesh", HttpStatusCode.OK),
            ("Aria Starfal", "Gilgamesh", HttpStatusCode.NotFound),
            ("Aria Starfall", "Balmung", HttpStatusCode.NotFound),
            ("Aria Starfall", "Atlantis", HttpStatusCode.NotFound),
            ("Bram Oakes", "Gilgamesh", HttpStatusCode.NotFound),
        })
        {
            using var response = await stranger.SendAsync("/v1/lookup", RequestProofKind.Lookup, $"{{\"name\":\"{name}\",\"world\":\"{world}\"}}");
            Assert.Equal(expected, response.StatusCode);
            if (expected == HttpStatusCode.NotFound)
            {
                Assert.Empty(await response.Content.ReadAsByteArrayAsync());
            }
        }
    }

    [Fact]
    public async Task Pausing_HidesThePlateUntilTheNextPublish_AndOptingOutDeletesEverything()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        using var bram = server.NewPlayer();
        var profile = ProfileId.Parse((await aria.BindAsync(Aria)).GetProperty("profileId").GetString()!);
        await bram.BindAsync(Bram, "Bram Oakes", "Gilgamesh");
        var png = Plates.Png(2, 2);
        using (await aria.PublishAsync(Plates.Snapshot(profile, "Plate", png), png))
        {
        }

        async Task<HttpStatusCode> LookupAsync()
        {
            using var response = await bram.SendAsync("/v1/lookup", RequestProofKind.Lookup, "{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}");
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await LookupAsync());
        using (var paused = await aria.SendAsync("/v1/opt-out", RequestProofKind.OptOut, "{\"mode\":\"pause\"}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, paused.StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, await LookupAsync());
        Assert.Equal(0L, await server.CountAsync("SELECT COUNT(*) FROM images;"));
        using (await aria.PublishAsync(Plates.Snapshot(profile, "Plate, back", png), png))
        {
        }

        Assert.Equal(HttpStatusCode.OK, await LookupAsync());
        using (await aria.SendAsync("/v1/opt-out", RequestProofKind.OptOut, "{}"))
        {
        }

        Assert.Equal(HttpStatusCode.NotFound, await LookupAsync());
        foreach (var table in new[] { "latest", "images", "revisions" })
        {
            Assert.Equal(0L, await server.CountAsync($"SELECT COUNT(*) FROM {table};"));
        }

        using var badMode = await aria.SendAsync("/v1/opt-out", RequestProofKind.OptOut, "{\"mode\":\"later\"}");
        Assert.Equal(HttpStatusCode.BadRequest, badMode.StatusCode);
    }

    [Fact]
    public async Task ATakenOverKey_IsToldSo()
    {
        using var server = new TestServer();
        using var oldPc = server.NewPlayer();
        using var newPc = server.NewPlayer();
        var profile = ProfileId.Parse((await oldPc.BindAsync(Aria)).GetProperty("profileId").GetString()!);
        await newPc.BindAsync(Aria);

        using (var publish = await oldPc.PublishDocumentAsync(SignedDocumentCodec.Sign(Plates.Snapshot(profile, "Plate"), oldPc.Key)))
        {
            Assert.Equal(HttpStatusCode.Gone, publish.StatusCode);
        }

        using (var reread = await oldPc.SendAsync("/v1/lodestone/reread", RequestProofKind.LodestoneReread, "{}"))
        {
            Assert.Equal(HttpStatusCode.Gone, reread.StatusCode);
        }

        // Opting out clears it; so would checking a character again.
        using (await oldPc.SendAsync("/v1/opt-out", RequestProofKind.OptOut, "{}"))
        {
        }

        using var after = await oldPc.SendAsync("/v1/lodestone/reread", RequestProofKind.LodestoneReread, "{}");
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task ATakeover_DeletesEverythingTheOldKeyPublished()
    {
        using var server = new TestServer();
        using var oldPc = server.NewPlayer();
        using var newPc = server.NewPlayer();
        var profile = ProfileId.Parse((await oldPc.BindAsync(Aria)).GetProperty("profileId").GetString()!);
        var png = Plates.Png(2, 2);
        using (await oldPc.PublishAsync(Plates.Snapshot(profile, "Plate", png), png))
        {
        }

        Assert.Equal(1L, await server.CountAsync("SELECT COUNT(*) FROM images;"));
        await newPc.BindAsync(Aria);
        foreach (var table in new[] { "latest", "images", "revisions" })
        {
            Assert.Equal(0L, await server.CountAsync($"SELECT COUNT(*) FROM {table};"));
        }
    }

    [Fact]
    public async Task AReport_IsKeptForTheOperator_And30DaysAtMost()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        using var bram = server.NewPlayer();
        var profile = ProfileId.Parse((await aria.BindAsync(Aria)).GetProperty("profileId").GetString()!);
        await bram.BindAsync(Bram, "Bram Oakes", "Gilgamesh");
        using (await aria.PublishDocumentAsync(SignedDocumentCodec.Sign(Plates.Snapshot(profile, "Plate"), aria.Key)))
        {
        }

        using (var report = await bram.SendAsync("/v1/report", RequestProofKind.Report, "{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\",\"reason\":\"spam\"}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, report.StatusCode);
        }

        using (var unknownReason = await bram.SendAsync("/v1/report", RequestProofKind.Report, "{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\",\"reason\":\"<b>\"}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknownReason.StatusCode);
        }

        Assert.Equal(1L, await server.CountAsync("SELECT COUNT(*) FROM reports WHERE lodestone_id = " + Aria + " AND reason = 'spam';"));

        // Housekeeping drops it after 30 days, with no new report to prompt it.
        var content = server.Services.GetRequiredService<AetherFrame.Server.Storage.ContentStore>();
        server.Time.Advance(TimeSpan.FromDays(29));
        Assert.Equal(0, await content.DropExpiredReportsAsync(default));
        server.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, await content.DropExpiredReportsAsync(default));
        Assert.Equal(0L, await server.CountAsync("SELECT COUNT(*) FROM reports;"));
    }

    [Fact]
    public async Task APublishOverItsBound_IsRefusedBeforeItIsRead()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/publish") { Content = new ByteArrayContent(new byte[PlateEndpoints.MaxPublishRequestBytes + 1]) };
        using var response = await aria.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task TheLog_HoldsNoMarkerNameOrContent()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        using var bram = server.NewPlayer();
        var profile = ProfileId.Parse((await aria.BindAsync(Aria)).GetProperty("profileId").GetString()!);
        await bram.BindAsync(Bram, "Bram Oakes", "Gilgamesh");
        var png = Plates.Png(2, 2);
        using (await aria.PublishAsync(Plates.Snapshot(profile, "Secret Plate Name", png), png))
        {
        }

        using var lookup = await bram.SendAsync("/v1/lookup", RequestProofKind.Lookup, "{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}");
        var served = ServedProfile.Read(await lookup.Content.ReadAsByteArrayAsync());
        using (await bram.SendAsync("/v1/image", RequestProofKind.Image, $"{{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\",\"marker\":\"{served.Marker}\",\"index\":0}}"))
        {
        }

        using (await bram.SendAsync("/v1/report", RequestProofKind.Report, "{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\",\"reason\":\"spam\"}"))
        {
        }

        var log = server.Log.All;
        Assert.Contains("/v1/publish", log, StringComparison.Ordinal);
        Assert.Contains("/v1/report", log, StringComparison.Ordinal);
        foreach (var secret in new[] { served.Marker.ToString(), served.Marker.ToString()[4..], "Secret Plate Name", profile.ToString(), "Aria", "Bram", "Gilgamesh", "spam", aria.Key.PublicKey.Id.ToString(), bram.Key.PublicKey.Id.ToString(), Aria.ToString(System.Globalization.CultureInfo.InvariantCulture), Bram.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        {
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        }
    }
}

/// <summary>Snapshots and images for the publish tests.</summary>
internal static class Plates
{
    public static ProfileLayoutSnapshot Snapshot(ProfileId profile, string name, byte[]? png = null, RevisionId? revision = null, long createdAt = 1_790_000_000)
    {
        if (png is null)
        {
            return new ProfileLayoutSnapshot(profile, revision ?? RevisionId.NewId(), createdAt, name, 128_000, 72_000, LayoutBackground.None, [], []);
        }

        var asset = AssetId.NewId();
        var sniffed = ImageSniffer.Sniff(png);
        var image = new ImageReference(asset, SHA256.HashData(png), sniffed.Format, png.Length, sniffed.Width, sniffed.Height);
        var item = new LayoutImage(asset, new LayoutPoint(0, 0), 10_000, 10_000, 0, LayoutImageFit.Fit, LayoutFlips.None, 255);
        return new ProfileLayoutSnapshot(profile, revision ?? RevisionId.NewId(), createdAt, name, 128_000, 72_000, LayoutBackground.None, [item], [image]);
    }

    /// <summary>A PNG section 8.2.1 accepts: 8-bit RGBA, one IDAT of zlib-compressed rows, and IEND.</summary>
    public static byte[] Png(int width, int height, byte fill = 0x11)
    {
        var raw = new byte[height * (1 + (width * 4))];
        for (var row = 0; row < height; row++)
        {
            Array.Fill(raw, fill, (row * (1 + (width * 4))) + 1, width * 4);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8;
        header[9] = 6;
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    public static byte[] PublishPayload(byte[] document, params byte[][] images)
    {
        using var payload = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)document.Length);
        payload.Write(length);
        payload.Write(document);
        payload.WriteByte((byte)images.Length);
        foreach (var image in images)
        {
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)image.Length);
            payload.Write(length);
            payload.Write(image);
        }

        return payload.ToArray();
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(typed);

        // Section 8.2.1 doesn't check a chunk's CRC, so these are zero.
        stream.Write(new byte[4]);
    }
}
