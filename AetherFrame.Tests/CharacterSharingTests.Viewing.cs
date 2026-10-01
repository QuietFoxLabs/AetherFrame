using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Services.Network.Sharing;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Viewing another player's Plate (N2-10): looked up only once one of the player's characters
/// shares, signed by that character's key, its images checked before they are drawn, held in
/// memory only, and never looked up for a player the player hid.
/// </summary>
public partial class CharacterSharingTests
{
    [Fact]
    public void NothingIsLookedUp_UntilACharacterShares()
    {
        using var harness = new SharingHarness();
        var viewing = Viewing(harness);
        Assert.False(viewing.CanView);

        Assert.True(viewing.Open("Bram Oakes", "Gilgamesh"));
        Assert.Equal((ViewStage.Failed, ViewFailure.NotSharing), (viewing.View.Stage, viewing.View.Failure));
        viewing.OnFrame();
        Assert.Empty(harness.Server.Actions);
        Assert.Equal(0, harness.Server.Challenges);
    }

    [Fact]
    public void ALookup_IsSignedByTheSharedCharactersKey_AndShowsThePlateWithItsImage()
    {
        using var harness = new SharingHarness();
        var aria = harness.Bound();
        var png = Png(4, 3);
        var served = ServedBytes(png, out var marker);
        harness.Server.Bytes["/v1/lookup"] = _ => (HttpStatusCode.OK, served);
        harness.Server.Bytes["/v1/image"] = _ => (HttpStatusCode.OK, png);
        var viewing = Viewing(harness);
        Assert.True(viewing.CanView);

        Assert.True(viewing.Open("  bram   oakes ", "Gilgamesh"));
        Assert.Equal(ViewStage.Waiting, viewing.View.Stage);
        Assert.DoesNotContain(harness.Server.Actions, action => action.Path == "/v1/lookup");
        viewing.OnFrame();

        var view = viewing.View;
        Assert.Equal(ViewStage.Shown, view.Stage);
        Assert.Equal(new PlateTarget("bram oakes", "Gilgamesh"), view.Target);
        var plate = Assert.IsType<ViewedPlate>(view.Plate);
        Assert.Equal("Bram's Plate", plate.Plate.Name);
        Assert.Equal(png, Assert.Single(plate.Images));
        Assert.Equal(0, plate.ImagesRefused);

        var lookup = harness.Server.Actions.Single(action => action.Path == "/v1/lookup");
        Assert.Equal((aria.Key, "{\"name\":\"bram oakes\",\"world\":\"Gilgamesh\"}"), (lookup.Signer, lookup.Body));
        var image = harness.Server.Actions.Single(action => action.Path == "/v1/image");
        Assert.Equal((aria.Key, "{\"name\":\"bram oakes\",\"world\":\"Gilgamesh\",\"marker\":\"" + marker + "\",\"index\":0}"), (image.Signer, image.Body));

        // The log names outcomes only: never who was looked up.
        Assert.DoesNotContain(harness.Log, line => line.Contains("bram", StringComparison.OrdinalIgnoreCase) || line.Contains("Gilgamesh", StringComparison.Ordinal));
    }

    [Fact]
    public void AnImageThatIsntWhatItsEntrySays_IsLeftOut()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        harness.Server.Bytes["/v1/lookup"] = _ => (HttpStatusCode.OK, ServedBytes(Png(4, 3), out RevisionMarker _));
        harness.Server.Bytes["/v1/image"] = _ => (HttpStatusCode.OK, Png(5, 3));
        var viewing = Viewing(harness);
        viewing.Open("Bram Oakes", "Gilgamesh");
        viewing.OnFrame();

