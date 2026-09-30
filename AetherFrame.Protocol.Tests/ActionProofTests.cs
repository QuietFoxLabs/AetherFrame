using System;
using System.Linq;
using AetherFrame.Protocol.Requests;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// Action requests (docs/networking/ProtocolSpecification-v1.md, section 14.5; decision C9): a
/// proof authorizes exactly one action with exactly one body at one deployment. The kind is signed,
/// so no action's proof verifies as another action or as a document submission, and a submission's
/// proof never verifies as an action.
/// </summary>
public class ActionProofTests
{
    private static readonly DeploymentName Deployment = DeploymentName.Parse(ProofSamples.DeploymentText);
    private static readonly RequestChallenge Challenge = RequestChallenge.Parse(ProofSamples.ChallengeText);
    private static readonly byte[] Body = "{\"name\":\"Jane Doe\",\"world\":\"Gilgamesh\"}"u8.ToArray();

    private static RequestProofKind[] Actions() =>
        Enum.GetValues<RequestProofKind>().Where(RequestProofCodec.IsAction).ToArray();

    [Fact]
    public void EveryKindButASubmission_IsAnAction()
    {
        Assert.Equal(
            [RequestProofKind.LodestoneCode, RequestProofKind.LodestoneCheck, RequestProofKind.LodestoneReread, RequestProofKind.OptOut, RequestProofKind.Lookup, RequestProofKind.Image, RequestProofKind.Report],
            Actions());
        Assert.False(RequestProofCodec.IsAction(RequestProofKind.DocumentSubmission));
        Assert.False(RequestProofCodec.IsAction((RequestProofKind)0));
        Assert.False(RequestProofCodec.IsAction((RequestProofKind)9));
    }

    [Fact]
    public void AnActionProof_AuthorizesExactlyItsActionAndBodyAtItsDeployment()
    {
        using var a = TestPersonas.CreateA();
        foreach (var kind in Actions())
        {
            var proof = RequestProofCodec.SignAction(kind, Body, Deployment, Challenge, a);
            Assert.Equal(ProtocolLimits.RequestProofOverheadBytes + Deployment.Bytes.Length, proof.Length);

            var verified = RequestProofCodec.VerifyAction(proof, Body, Deployment, kind);
            Assert.Equal(kind, verified.Kind);
            Assert.Equal(a.PublicKey, verified.PublicKey);
            Assert.Equal(Challenge, verified.Challenge);
            Assert.Equal(Body, verified.Body.ToArray());
        }
    }

    [Fact]
    public void NoActionsProof_VerifiesAsAnotherAction()
    {
        using var a = TestPersonas.CreateA();
        foreach (var kind in Actions())
        {
            var proof = RequestProofCodec.SignAction(kind, Body, Deployment, Challenge, a);
            foreach (var other in Actions().Where(o => o != kind))
            {
                ProtocolAssert.Throws(ProtocolError.ProofMismatch, () => RequestProofCodec.VerifyAction(proof, Body, Deployment, other));
            }

            // Rewriting the kind byte breaks the signature, since the kind is signed.
            foreach (var other in Actions().Where(o => o != kind).Append(RequestProofKind.DocumentSubmission))
            {
                var rewritten = (byte[])proof.Clone();
                rewritten[6] = (byte)other;
                ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => RequestProofCodec.Verify(rewritten));
            }
        }
    }

    [Fact]
    public void SubmissionsAndActions_NeverCross()
    {
        using var a = TestPersonas.CreateA();
        var document = Samples.SignedSnapshot(a);
        var submission = RequestProofCodec.Sign(document, Deployment, Challenge, a);
        foreach (var kind in Actions())
        {
            ProtocolAssert.Throws(ProtocolError.ProofMismatch, () => RequestProofCodec.VerifyAction(submission, document, Deployment, kind));

            var action = RequestProofCodec.SignAction(kind, document.AsSpan(0, Math.Min(document.Length, ProtocolLimits.MaxActionBodyBytes)), Deployment, Challenge, a);
            ProtocolAssert.Throws(ProtocolError.ProofMismatch, () => RequestProofCodec.VerifySubmission(action, document, Deployment));
        }
    }

    [Fact]
    public void AnActionProof_BindsItsBodyAndItsDeployment()
    {
        using var a = TestPersonas.CreateA();
        var proof = RequestProofCodec.SignAction(RequestProofKind.Lookup, Body, Deployment, Challenge, a);

        var changed = (byte[])Body.Clone();
        changed[^2] ^= 1;
        ProtocolAssert.Throws(ProtocolError.ProofMismatch, () => RequestProofCodec.VerifyAction(proof, changed, Deployment, RequestProofKind.Lookup));
        ProtocolAssert.Throws(ProtocolError.ProofMismatch, () => RequestProofCodec.VerifyAction(proof, Body.AsSpan(1), Deployment, RequestProofKind.Lookup));
        ProtocolAssert.Throws(ProtocolError.ProofMismatch, () => RequestProofCodec.VerifyAction(proof, Body, DeploymentName.Parse("other.example.com"), RequestProofKind.Lookup));
    }

    [Fact]
    public void AnEmptyBody_IsAllowed_AndAnOversizedOneIsRefusedBothWays()
    {
        using var a = TestPersonas.CreateA();
        var empty = RequestProofCodec.SignAction(RequestProofKind.OptOut, [], Deployment, Challenge, a);
        Assert.Equal(0, RequestProofCodec.VerifyAction(empty, [], Deployment, RequestProofKind.OptOut).Body.Length);

        var largest = new byte[ProtocolLimits.MaxActionBodyBytes];
        var proof = RequestProofCodec.SignAction(RequestProofKind.Report, largest, Deployment, Challenge, a);
        RequestProofCodec.VerifyAction(proof, largest, Deployment, RequestProofKind.Report);

        var over = new byte[ProtocolLimits.MaxActionBodyBytes + 1];
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => RequestProofCodec.SignAction(RequestProofKind.Report, over, Deployment, Challenge, a));
        ProtocolAssert.Throws(ProtocolError.LimitExceeded, () => RequestProofCodec.VerifyAction(proof, over, Deployment, RequestProofKind.Report));
    }

    [Fact]
    public void OnlyActionsAreSignedOrCheckedAsActions()
    {
        using var a = TestPersonas.CreateA();
        Assert.Throws<ArgumentOutOfRangeException>(() => RequestProofCodec.SignAction(RequestProofKind.DocumentSubmission, Body, Deployment, Challenge, a));
        Assert.Throws<ArgumentOutOfRangeException>(() => RequestProofCodec.SignAction((RequestProofKind)9, Body, Deployment, Challenge, a));
        var proof = RequestProofCodec.SignAction(RequestProofKind.Lookup, Body, Deployment, Challenge, a);
        Assert.Throws<ArgumentOutOfRangeException>(() => RequestProofCodec.VerifyAction(proof, Body, Deployment, RequestProofKind.DocumentSubmission));
    }

    [Fact]
    public void TheBodysBytesArePrivate_AndTheDigestIsItsSha256()
    {
        using var a = TestPersonas.CreateA();
        var body = (byte[])Body.Clone();
        var proof = RequestProofCodec.SignAction(RequestProofKind.Lookup, body, Deployment, Challenge, a);
        var verified = RequestProofCodec.VerifyAction(proof, body, Deployment, RequestProofKind.Lookup);
        body[0] = (byte)'X';
        Assert.Equal((byte)'{', verified.Body[0]);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(Body), verified.Proof.SubjectDigest.ToArray());
    }
}
