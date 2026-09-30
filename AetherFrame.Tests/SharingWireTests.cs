using System;
using System.IO;
using System.Text;
using AetherFrame.Personas;
using AetherFrame.Protocol.Identity;
using AetherFrame.Services.Network.Sharing;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>The sharing file, the Lodestone address, and the bodies and answers of N2-9b's requests.</summary>
public class SharingWireTests
{
    private static readonly PersonaId Key = PersonaId.Parse(PersonaId.Prefix + new string('a', 64));
    private static readonly ProfileId Profile = ProfileId.Parse("prf_0123456789abcdef0123456789abcdef");

    [Theory]
    [InlineData("12345678", "12345678")]
    [InlineData("  12345678  ", "12345678")]
    [InlineData("https://na.finalfantasyxiv.com/lodestone/character/12345678/", "12345678")]
    [InlineData("https://eu.finalfantasyxiv.com/lodestone/character/12345678", "12345678")]
    [InlineData("https://JP.finalfantasyxiv.com/lodestone/character/9/achievement/", "9")]
    [InlineData("de.finalfantasyxiv.com/lodestone/character/1234567890/", "1234567890")]
    [InlineData("http://fr.finalfantasyxiv.com/lodestone/character/42/?x=1#top", "42")]
    public void ALodestonePageAddress_GivesItsId(string address, string id)
    {
        Assert.True(LodestoneAddress.TryReadId(address, out var read));
        Assert.Equal(id, read);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("012345")]
    [InlineData("12345678901")]
    [InlineData("-5")]
    [InlineData("https://na.finalfantasyxiv.com/lodestone/character/012345/")]
    [InlineData("https://na.finalfantasyxiv.com/lodestone/character/")]
    [InlineData("https://na.finalfantasyxiv.com/lodestone/freecompany/12345678/")]
    [InlineData("https://na.finalfantasyxiv.com.example.com/lodestone/character/12345678/")]
    [InlineData("https://example.com/lodestone/character/12345678/")]
    [InlineData("https://user@na.finalfantasyxiv.com/lodestone/character/12345678/")]
    [InlineData("https://na.finalfantasyxiv.com:8443/lodestone/character/12345678/")]
    [InlineData("ftp://na.finalfantasyxiv.com/lodestone/character/12345678/")]
    [InlineData("https://na.finalfantasyxiv.com/other/lodestone/character/12345678/")]
    public void AnythingElse_IsRefused(string address)
    {
        Assert.False(LodestoneAddress.TryReadId(address, out _));
    }

    [Fact]
    public void ACode_IsAfAndTenCrockfordSymbols()
    {
        Assert.True(LodestoneCode.IsCode("AF-0123456789"));
        Assert.True(LodestoneCode.IsCode("AF-ABCDEFGHJK"));
        Assert.False(LodestoneCode.IsCode("AF-ABCDEFGHIK"));
        Assert.False(LodestoneCode.IsCode("AF-abcdefghjk"));
        Assert.False(LodestoneCode.IsCode("AF-012345678"));
        Assert.False(LodestoneCode.IsCode("XF-0123456789"));
        Assert.False(LodestoneCode.IsCode(null));
    }

    [Fact]
    public void Bodies_AreTheInterfacesJson()
    {
        Assert.Equal("{}", Encoding.UTF8.GetString(SharingWire.Empty()));
        Assert.Equal("{\"mode\":\"pause\"}", Encoding.UTF8.GetString(SharingWire.Pause()));
        Assert.Equal("{\"lodestoneId\":\"12345678\",\"code\":\"AF-0123456789\"}", Encoding.UTF8.GetString(SharingWire.Check("12345678", "AF-0123456789")));
        Assert.Throws<ArgumentException>(() => SharingWire.Check("012", "AF-0123456789"));
        Assert.Throws<ArgumentException>(() => SharingWire.Check("12345678", "AF-\"}"));
    }

    [Fact]
    public void Answers_AreReadStrictly()
    {
        Assert.Equal("AF-0123456789", SharingWire.ReadCode(Utf8("{\"code\":\"AF-0123456789\",\"expiresInSeconds\":3600}")));
        var check = SharingWire.ReadCheck(Utf8($"{{\"profileId\":\"{Profile}\",\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}}"));
        Assert.Equal(new CheckAnswer(Profile, "Aria Starfall", "Gilgamesh"), check);
        Assert.Equal(("Aria Starfall", "Gilgamesh"), SharingWire.ReadReread(Utf8("{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}")));
        Assert.Equal(new Version(0, 1, 6), SharingWire.ReadMinimumPlugin(Utf8("{\"protocolVersion\":32769,\"api\":1,\"minimumPlugin\":\"0.1.6\"}")));

        string[] refused =
        [
            "",
            "[]",
            "{\"code\":\"AF-0123456789\"}",
            "{\"code\":\"AF-0123456789\",\"expiresInSeconds\":3600,\"extra\":1}",
            "{\"code\":\"AF-0123456789\",\"code\":\"AF-0123456789\",\"expiresInSeconds\":3600}",
            "{\"code\":\"AF-0123456789\",\"expiresInSeconds\":\"3600\"}",
            "{\"code\":\"AF-01234\",\"expiresInSeconds\":3600}",
            "{\"code\":\"AF-0123456789\",\"expiresInSeconds\":3600} x",
        ];
        foreach (var text in refused)
        {
            Assert.Throws<InvalidDataException>(() => SharingWire.ReadCode(Utf8(text)));
        }

        // A JSON escape for a line feed: the name it decodes to holds a control character.
        Assert.Throws<InvalidDataException>(() => SharingWire.ReadCheck(Utf8($"{{\"profileId\":\"{Profile}\",\"name\":\"Aria\\nStarfall\",\"world\":\"Gilgamesh\"}}")));
        Assert.Throws<InvalidDataException>(() => SharingWire.ReadCheck(Utf8("{\"profileId\":\"prf_nope\",\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}")));
        Assert.Throws<InvalidDataException>(() => SharingWire.ReadReread(Utf8("{\"name\":\"\",\"world\":\"Gilgamesh\"}")));
        Assert.Throws<InvalidDataException>(() => SharingWire.ReadMinimumPlugin(Utf8("{\"protocolVersion\":32769,\"api\":1,\"minimumPlugin\":\"soon\"}")));
    }

