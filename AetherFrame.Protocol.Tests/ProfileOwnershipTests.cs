using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// A remote profile is (persona, profile id): a document names a profile of the persona that
/// signed it and of nobody else, so only the owner can publish to a profile or retract it
/// (docs/networking/ProtocolSpecification-v1.md, "Profile identity and ownership").
/// </summary>
public class ProfileOwnershipTests
{
    [Fact]
    public void VerifiedDocument_NamesTheSigningPersonasProfile_NeverAnotherPersonas()
    {
        using var a = TestPersonas.CreateA();
        using var b = TestPersonas.CreateB();

        var published = SignedDocumentCodec.Verify(SignedDocumentCodec.Sign(Samples.Snapshot(), a));
        Assert.Equal(new RemoteProfileKey(a.PublicKey.Id, Samples.Profile), ProtocolAssert.ProfileOf(published));

        // B retracts, and republishes, the same profile id: those are B's own profile of that id,
        // unrelated to A's, so a server keyed by the pair touches nothing of A's.
        var retractedByB = SignedDocumentCodec.Verify(SignedDocumentCodec.Sign(new ProfileRetraction(Samples.Profile, Samples.IssuedAt), b));
        var republishedByB = SignedDocumentCodec.Verify(SignedDocumentCodec.Sign(Samples.Snapshot(), b));
        Assert.Equal(new RemoteProfileKey(b.PublicKey.Id, Samples.Profile), ProtocolAssert.ProfileOf(retractedByB));
        Assert.Equal(ProtocolAssert.ProfileOf(retractedByB), ProtocolAssert.ProfileOf(republishedByB));
        Assert.NotEqual(ProtocolAssert.ProfileOf(published), ProtocolAssert.ProfileOf(retractedByB));
        Assert.NotEqual(ProtocolAssert.ProfileOf(published), ProtocolAssert.ProfileOf(republishedByB));

        // A's own retraction names A's profile.
        var retractedByA = SignedDocumentCodec.Verify(SignedDocumentCodec.Sign(new ProfileRetraction(Samples.Profile, Samples.IssuedAt), a));
        Assert.Equal(ProtocolAssert.ProfileOf(published), ProtocolAssert.ProfileOf(retractedByA));
    }

    [Fact]
    public void RemoteProfileDocument_ExposesTheProfileIdOfEitherType()
    {
        RemoteProfileDocument snapshot = Samples.Snapshot();
        RemoteProfileDocument retraction = new ProfileRetraction(Samples.ProfileB, Samples.IssuedAt);
        Assert.Equal(Samples.Profile, snapshot.ProfileId);
        Assert.Equal(Samples.ProfileB, retraction.ProfileId);
    }

    [Fact]
    public void ProfileId_BelongsToTheProfileDocumentTypesOnly()
    {
        // Decision N5 (docs/networking/DecisionRegister.md): a later document type that is not about
        // a profile derives from RemoteDocument without one, so the base type carries no profile id,
        // and every version 1 document type is a profile document.
        Assert.Null(typeof(RemoteDocument).GetProperty(nameof(RemoteProfileDocument.ProfileId)));
        Assert.True(typeof(RemoteProfileDocument).IsSubclassOf(typeof(RemoteDocument)));
        Assert.True(typeof(ProfileSnapshot).IsSubclassOf(typeof(RemoteProfileDocument)));
        Assert.True(typeof(ProfileRetraction).IsSubclassOf(typeof(RemoteProfileDocument)));
    }

    [Fact]
    public void RemoteProfileKey_HasValueSemantics_AndRefusesEmptyParts()
    {
        using var a = TestPersonas.CreateA();
        using var b = TestPersonas.CreateB();
        var key = new RemoteProfileKey(a.PublicKey.Id, Samples.Profile);

        Assert.Equal(key, new RemoteProfileKey(a.PublicKey.Id, ProfileId.Parse(Samples.Profile.ToString())));
        Assert.Equal(key.GetHashCode(), new RemoteProfileKey(a.PublicKey.Id, Samples.Profile).GetHashCode());
        Assert.True(key == new RemoteProfileKey(a.PublicKey.Id, Samples.Profile));
        Assert.True(key != new RemoteProfileKey(b.PublicKey.Id, Samples.Profile));
        Assert.True(key != new RemoteProfileKey(a.PublicKey.Id, Samples.ProfileB));
        Assert.Equal(a.PublicKey.Id.ToString() + "/" + Samples.Profile.ToString(), key.ToString());
        Assert.False(key.IsEmpty);
        Assert.True(default(RemoteProfileKey).IsEmpty);

        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new RemoteProfileKey(default, Samples.Profile));
        ProtocolAssert.Throws(ProtocolError.InvalidValue, () => new RemoteProfileKey(a.PublicKey.Id, default));
    }
}
