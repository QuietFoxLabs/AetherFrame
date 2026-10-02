using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Protocol.Remote;

/// <summary>
/// A document about one remote profile: a <see cref="ProfileSnapshot"/> or a
/// <see cref="ProfileRetraction"/>. Only these carry a profile id. A later document type that is
/// not about a profile (a persona statement, say) derives from
/// <see cref="RemoteDocument"/> directly and has none (decision N5, docs/networking/DecisionRegister.md).
/// The set of subtypes is closed (the constructor is private protected).
/// </summary>
public abstract class RemoteProfileDocument : RemoteDocument
{
    private protected RemoteProfileDocument()
    {
    }

    /// <summary>
    /// The profile the document is about. On its own it names nothing: a document only ever refers
    /// to a profile of the persona that signed it, so the profile is (persona, profile id), which a
    /// verified document exposes as <see cref="VerifiedDocument.Profile"/>.
    /// </summary>
    public abstract ProfileId ProfileId { get; }
}
