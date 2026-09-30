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

        // DPAPI ignores bytes after the blob it made (see the protector's remarks), so an extended
        // blob is not asserted either way: the envelope's length prefix is what fixes a blob's bytes.
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

        Assert.ThrowsAny<Exception>(() => protector.Protect(new byte[32], Context(1)));
    }

    [Theory]
    [InlineData("01000000d08c9ddf0115d1118c7a00c04fc297eb", true)]
    [InlineData("01000000d08c9ddf0115d1118c7a00c04fc297eb0000", true)]
    [InlineData("01000000d08c9ddf0115d1118c7a00c04fc297", false)]
    [InlineData("02000000d08c9ddf0115d1118c7a00c04fc297eb", false)]
    [InlineData("01000000d08c9ddf0115d1118c7a00c04fc297ea", false)]
    [InlineData("df9d8cd0150111d18c7a00c04fc297eb00000000", false)]
    [InlineData("", false)]
    public void TheProtectionClaim_RestsOnTheWindowsProvidersIdentifierAlone(string hex, bool claimed)
    {
        Assert.Equal(claimed, DpapiPersonaKeyProtector.CarriesWindowsProvider(Convert.FromHexString(hex)));
    }

    [Fact]
    public void TheStore_KeepsAndOpensAKeyThroughDpapi_AndABlobMovedToAnotherSlotDoesNotOpen()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var storage = new PersonaKeyFileStorage(directory.Path);
        var store = new ProtectedPersonaKeyStore(storage, protector);
        using var material = store.GenerateKey();
        var slot = PersonaSlotId.NewId();
        store.AddKey(slot, material);
        using (var signer = (IDisposable)store.OpenSigner(slot)!)
        {
            Assert.Equal(material.PublicKey, ((AetherFrame.Protocol.Signing.IPersonaSigner)signer).PublicKey);
        }

        // The same envelope under another slot's name: the header no longer matches the slot, so
        // the store refuses it before or at DPAPI, and the key stays unavailable there.
        var other = PersonaSlotId.NewId();
        File.Copy(Path.Combine(directory.Path, slot + PersonaKeyFileStorage.Extension), Path.Combine(directory.Path, other + PersonaKeyFileStorage.Extension));
        Assert.Null(store.OpenSigner(other));
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