    [Fact]
    public void TheSharingFile_RoundTripsEveryStage()
    {
        var slot = PersonaSlotId.NewId();
        var characters = new[]
        {
            new SharingCharacter(1, PersonaSlotId.NewId(), Key, SharingStage.Off),
            new SharingCharacter(2, PersonaSlotId.NewId(), Key, SharingStage.Checking),
            new SharingCharacter(3, slot, Key, SharingStage.Shared, "12345678", Profile, "Aria Starfall", "Gilgamesh"),
            new SharingCharacter(ulong.MaxValue, PersonaSlotId.NewId(), Key, SharingStage.Paused, "9", Profile, "Bram Oakes", "Cactuar"),
            new SharingCharacter(5, PersonaSlotId.NewId(), Key, SharingStage.TakenOver),
        };

        var decoded = SharingStateCodec.Decode(SharingStateCodec.Encode(characters));
        Assert.Equal(characters, decoded);
    }

    [Fact]
    public void TheSharingFile_RefusesWhatItDoesntHold()
    {
        var slot = PersonaSlotId.NewId();
        var good = $"{{\"contentId\":\"3\",\"slot\":\"{slot}\",\"key\":\"{Key}\",\"stage\":\"shared\",\"lodestoneId\":\"12345678\",\"profileId\":\"{Profile}\",\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}}";
        Assert.Single(SharingStateCodec.Decode(Utf8($"{{\"version\":1,\"characters\":[{good}]}}")));

        string[] refused =
        [
            "{}",
            "{\"version\":2,\"characters\":[]}",
            "{\"version\":1,\"characters\":[],\"more\":0}",
            "{\"version\":1,\"version\":1,\"characters\":[]}",
            $"{{\"version\":1,\"characters\":[{good},{good}]}}",
            $"{{\"version\":1,\"characters\":[{good.Replace("\"stage\":\"shared\"", "\"stage\":\"off\"", StringComparison.Ordinal)}]}}",
            $"{{\"version\":1,\"characters\":[{good.Replace("\"stage\":\"shared\"", "\"stage\":\"public\"", StringComparison.Ordinal)}]}}",
            $"{{\"version\":1,\"characters\":[{good.Replace("\"12345678\"", "null", StringComparison.Ordinal)}]}}",
            $"{{\"version\":1,\"characters\":[{good.Replace("\"contentId\":\"3\"", "\"contentId\":\"03\"", StringComparison.Ordinal)}]}}",
            $"{{\"version\":1,\"characters\":[{good.Replace("\"contentId\":\"3\"", "\"contentId\":\"0\"", StringComparison.Ordinal)}]}}",
            $"{{\"version\":1,\"characters\":[{good.Replace("\"contentId\":\"3\"", "\"contentId\":3", StringComparison.Ordinal)}]}}",
            $"{{\"version\":1,\"characters\":[{good.Replace(",\"world\":\"Gilgamesh\"", "", StringComparison.Ordinal)}]}}",
        ];
        foreach (var text in refused)
        {
            Assert.Throws<InvalidDataException>(() => SharingStateCodec.Decode(Utf8(text)));
        }

        Assert.Throws<InvalidDataException>(() => SharingStateCodec.Decode(new byte[SharingStateCodec.MaxBytes + 1]));
    }

    [Fact]
    public void TheSharingFile_IsReplacedWholeAndReadBack()
    {
        var root = Path.Combine(Path.GetTempPath(), "aetherframe-sharing-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = new SharingStateFile(root);
            Assert.Empty(file.Read());
            var first = new[] { new SharingCharacter(7, PersonaSlotId.NewId(), Key, SharingStage.Checking) };
            file.Replace(first);
            Assert.Equal(first, file.Read());
            var second = new[] { first[0] with { Stage = SharingStage.Shared, LodestoneId = "12345678", ProfileId = Profile, Name = "Aria Starfall", World = "Gilgamesh" } };
            file.Replace(second);
            Assert.Equal(second, file.Read());
            Assert.Equal(SharingStateFile.FileName, Path.GetFileName(Assert.Single(Directory.GetFiles(root))));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
