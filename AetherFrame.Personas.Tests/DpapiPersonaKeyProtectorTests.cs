using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AetherFrame.Personas.Storage;
using AetherFrame.Services.Network.Personas;
using Xunit;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// The DPAPI protector (docs/networking/DecisionRegister.md, K2). On Windows it protects under
/// CurrentUser DPAPI and binds every blob to its context; anywhere else it refuses to protect and
/// opens nothing, which is what the probe turns into "personas off". The tests ask the platform
/// only to know which of the two to expect; the plugin itself never decides by the platform's name.
/// </summary>
public class DpapiPersonaKeyProtectorTests : IDisposable
{
    private readonly TemporaryDirectory directory = new();

    private readonly DpapiPersonaKeyProtector protector = new();

    public void Dispose() => directory.Dispose();

    private static byte[] Context(byte fill) => Enumerable.Repeat(fill, 116).ToArray();

    [Fact]
    public void TheId_IsOneAnEnvelopeCarries()
    {
        Assert.Equal("windows.dpapi.currentuser.v1", protector.Id);
        Assert.Equal(DpapiPersonaKeyProtector.ProtectorId, protector.Id);
    }

    [Fact]
    public void Protect_OpensOnlyUnderItsOwnContext()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var secret = RandomNumberGenerator.GetBytes(32);
        var context = Context(0x41);
        var blob = protector.Protect(secret, context);

        Assert.True(DpapiPersonaKeyProtector.CarriesWindowsProvider(blob));
        Assert.True(blob.Length > secret.Length);
        Assert.False(blob.AsSpan().IndexOf(secret) >= 0);
        Assert.NotEqual(blob, protector.Protect(secret, context));
        Assert.Equal(secret, protector.Unprotect(blob, context));

        var otherContext = Context(0x41);
        otherContext[^1] ^= 0x01;
        Assert.Null(protector.Unprotect(blob, otherContext));
        Assert.Null(protector.Unprotect(blob, Context(0x42)));
        Assert.Null(protector.Unprotect(blob, context.AsSpan(0, context.Length - 1)));
        Assert.Null(protector.Unprotect(blob, []));
    }

    [Fact]
    public void Unprotect_IsNullForAnythingButItsOwnBlob()
    {
        var context = Context(0x41);
        Assert.Null(protector.Unprotect([], context));
        Assert.Null(protector.Unprotect(new byte[DpapiPersonaKeyProtector.MaxBlobLength + 1], context));
        Assert.Null(protector.Unprotect(RandomNumberGenerator.GetBytes(200), context));
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var blob = protector.Protect(RandomNumberGenerator.GetBytes(32), context);
        var tampered = (byte[])blob.Clone();
        tampered[blob.Length / 2] ^= 0x01;
        Assert.Null(protector.Unprotect(tampered, context));
        Assert.Null(protector.Unprotect(blob.AsSpan(0, blob.Length - 1), context));

        // DPAPI authenticates neither bytes appended after a blob nor its provider identifier (see the
        // protector's remarks), so neither change is asserted here: with either, the same key opens,
        // never another, and the store checks the key it opens against the envelope's public key.
    }

    [Fact]
    public void Protect_RefusesNothingToProtectOrNoContext()
    {
        Assert.Throws<ArgumentException>(() => protector.Protect([], Context(1)));
        Assert.Throws<ArgumentException>(() => protector.Protect(new byte[32], []));
    }

    [Fact]
    public void Protect_ThrowsWhereThereIsNoDpapi()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Throws<DllNotFoundException>(() => protector.Protect(new byte[32], Context(1)));
    }

    [Theory]
    [InlineData("01000000d08c9ddf0115d1118c7a00c04fc297eb", true)]
    [InlineData("01000000d08c9ddf0115d1118c7a00c04fc297eb0000", true)]
    [InlineData("01000000d08c9ddf0115d1118c7a00c04fc297", false)]
    [InlineData("02000000d08c9ddf0115d1118c7a00c04fc297eb", false)]
    [InlineData("01000000d08c9ddf0115d1118c7a00c04fc297ea", false)]
    [InlineData("df9d8cd0150111d18c7a00c04fc297eb00000000", false)]
    [InlineData("0100000057696e652043727970743332206f6b00", false)]
    [InlineData("", false)]
    public void TheProtectionClaim_RestsOnTheWindowsProvidersIdentifierAlone(string hex, bool claimed)
    {
        Assert.Equal(claimed, DpapiPersonaKeyProtector.CarriesWindowsProvider(Convert.FromHexString(hex)));
    }

    [Fact]
    public void TheStore_KeepsAndOpensAKeyThroughDpapi_AndItsBlobOpensUnderNoOtherHeader()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var storage = new PersonaKeyFileStorage(directory.Path);
        var reports = new System.Collections.Generic.List<string>();
        var store = new ProtectedPersonaKeyStore(storage, protector, reports.Add);
        using var material = store.GenerateKey();
        var slot = PersonaSlotId.NewId();
        store.AddKey(slot, material);
        using (var signer = (IDisposable)store.OpenSigner(slot)!)
        {
            Assert.Equal(material.PublicKey, ((AetherFrame.Protocol.Signing.IPersonaSigner)signer).PublicKey);
        }

        // The same envelope under another slot's name: its header still names the old slot, so the
        // store's own slot check refuses it before DPAPI is called.
        var envelope = File.ReadAllBytes(Path.Combine(directory.Path, slot + PersonaKeyFileStorage.Extension));
        var copied = PersonaSlotId.NewId();
        File.WriteAllBytes(Path.Combine(directory.Path, copied + PersonaKeyFileStorage.Extension), envelope);
        Assert.Null(store.OpenSigner(copied));
        Assert.Contains("the envelope names another slot", reports[^1], StringComparison.Ordinal);

        // A forged header: the slot inside the envelope rewritten to the new slot's (bytes 6 to 21),
        // so every check before DPAPI passes and only DPAPI's entropy, the original header, refuses it.
        var forged = PersonaSlotId.NewId();
        var rewritten = (byte[])envelope.Clone();
        forged.WriteBytes(rewritten.AsSpan(6, 16));
        File.WriteAllBytes(Path.Combine(directory.Path, forged + PersonaKeyFileStorage.Extension), rewritten);
        Assert.Null(store.OpenSigner(forged));
        Assert.Contains("could not open the key on this account", reports[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void TheSecretUnprotectReturns_IsOnThePinnedObjectHeap()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // An array on the pinned object heap reports the oldest generation from the start; an
        // ordinary new array starts in generation 0, where the collector may move it and leave a copy.
        var context = Context(0x41);
        var opened = protector.Unprotect(protector.Protect(RandomNumberGenerator.GetBytes(32), context), context);
        Assert.NotNull(opened);
        Assert.Equal(GC.MaxGeneration, GC.GetGeneration(opened!));
        Assert.Equal(0, GC.GetGeneration(new byte[32]));
    }

    [Fact]
    public void TheStore_RefusesToKeepAKeyWhereThereIsNoDpapi()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new ProtectedPersonaKeyStore(new PersonaKeyFileStorage(directory.Path), protector);
        using var material = store.GenerateKey();
        var failure = Assert.Throws<PersonaException>(() => store.AddKey(PersonaSlotId.NewId(), material));
        Assert.Equal(PersonaError.CustodyFailed, failure.Error);
        Assert.Empty(Directory.Exists(directory.Path) ? Directory.GetFiles(directory.Path) : []);
    }
}
