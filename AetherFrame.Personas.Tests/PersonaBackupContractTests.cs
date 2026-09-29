using System;
using System.Reflection;
using AetherFrame.Protocol.Documents;
using Xunit;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// The backup and restore contracts of decision D2, at the model level: an export happens only
/// when asked; the same identity comes back on another installation; a duplicate is detected and
/// nothing is overwritten; an unsupported or malformed container is refused before any secret is
/// used; and no private key crosses the seam as bytes. The codec is a test double that holds no
/// key material in the bytes it produces; no backup format exists yet.
/// </summary>
public class PersonaBackupContractTests
{
    private readonly HandleBackupCodec codec = new();
    private readonly InMemoryPersonaKeyStore homeStore = new();
    private readonly InMemoryPersonaKeyStore awayStore = new();

    private PersonaManager Home() => new(homeStore, codec);

    private PersonaManager Away() => new(awayStore, codec);

    private static PersonaBackupSecret Secret(string text = "correct horse battery staple") => PersonaBackupSecret.FromText(text);

    [Fact]
    public void Export_HappensOnlyWhenAsked()
    {
        var home = Home();
        var main = home.Create("Main");
        home.Select(main.Slot);
        home.Rename(main.Slot, "Main persona");
        home.TryOpenActiveSigner(out var lease);
        lease!.Dispose();
        home.Create("Second");

        Assert.Equal(0, codec.WriteCalls);
        Assert.Equal(0, homeStore.CallsTo("OpenKey"));

        using var secret = Secret();
        var backup = home.ExportBackup(main.Slot, secret);
        Assert.Equal(HandleBackupCodec.Length, backup.Length);
        Assert.Equal(1, codec.WriteCalls);
        Assert.Equal(1, homeStore.CallsTo("OpenKey"));
        Assert.True(home.InspectBackup(backup).IsSupported);
    }

    [Fact]
    public void Export_RefusesAnUnknownSlotOrAnUnavailableKey()
    {
        var home = Home();
        var main = home.Create("Main");
        using var secret = Secret();

        var unknown = Assert.Throws<PersonaException>(() => home.ExportBackup(PersonaSlotId.NewId(), secret));
        Assert.Equal(PersonaError.UnknownPersona, unknown.Error);

        homeStore.Lock(main.Slot);
        var unavailable = Assert.Throws<PersonaException>(() => home.ExportBackup(main.Slot, secret));
        Assert.Equal(PersonaError.KeyUnavailable, unavailable.Error);
        Assert.Equal(0, codec.WriteCalls);

        Assert.Throws<ArgumentNullException>(() => home.ExportBackup(main.Slot, null!));
    }

    [Fact]
    public void Export_RefusesAKeyThatIsNotThePersonas()
    {
        var home = Home();
        var main = home.Create("Main");
        var alt = home.Create("RP alt");
        homeStore.Impersonate[main.Slot] = alt.Slot;
        using var secret = Secret();

        var exception = Assert.Throws<PersonaException>(() => home.ExportBackup(main.Slot, secret));
        Assert.Equal(PersonaError.InvalidKeyMaterial, exception.Error);
        Assert.Equal(0, codec.WriteCalls);
    }

    [Fact]
    public void Restore_OnAnotherInstallation_GivesTheSameIdentityUnderANewSlot()
    {
        var home = Home();
        var main = home.Create("Main");
        using var secret = Secret();
        var backup = home.ExportBackup(main.Slot, secret);

        var away = Away();
        var result = away.RestoreBackup(backup, secret, "Restored main");
        Assert.Equal(PersonaRestoreStatus.Restored, result.Status);
        Assert.Equal(HandleBackupCodec.SupportedVersion, result.FormatVersion);
        var restored = result.Persona!;
        Assert.Equal(main.Id, restored.Id);
        Assert.Equal(main.PublicKey, restored.PublicKey);
        Assert.NotEqual(main.Slot, restored.Slot);
        Assert.Equal("Restored main", restored.Label);
        Assert.Null(away.Active);
        Assert.Single(away.Personas);
        Assert.Equal(1, awayStore.CallsTo("AddKey"));
        Assert.True(awayStore.Holds(restored.Slot));

        // The restored persona signs as the original.
        away.Select(restored.Slot);
        Assert.Equal(PersonaSignerAvailability.Available, away.TryOpenActiveSigner(out var lease));
        using (lease)
        {
            Assert.Equal(main.Id, SignedDocumentCodec.Verify(Documents.SignedRetraction(lease!.Signer)).Persona);
        }

        // The origin is untouched.
        Assert.Single(home.Personas);
        Assert.Same(main, home.Personas[0]);
        Assert.Equal(1, homeStore.Count);
    }

