using System;
using System.IO;
using AetherFrame.Services.Diagnostics;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The log never shows a remote protocol identifier (docs/networking/NETWORK1.md, safeguard 7):
/// a persona identity, a profile, revision or asset id is replaced by a placeholder naming its
/// kind, exactly as a character binding file's name already is. Nothing else changes, and nothing
/// that merely resembles an identifier is touched.
/// </summary>
public class NetworkIdentifierPrivacyTests
{
    private const string Persona = "psn_" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Profile = "prf_a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1";
    private const string Revision = "rev_b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";
    private const string Asset = "ast_c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3";

    [Fact]
    public void Redact_ReplacesEveryIdentifierWithItsKind()
    {
        var text = $"persona {Persona} published {Profile} as {Revision} with {Asset} and {Asset}.";
        var redacted = LogPrivacy.Redact(text);
        Assert.Equal(
            $"persona {LogPrivacy.PersonaIdentity} published {LogPrivacy.ProfileIdentifier} as {LogPrivacy.RevisionIdentifier} with {LogPrivacy.AssetIdentifier} and {LogPrivacy.AssetIdentifier}.",
            redacted);
        Assert.DoesNotContain("psn_", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("a1a1", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("psn_0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("psn_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("psn_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    [InlineData("psn_a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1")]
    [InlineData("prf_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("xprf_a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1")]
    [InlineData("prf_a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1x")]
    [InlineData("prf-a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("Saved the Plate.")]
    public void Redact_LeavesEverythingThatIsNotAnIdentifier(string text)
    {
        Assert.Equal(text, LogPrivacy.Redact(text));
    }

    [Fact]
    public void Redact_StillHidesCharacterBindingFilesInTheSamePass()
    {
        var redacted = LogPrivacy.Redact($"read 1001.json for {Persona}");
        Assert.Equal($"read {LogPrivacy.CharacterBindingFile} for {LogPrivacy.PersonaIdentity}", redacted);
    }

    [Fact]
    public void FileName_HidesAnIdentifierUsedAsAFileName()
    {
        // No file is ever named by an identifier (NETWORK1.md, safeguard 7); if one were, the log
        // would still not show it.
        Assert.Equal($"{LogPrivacy.PersonaIdentity}.key", LogPrivacy.FileName(Path.Combine("C:", "data", Persona + ".key")));
    }

    [Fact]
    public void ForLog_RedactsAnExceptionThatNamesAnIdentifier_AndItsInnerException()
    {
        var original = new IOException($"could not read the profile {Profile}", new InvalidOperationException($"asset {Asset} missing"));
        var logged = LogPrivacy.ForLog(original);

        Assert.NotSame(original, logged);
        var text = logged.ToString();
        Assert.Contains(LogPrivacy.ProfileIdentifier, text, StringComparison.Ordinal);
        Assert.Contains(LogPrivacy.AssetIdentifier, text, StringComparison.Ordinal);
        Assert.DoesNotContain("a1a1a1a1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("c3c3c3c3", text, StringComparison.Ordinal);
        Assert.Contains(typeof(IOException).FullName!, text, StringComparison.Ordinal);
        Assert.Equal(original.HResult, logged.HResult);

        // The original is untouched: it still names the profile.
        Assert.Contains(Profile, original.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForLog_LeavesAnExceptionWithoutIdentifiersAlone()
    {
        var original = new IOException("could not read the Plate file");
        Assert.Same(original, LogPrivacy.ForLog(original));
        Assert.Null(LogPrivacy.ForLog(null));
    }
}
