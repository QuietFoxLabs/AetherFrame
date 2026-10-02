using System;
using System.Collections.Generic;
using System.Linq;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Network.Publishing;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// What the preview's share check says (N2-6c's second part): every refusal, left-out reason and
/// failure has a message of its own, in plain text that names no path, id or label.
/// </summary>
public sealed class ShareMessagesTests
{
    [Fact]
    public void EveryCase_HasAMessageOfItsOwn()
    {
        AssertDistinct(Enum.GetValues<PlateSnapshotRefusal>().Select(ShareMessages.For), ShareMessages.For((PlateSnapshotRefusal)250));
        AssertDistinct(Enum.GetValues<LeftOutReason>().Select(ShareMessages.For), ShareMessages.For((LeftOutReason)250));
        AssertDistinct(Enum.GetValues<ShareCheckFailure>().Where(f => f != ShareCheckFailure.None).Select(ShareMessages.For), ShareMessages.For(ShareCheckFailure.None));
    }

    [Fact]
    public void OnlyWhatTheGameFillsIn_IsFlaggedAsFromTheCharacter()
    {
        var flagged = Enum.GetValues<ProfileElementRole>().Where(role => ShareMessages.IsFromTheCharacter(role)).ToHashSet();
        Assert.True(flagged.SetEquals(new[] { ProfileElementRole.BasicName, ProfileElementRole.BasicWorld, ProfileElementRole.BasicJob, ProfileElementRole.BasicFreeCompany }));
        Assert.False(ShareMessages.IsFromTheCharacter(null));
    }

    /// <summary>Each message non-empty, plain ASCII with no control character, and none the fallback or another's.</summary>
    private static void AssertDistinct(IEnumerable<string> messages, string fallback)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.NotEqual(fallback, message);
            Assert.True(seen.Add(message), message);
            Assert.All(message, c => Assert.InRange(c, ' ', '~'));
        }
    }
}