    [Fact]
    public void Restore_OfAPersonaAlreadyHeld_ReportsPresentAndChangesNothing()
    {
        var home = Home();
        var main = home.Create("Main");
        using var secret = Secret();
        var backup = home.ExportBackup(main.Slot, secret);

        var away = Away();
        var first = away.RestoreBackup(backup, secret, "Restored main");
        var restored = first.Persona!;
        away.Select(restored.Slot);
        var heldBefore = awayStore.Held(restored.Slot);
        var callsBefore = awayStore.Calls.Count;

        var second = away.RestoreBackup(backup, secret, "A different label");
        Assert.Equal(PersonaRestoreStatus.AlreadyPresent, second.Status);
        Assert.Same(restored, second.Persona);
        Assert.Equal("Restored main", second.Persona!.Label);
        Assert.Single(away.Personas);
        Assert.Same(restored, away.Personas[0]);
        Assert.Same(restored, away.Active);
        Assert.Same(heldBefore, awayStore.Held(restored.Slot));
        Assert.Equal(1, awayStore.Count);
        Assert.Equal(callsBefore, awayStore.Calls.Count);

        // A second export of the same persona is a different backup of the same identity, and it is
        // present too.
        var again = home.ExportBackup(main.Slot, secret);
        Assert.NotEqual(backup, again);
        Assert.Equal(PersonaRestoreStatus.AlreadyPresent, away.RestoreBackup(again, secret, "Third label").Status);
    }

    [Fact]
    public void Restore_OnTheOriginInstallation_ReportsPresent()
    {
        var home = Home();
        var main = home.Create("Main");
        using var secret = Secret();
        var backup = home.ExportBackup(main.Slot, secret);
        var addsBefore = homeStore.CallsTo("AddKey");

        var result = home.RestoreBackup(backup, secret, "Main again");
        Assert.Equal(PersonaRestoreStatus.AlreadyPresent, result.Status);
        Assert.Same(main, result.Persona);
        Assert.Single(home.Personas);
        Assert.Equal(addsBefore, homeStore.CallsTo("AddKey"));
        Assert.Equal(1, homeStore.Count);
    }