        var plate = viewing.View.Plate!;
        Assert.Null(Assert.Single(plate.Images));
        Assert.Equal(1, plate.ImagesRefused);
        Assert.False(PlateViewing.Accepts(Encoding.ASCII.GetBytes("GIF89a"), new ServedImage(ImageFormat.Png, 4, 3)));
    }

    [Fact]
    public void NotFound_ClearsWhatWasShown_AndAServedPlateThatCantBeReadIsRefused()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        var png = Png(4, 3);
        harness.Server.Bytes["/v1/lookup"] = _ => (HttpStatusCode.OK, ServedBytes(png, out RevisionMarker _));
        harness.Server.Bytes["/v1/image"] = _ => (HttpStatusCode.OK, png);
        var viewing = Viewing(harness);
        viewing.Open("Bram Oakes", "Gilgamesh");
        viewing.OnFrame();
        Assert.NotNull(viewing.View.Plate);

        harness.Server.Bytes["/v1/lookup"] = _ => (HttpStatusCode.NotFound, null);
        Assert.True(viewing.Refresh());
        viewing.OnFrame();
        Assert.Equal(ViewStage.NotFound, viewing.View.Stage);
        Assert.Null(viewing.View.Plate);

        harness.Server.Bytes["/v1/lookup"] = _ => (HttpStatusCode.OK, Encoding.ASCII.GetBytes("AFSP but not really"));
        viewing.Refresh();
        viewing.OnFrame();
        Assert.Equal((ViewStage.Failed, ViewFailure.Refused), (viewing.View.Stage, viewing.View.Failure));

        harness.Server.Bytes["/v1/lookup"] = _ => (HttpStatusCode.TooManyRequests, null);
        viewing.Refresh();
        viewing.OnFrame();
        Assert.Equal(ViewFailure.TooMany, viewing.View.Failure);

        harness.Server.Unreachable = true;
        viewing.Refresh();
        viewing.OnFrame();
        Assert.Equal(ViewFailure.Unreachable, viewing.View.Failure);
    }

    [Fact]
    public void AHiddenPlayer_IsNeverLookedUp_AndStaysHiddenAfterARestart()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        var png = Png(4, 3);
        harness.Server.Bytes["/v1/lookup"] = _ => (HttpStatusCode.OK, ServedBytes(png, out RevisionMarker _));
        harness.Server.Bytes["/v1/image"] = _ => (HttpStatusCode.OK, png);
        var viewing = Viewing(harness);
        viewing.Open("Bram Oakes", "Gilgamesh");
        viewing.OnFrame();

        Assert.True(viewing.Hide());
        Assert.Equal(ViewStage.Hidden, viewing.View.Stage);
        Assert.Null(viewing.View.Plate);

        var lookups = harness.Server.Actions.Count(action => action.Path == "/v1/lookup");
        var again = Viewing(harness);
        again.Open("BRAM OAKES", "gilgamesh");
        again.OnFrame();
        Assert.Equal(ViewStage.Hidden, again.View.Stage);
        Assert.Equal(lookups, harness.Server.Actions.Count(action => action.Path == "/v1/lookup"));

        Assert.True(again.Unhide());
        again.OnFrame();
        Assert.Equal(ViewStage.Shown, again.View.Stage);
        Assert.False(new HiddenPlates(harness.Root, harness.Log.Add).IsHidden("Bram Oakes", "Gilgamesh"));
    }

    [Fact]
    public void AReport_NamesThePlayerAndAReason_AndIsSentOnce()
    {
        using var harness = new SharingHarness();
        var aria = harness.Bound();
        var png = Png(4, 3);
        harness.Server.Bytes["/v1/lookup"] = _ => (HttpStatusCode.OK, ServedBytes(png, out RevisionMarker _));
        harness.Server.Bytes["/v1/image"] = _ => (HttpStatusCode.OK, png);
        harness.Server.Bytes["/v1/report"] = _ => (HttpStatusCode.NoContent, null);
        var viewing = Viewing(harness);
        Assert.False(viewing.Report("spam"));
        viewing.Open("Bram Oakes", "Gilgamesh");
        viewing.OnFrame();

        Assert.False(viewing.Report("because"));
        Assert.True(viewing.Report("spam"));
        Assert.Equal(ReportStage.Sending, viewing.View.Report);
        viewing.OnFrame();
        Assert.Equal(ReportStage.Sent, viewing.View.Report);
        Assert.False(viewing.Report("spam"));

        var report = Assert.Single(harness.Server.Actions, action => action.Path == "/v1/report");
        Assert.Equal((aria.Key, "{\"name\":\"Bram Oakes\",\"world\":\"Gilgamesh\",\"reason\":\"spam\"}"), (report.Signer, report.Body));
    }

    [Fact]
    public void ABusySession_KeepsTheRequestWaiting_AndANewerOneReplacesIt()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        harness.Server.Bytes["/v1/lookup"] = _ => (HttpStatusCode.NotFound, null);
        var busy = true;
        var viewing = new PlateViewing((_, work) =>
        {
            if (busy)
            {
                return false;
            }

            work(harness.Personas);
            return true;
        }, () => harness.Sharing.View, () => Aria, harness.Client, new HiddenPlates(harness.Root, harness.Log.Add), CancellationToken.None, harness.Log.Add);

        viewing.Open("Bram Oakes", "Gilgamesh");
        viewing.OnFrame();
        Assert.Equal(ViewStage.Waiting, viewing.View.Stage);
        viewing.Open("Cara Vell", "Gilgamesh");

        busy = false;
        viewing.OnFrame();
        Assert.Equal((ViewStage.NotFound, "Cara Vell"), (viewing.View.Stage, viewing.View.Target!.Name));
        var lookup = Assert.Single(harness.Server.Actions, action => action.Path == "/v1/lookup");
        Assert.Contains("Cara Vell", lookup.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Aria Starfall", true)]
    [InlineData("A'ria Star-fall", true)]
    [InlineData("Aria", false)]
    [InlineData("Aria Star Fall", false)]
    [InlineData("Aria St4rfall", false)]
    [InlineData("Ariaaaaaaaaaaaaaa Starfall", false)]
    [InlineData("", false)]
    public void ANameIsTwoWordsAsTheGameAllows(string name, bool valid) => Assert.Equal(valid, PlateViewing.IsName(name));

    [Fact]
    public void AnUnreadableHiddenFile_HidesNobody_AndIsNeverWrittenOver()
    {
        using var harness = new SharingHarness();
        Directory.CreateDirectory(harness.Root);
        var path = Path.Combine(harness.Root, HiddenPlates.FileName);
        File.WriteAllText(path, "{ not json");

        var hidden = new HiddenPlates(harness.Root, harness.Log.Add);
        Assert.False(hidden.IsHidden("Bram Oakes", "Gilgamesh"));
        Assert.True(hidden.Unreadable);
        Assert.False(hidden.Hide("Bram Oakes", "Gilgamesh"));
        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    private static PlateViewing Viewing(SharingHarness harness) =>
        new((_, work) =>
        {
            work(harness.Personas);
            return true;
        }, () => harness.Sharing.View, () => Aria, harness.Client, new HiddenPlates(harness.Root, harness.Log.Add), CancellationToken.None, harness.Log.Add);

    /// <summary>A served profile with one image, as the server builds one.</summary>
    private static byte[] ServedBytes(byte[] png, out RevisionMarker marker)
    {
        marker = RevisionMarker.NewMarker();
        var asset = AssetId.NewId();
        var items = new LayoutItem[] { new LayoutImage(asset, new LayoutPoint(0, 0), 40_000, 30_000, 0, LayoutImageFit.Stretch, LayoutFlips.None, 255) };
        var images = new[] { new ImageReference(asset, SHA256.HashData(png), ImageFormat.Png, png.Length, 4, 3) };
        var snapshot = new ProfileLayoutSnapshot(ProfileId.NewId(), RevisionId.NewId(), 1_790_000_000, "Bram's Plate", 80_000, 60_000, LayoutBackground.None, items, images);
        return ServedProfile.Build(snapshot, marker);
    }

    /// <summary>A PNG section 8.2.1 allows: 8-bit RGBA, not interlaced.</summary>
    private static byte[] Png(int width, int height)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        (header[8], header[9]) = (8, 6);
        var rows = new byte[height * (1 + (width * 4))];
        using var deflated = new MemoryStream();
        using (var zlib = new ZLibStream(deflated, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(rows);
        }

        return [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. PngChunk("IHDR", header), .. PngChunk("IDAT", deflated.ToArray()), .. PngChunk("IEND", [])];
    }

    private static byte[] PngChunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        var crc = 0xFFFF_FFFFu;
        foreach (var value in chunk.AsSpan(4, 4 + data.Length))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB8_8320u ^ (crc >> 1) : crc >> 1;
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), ~crc);
        return chunk;
    }
}
