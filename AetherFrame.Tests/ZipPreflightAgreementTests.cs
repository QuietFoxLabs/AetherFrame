using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using AetherFrame.Services.Packages;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// ZipPreflight must read the end-of-central-directory record that System.IO.Compression's
/// ZipArchive reads. ZipArchive seeks to end-18, scans back up to 65539 bytes and takes the LAST
/// signature it finds, requiring only that the comment FITS in the file (not that it reaches the
/// end). A preflight that skipped that record for an earlier one whose comment does reach the end
/// would check its limits against a record ZipArchive never uses: a tail of
///   [record A: small entry count, comment spanning the rest][junk][record B: huge count][junk]
/// would pass as a small count while ZipArchive followed B into the full directory.
/// </summary>
public class ZipPreflightAgreementTests
{
    private const uint EndRecordSignature = 0x06054b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const int EndRecordSize = 22;

    private static byte[] BuildArchive(int entryCount)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < entryCount; i++)
            {
                var entry = archive.CreateEntry("e" + i, CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write(new byte[i % 7]);
            }
        }

        return ms.ToArray();
    }

    private static int FindLastEndRecord(byte[] bytes)
    {
        for (var i = bytes.Length - EndRecordSize; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == EndRecordSignature)
            {
                return i;
            }
        }

        throw new InvalidOperationException("no end record");
    }

    private static byte[] EndRecord(ushort entries, uint directorySize, uint directoryOffset, ushort commentLength)
    {
        var record = new byte[EndRecordSize];
        BinaryPrimitives.WriteUInt32LittleEndian(record, EndRecordSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(8), entries);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(10), entries);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), directorySize);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), directoryOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), commentLength);
        return record;
    }

    /// <summary>
    /// The two-record tail: the honest archive, then a decoy record A claiming 3 entries whose
    /// comment spans everything after it to the end of the file, 2 junk bytes, a faithful copy of
    /// the archive's own record B (comment 0), and 2 junk bytes. ZipArchive reads B.
    /// </summary>
    internal static byte[] WithTwoEndRecords(byte[] real)
    {
        var realEnd = FindLastEndRecord(real);
        var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(real.AsSpan(realEnd + 16));

        var recordB = new byte[EndRecordSize];
        Array.Copy(real, realEnd, recordB, 0, EndRecordSize);
        BinaryPrimitives.WriteUInt16LittleEndian(recordB.AsSpan(20), 0);

        const int trailerAfterA = 2 + EndRecordSize + 2;
        var recordA = EndRecord(3, 200, directoryOffset, trailerAfterA);

        return [.. real, .. recordA, 0x11, 0x22, .. recordB, 0x33, 0x44];
    }

    [Fact]
    public void Preflight_BindsToTheRecordZipArchiveSelects()
    {
        // A modest but real directory that ZipArchive parses if it follows record B.
        var real = BuildArchive(40);
        var crafted = WithTwoEndRecords(real);

        int zipEntryCount;
        using (var archive = new ZipArchive(new MemoryStream(crafted), ZipArchiveMode.Read))
        {
            zipEntryCount = archive.Entries.Count;
        }

        Assert.Equal(40, zipEntryCount);

        // The preflight lands on B too, whose comment doesn't reach the end of the file: refused.
        var declared = ZipPreflight.Read(new MemoryStream(crafted), out var error);

        Assert.Null(declared);
        Assert.Contains("trailing data", error);

        // The honest file is accepted with exactly the count ZipArchive parses.
        Assert.Equal(40, ZipPreflight.Read(new MemoryStream(real), out _)!.Value.EntryCount);
    }

    [Fact]
    public void SingleRecord_Zip64EntryCount_IsRefused()
    {
        var record = EndRecord(ushort.MaxValue, 100, 0, 0);

        Assert.Null(ZipPreflight.Read(new MemoryStream([.. new byte[100], .. record]), out var error));
        Assert.Contains("ZIP64", error);
    }

    [Fact]
    public void SingleRecord_Zip64Locator_IsRefused()
    {
        var locator = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(locator, Zip64LocatorSignature);
        var record = EndRecord(3, 100, 0, 0);

        Assert.Null(ZipPreflight.Read(new MemoryStream([.. new byte[100], .. locator, .. record]), out var error));
        Assert.Contains("ZIP64", error);
    }

    [Fact]
    public void SingleRecord_CountPastTheLimit_IsRefusedByTheReader()
    {
        using var directory = new TempDirectory();
        var count = PackagePolicy.MaxEntryCount + 1;
        var path = Path.Combine(directory.Path, "many.aetherframe");
        File.WriteAllBytes(path, BuildArchive(count));

        using (var file = File.OpenRead(path))
        {
            Assert.Equal(count, ZipPreflight.Read(file, out _)!.Value.EntryCount);
        }

        using var staged = PackageReader.Open(path, Path.Combine(directory.Path, "staging"));
        Assert.Equal(PackageCompatibility.Invalid, staged.Compatibility);
        var error = Assert.Single(staged.Diagnostics.Errors);
        Assert.Equal(PackageErrorCode.PackageTooLarge, error.Code);
        Assert.Contains($"{count} entries", error.Detail);
    }

    [Fact]
    public void Fuzz_PreflightAndZipArchiveAgree()
    {
        // Deterministic: small honest archives with their tails mangled every way the two parsers
        // could disagree on. Whenever the preflight accepts, ZipArchive either refuses the file or
        // parses exactly the entry count the preflight reported — never more.
        var random = new Random(20260926);
        for (var i = 0; i < 400; i++)
        {
            var real = BuildArchive(random.Next(0, 6));
            var end = FindLastEndRecord(real);
            byte[] crafted;
            switch (random.Next(6))
            {
                case 0:
                    crafted = real;
                    break;
                case 1:
                    // A comment of the declared length (which may itself contain a signature).
                    var comment = new byte[random.Next(0, 80)];
                    random.NextBytes(comment);
                    crafted = [.. real, .. comment];
                    BinaryPrimitives.WriteUInt16LittleEndian(crafted.AsSpan(end + 20), (ushort)comment.Length);
                    break;
                case 2:
                    // Junk after the record, not declared as a comment.
                    var junk = new byte[random.Next(1, 40)];
                    random.NextBytes(junk);
                    crafted = [.. real, .. junk];
                    break;
                case 3:
                    crafted = WithTwoEndRecords(real);
                    break;
                case 4:
                    // A comment declared longer than what follows.
                    var declared = (ushort)random.Next(1, 60);
                    crafted = [.. real, .. new byte[random.Next(0, declared)]];
                    BinaryPrimitives.WriteUInt16LittleEndian(crafted.AsSpan(end + 20), declared);
                    break;
                default:
                    // Random byte flips in the record itself.
                    crafted = (byte[])real.Clone();
                    for (var flips = random.Next(1, 4); flips > 0; flips--)
                    {
                        crafted[end + random.Next(EndRecordSize)] ^= (byte)random.Next(1, 256);
                    }

                    break;
            }

            var preflight = ZipPreflight.Read(new MemoryStream(crafted), out _);
            if (preflight is not { } accepted)
            {
                continue;
            }

            try
            {
                using var archive = new ZipArchive(new MemoryStream(crafted), ZipArchiveMode.Read);
                Assert.True(accepted.EntryCount == archive.Entries.Count,
                    $"variant {i}: preflight accepted {accepted.EntryCount} entries but ZipArchive parsed {archive.Entries.Count}");
            }
            catch (InvalidDataException)
            {
                // Refused by ZipArchive: never more work than the preflight allowed.
            }
        }
    }

    [Fact]
    public async Task TwoEndRecords_AreRefusedBeforeTheDirectoryIsParsed()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);
        var honestPath = fixture.Export(packages, plateId);
        var path = Path.Combine(fixture.ExportDirectory, "two-records.aetherframe");
        File.WriteAllBytes(path, WithTwoEndRecords(File.ReadAllBytes(honestPath)));

        // ZipArchive itself would read the whole directory through the second record...
        using (var archive = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read))
        {
            Assert.Equal(PackageFiles.Read(honestPath).Count, archive.Entries.Count);
        }

        // ...but the package never gets that far.
        var before = fixture.SnapshotInstallation();
        var staged = packages.Inspect(path);
        try
        {
            Assert.Equal(PackageCompatibility.Invalid, staged.Compatibility);
            var error = Assert.Single(staged.Diagnostics.Errors);
            Assert.Equal(PackageErrorCode.InvalidArchive, error.Code);
            Assert.Contains("trailing data", error.Detail);
            Assert.Null(staged.Manifest);
            Assert.Empty(Directory.GetFileSystemEntries(staged.StagingDirectory));
        }
        finally
        {
            staged.Dispose();
        }

        Assert.Equal(before, fixture.SnapshotInstallation());
        Assert.True(fixture.StagingIsEmpty);
        Assert.Single(library.GetOrderedPlates());
    }
}
