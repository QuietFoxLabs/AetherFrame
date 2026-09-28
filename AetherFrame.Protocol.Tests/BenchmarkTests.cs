using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Remote;
using AetherFrame.Protocol.Signing;
using Xunit;
using static AetherFrame.Protocol.Tests.DocumentMutations;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// Representative timings and allocations, written to the file named by
/// AETHERFRAME_PROTOCOL_BENCHMARK (docs/networking/NETWORK0_HANDOFF.md records a run). Off by
/// default so the suite stays fast; when off, this test does nothing.
/// </summary>
public class BenchmarkTests
{
    [Fact]
    public void Measure_WhenAskedTo()
    {
        var output = Environment.GetEnvironmentVariable("AETHERFRAME_PROTOCOL_BENCHMARK");
        if (string.IsNullOrEmpty(output))
        {
            return;
        }

        using var signer = TestPersonas.CreateA();
        var snapshot = Samples.Snapshot();
        var document = SignedDocumentCodec.Sign(snapshot, signer);
        var maximal = PayloadBuilder.MaximalSnapshot();
        var maximalDocument = SignedDocumentCodec.Sign(maximal, signer);
        var input = SigningInput.Create(DocumentType.ProfileSnapshot, signer.PublicKey, snapshot.EncodePayload());
        var signature = signer.Sign(input);
        var oversized = WithPayloadLength(document, 0xFFFFFFFF);
        var flipped = Flip(document, Layout.Payload + 3);
        var badKey = Flip(document, Layout.Key + 64);
        var random = new byte[300];
        new Random(1).NextBytes(random);
        var unsorted = PayloadBuilder.Signed(DocumentType.ProfileSnapshot, signer, SnapshotPayloadCases.Build("images unsorted"));

        var report = new StringBuilder();
        report.AppendLine($"machine: {Environment.MachineName} {Environment.OSVersion} {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} .NET {Environment.Version} cores {Environment.ProcessorCount}");
        report.AppendLine($"normal document {document.Length} bytes; maximal document {maximalDocument.Length} bytes");
        Measure(report, "encode payload (normal)", 20_000, () => snapshot.EncodePayload());
        Measure(report, "encode payload (maximal)", 200, () => maximal.EncodePayload());
        Measure(report, "build signing input (normal)", 20_000, () => SigningInput.Create(DocumentType.ProfileSnapshot, signer.PublicKey, input.Bytes.Slice(input.Bytes.Length - 100)));
        Measure(report, "sign (normal)", 2_000, () => signer.Sign(input));
        Measure(report, "verify signature only (normal)", 2_000, () => SignatureVerifier.Verify(input, signature));
        Measure(report, "verify + decode document (normal)", 2_000, () => SignedDocumentCodec.Verify(document));
        Measure(report, "verify + decode document (maximal, 1 MiB)", 200, () => SignedDocumentCodec.Verify(maximalDocument));
        Measure(report, "reject oversized declared length", 20_000, () => Reject(oversized));
        Measure(report, "reject random bytes", 20_000, () => Reject(random));
        Measure(report, "reject flipped payload bit (signature mismatch)", 2_000, () => Reject(flipped));
        Measure(report, "reject off-curve key", 20_000, () => Reject(badKey));
        Measure(report, "reject signed non-canonical payload", 2_000, () => Reject(unsorted));
        Measure(report, "parse public key (on-curve check)", 20_000, () => Identity.PersonaPublicKey.FromBytes(signer.PublicKey.Bytes));
        Measure(report, "derive persona id", 20_000, () => signer.PublicKey.Id.ToString());

        File.WriteAllText(output, report.ToString());
    }

    private static void Reject(byte[] document)
    {
        try
        {
            SignedDocumentCodec.Verify(document);
            throw new InvalidOperationException("accepted");
        }
        catch (ProtocolException)
        {
        }
    }

    private static void Measure(StringBuilder report, string name, int iterations, Action action)
    {
        for (var warmup = 0; warmup < Math.Max(10, iterations / 10); warmup++)
        {
            action();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        for (var index = 0; index < iterations; index++)
        {
            action();
        }

        stopwatch.Stop();
        var allocated = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / iterations;
        var micros = stopwatch.Elapsed.TotalMilliseconds * 1000.0 / iterations;
        report.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{name}: {micros:F1} us/op, {allocated} B/op ({iterations} iterations)"));
    }
}