    [Fact]
    public void Restore_RefusesAnUnsupportedVersionBeforeAnySecretIsUsed()
    {
        var home = Home();
        var main = home.Create("Main");
        using var secret = Secret();
        var newer = HandleBackupCodec.WithVersion(home.ExportBackup(main.Slot, secret), 2);

        var away = Away();
        var inspection = away.InspectBackup(newer);
        Assert.Equal(PersonaBackupStatus.UnsupportedVersion, inspection.Status);
        Assert.Equal(2, inspection.FormatVersion);
        Assert.False(inspection.IsSupported);

        var result = away.RestoreBackup(newer, secret, "Newer");
        Assert.Equal(PersonaRestoreStatus.UnsupportedVersion, result.Status);
        Assert.Equal(2, result.FormatVersion);
        Assert.Null(result.Persona);
        Assert.Equal(0, codec.OpenCalls);
        Assert.Empty(away.Personas);
        Assert.Equal(0, awayStore.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(HandleBackupCodec.Length - 1)]
    [InlineData(HandleBackupCodec.Length + 1)]
    [InlineData(4096)]
    public void Restore_RefusesMalformedBytesBeforeAnySecretIsUsed(int length)
    {
        var away = Away();
        using var secret = Secret();
        var garbage = new byte[length];
        Array.Fill(garbage, (byte)0x41);

        Assert.Equal(PersonaBackupStatus.Malformed, away.InspectBackup(garbage).Status);
        var result = away.RestoreBackup(garbage, secret, "Garbage");
        Assert.Equal(PersonaRestoreStatus.Malformed, result.Status);
        Assert.Equal(0, result.FormatVersion);
        Assert.Null(result.Persona);
        Assert.Equal(0, codec.OpenCalls);
        Assert.Empty(away.Personas);
    }

    [Fact]
    public void Restore_WithTheWrongSecretOrDamagedContent_ReportsCannotOpenAndAddsNothing()
    {
        var home = Home();
        var main = home.Create("Main");
        using var secret = Secret();
        var backup = home.ExportBackup(main.Slot, secret);

        var away = Away();
        using var wrong = Secret("incorrect horse");
        var wrongSecret = away.RestoreBackup(backup, wrong, "Main");
        Assert.Equal(PersonaRestoreStatus.CannotOpen, wrongSecret.Status);
        Assert.Null(wrongSecret.Persona);

        var damaged = away.RestoreBackup(HandleBackupCodec.Damaged(backup), secret, "Main");
        Assert.Equal(PersonaRestoreStatus.CannotOpen, damaged.Status);

        Assert.Empty(away.Personas);
        Assert.Equal(0, awayStore.Count);
        Assert.Equal(0, awayStore.CallsTo("AddKey"));
        Assert.Null(away.Active);
    }

    [Fact]
    public void Restore_ChecksTheLabelBeforeTouchingTheBackup()
    {
        var away = Away();
        using var secret = Secret();
        var exception = Assert.Throws<PersonaException>(() => away.RestoreBackup(new byte[HandleBackupCodec.Length], secret, ""));
        Assert.Equal(PersonaError.InvalidLabel, exception.Error);
        Assert.Equal(0, codec.InspectCalls);
        Assert.Equal(0, codec.OpenCalls);
        Assert.Throws<ArgumentNullException>(() => away.RestoreBackup(new byte[HandleBackupCodec.Length], null!, "Main"));
    }

    [Fact]
    public void Restore_WhenTheStoreRefusesTheKey_AddsNothing()
    {
        var home = Home();
        var main = home.Create("Main");
        using var secret = Secret();
        var backup = home.ExportBackup(main.Slot, secret);

        var away = Away();
        awayStore.FailNextAdd = new InvalidOperationException("This store refuses the adoption.");
        Assert.Throws<InvalidOperationException>(() => away.RestoreBackup(backup, secret, "Main"));
        Assert.Empty(away.Personas);
        Assert.Null(away.Active);
        Assert.Equal(0, awayStore.Count);
        Assert.True(Disposal.IsDisposed(codec.HandedOut[^1]));
    }

    [Fact]
    public void TheTestDoublesBytes_CarryNoKeyMaterial()
    {
        // The double is not a format: its bytes hold no public key, no private scalar and no secret.
        var home = Home();
        using var shared = SyntheticKeys.Create();
        homeStore.NextKey = () => SyntheticKeys.Copy(shared);
        var main = home.Create("Main");
        using var secret = Secret();
        var backup = home.ExportBackup(main.Slot, secret);

        var hex = Convert.ToHexStringLower(backup);
        Assert.DoesNotContain(SyntheticKeys.PrivateHex(shared).Substring(0, 16), hex, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticKeys.PublicHex(main.PublicKey).Substring(2, 16), hex, StringComparison.Ordinal);
        Assert.DoesNotContain(main.Id.ToString().Substring(4, 16), hex, StringComparison.Ordinal);
        Assert.DoesNotContain("correct", System.Text.Encoding.ASCII.GetString(backup), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BackupSecret_IsZeroedOnDisposeAndNeverPrinted()
    {
        var secret = PersonaBackupSecret.FromText("correct horse battery staple");
        Assert.Equal("[backup secret]", secret.ToString());
        Assert.Equal("correct horse battery staple", secret.Text.ToString());

        var field = typeof(PersonaBackupSecret).GetField("text", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var storage = (char[])field.GetValue(secret)!;
        secret.Dispose();
        secret.Dispose();
        Assert.All(storage, c => Assert.Equal('\0', c));
        Assert.Throws<ObjectDisposedException>(() => secret.Text.Length);
        Assert.Equal("[backup secret]", secret.ToString());
    }

    [Fact]
    public void BackupSecret_HoldsACopy()
    {
        var source = "correct horse battery staple".ToCharArray();
        using var secret = PersonaBackupSecret.FromText(source);
        Array.Clear(source);
        Assert.Equal("correct horse battery staple", secret.Text.ToString());
    }

    [Fact]
    public void Inspection_IsSupportedOnlyWhenSupported()
    {
        Assert.True(new PersonaBackupInspection(PersonaBackupStatus.Supported, 1).IsSupported);
        Assert.False(new PersonaBackupInspection(PersonaBackupStatus.UnsupportedVersion, 7).IsSupported);
        Assert.False(new PersonaBackupInspection(PersonaBackupStatus.Malformed, 0).IsSupported);
        Assert.Equal(7, new PersonaBackupInspection(PersonaBackupStatus.UnsupportedVersion, 7).FormatVersion);
    }

    [Theory]
    [InlineData(PersonaBackupStatus.Supported, 0)]
    [InlineData(PersonaBackupStatus.Supported, -1)]
    [InlineData(PersonaBackupStatus.UnsupportedVersion, 0)]
    [InlineData(PersonaBackupStatus.UnsupportedVersion, int.MinValue)]
    [InlineData(PersonaBackupStatus.Malformed, 1)]
    [InlineData(PersonaBackupStatus.Malformed, -1)]
    [InlineData((PersonaBackupStatus)0, 3)]
    [InlineData((PersonaBackupStatus)0, 0)]
    [InlineData((PersonaBackupStatus)4, 1)]
    [InlineData((PersonaBackupStatus)(-1), 0)]
    [InlineData((PersonaBackupStatus)int.MaxValue, 1)]
    public void Inspection_RefusesAnUndefinedStatusOrAContradictoryVersion(PersonaBackupStatus status, int version)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PersonaBackupInspection(status, version));
    }
}
