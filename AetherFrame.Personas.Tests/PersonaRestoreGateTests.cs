using System;
using Xunit;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// A restore opens a backup, and presents the secret to the codec, only after the codec's inspection
/// explicitly and coherently reports a supported container; and the container it opens is exactly
/// the one it inspected, a private copy the caller's buffer cannot reach. Every refusal here happens
/// with <see cref="HandleBackupCodec.OpenCalls"/> at zero, which is how a test sees that no secret
/// reached the codec. The codec is a test double; no backup format exists.
/// </summary>
public class PersonaRestoreGateTests
{
    private readonly HandleBackupCodec codec = new();
    private readonly InMemoryPersonaKeyStore homeStore = new();
    private readonly InMemoryPersonaKeyStore awayStore = new();

    private static PersonaBackupSecret Secret() => PersonaBackupSecret.FromText("correct horse battery staple");

    public static TheoryData<string, PersonaBackupInspection?> IncoherentInspections() => new()
    {
        { "null", null },
        { "undefined status 3", HandleBackupCodec.Forged((PersonaBackupStatus)3, 1) },
        { "undefined status -1", HandleBackupCodec.Forged((PersonaBackupStatus)(-1), 1) },
        { "undefined status max", HandleBackupCodec.Forged((PersonaBackupStatus)int.MaxValue, 1) },
        { "supported, version 0", HandleBackupCodec.Forged(PersonaBackupStatus.Supported, 0) },
        { "supported, negative version", HandleBackupCodec.Forged(PersonaBackupStatus.Supported, -5) },
        { "unsupported, version 0", HandleBackupCodec.Forged(PersonaBackupStatus.UnsupportedVersion, 0) },
        { "malformed, with a version", HandleBackupCodec.Forged(PersonaBackupStatus.Malformed, 1) },
    };

    [Theory]
    [MemberData(nameof(IncoherentInspections))]
    public void Restore_StopsWithoutASecretWhenTheInspectionIsNotCoherent(string name, PersonaBackupInspection? inspection)
    {
        var (backup, _) = Exported();
        var away = new PersonaManager(awayStore, codec);
        codec.InspectOverride = _ => inspection;
        using var secret = Secret();

        var exception = Assert.Throws<PersonaException>(() => away.RestoreBackup(backup, secret, "Main"));
        Assert.Equal(PersonaError.InvalidBackupInspection, exception.Error);
        Assert.Equal(0, codec.OpenCalls);
        Assert.Empty(away.Personas);
        Assert.Equal(0, awayStore.Count);
        Assert.False(string.IsNullOrEmpty(name));

        // Inspecting alone refuses it the same way rather than passing it on.
        Assert.Equal(PersonaError.InvalidBackupInspection, Assert.Throws<PersonaException>(() => away.InspectBackup(backup)).Error);
    }

    [Fact]
    public void Restore_ProceedsOnlyOnAnExplicitSupportedResult()
    {
        // Every coherent status other than Supported stops the restore with its own outcome.
        var (backup, main) = Exported();
        var away = new PersonaManager(awayStore, codec);
        using var secret = Secret();

        codec.InspectOverride = _ => new PersonaBackupInspection(PersonaBackupStatus.UnsupportedVersion, 9);
        var unsupported = away.RestoreBackup(backup, secret, "Main");
        Assert.Equal(PersonaRestoreStatus.UnsupportedVersion, unsupported.Status);
        Assert.Equal(9, unsupported.FormatVersion);

        codec.InspectOverride = _ => new PersonaBackupInspection(PersonaBackupStatus.Malformed, 0);
        var malformed = away.RestoreBackup(backup, secret, "Main");
        Assert.Equal(PersonaRestoreStatus.Malformed, malformed.Status);
        Assert.Equal(0, malformed.FormatVersion);

        Assert.Equal(0, codec.OpenCalls);
        Assert.Empty(away.Personas);

        codec.InspectOverride = null;
        var restored = away.RestoreBackup(backup, secret, "Main");
        Assert.Equal(PersonaRestoreStatus.Restored, restored.Status);
        Assert.Equal(main.Id, restored.Persona!.Id);
        Assert.Equal(1, codec.OpenCalls);
    }

    [Fact]
    public void Restore_OpensExactlyTheBytesItInspected_WhenTheCallersBufferChangesMeanwhile()
    {
        // The caller's buffer holds main's backup when it is inspected, and is overwritten with the
        // alt's backup before the codec would open it. The persona restored is main's: the codec
        // opened the private copy it inspected, never the caller's changed buffer.
        var home = new PersonaManager(homeStore, codec);
        var main = home.Create("Main");
        var alt = home.Create("RP alt");
        using var secret = Secret();
        var mainBackup = home.ExportBackup(main.Slot, secret);
        var altBackup = home.ExportBackup(alt.Slot, secret);

        var buffer = (byte[])mainBackup.Clone();
        codec.DuringInspect = () => altBackup.CopyTo(buffer, 0);

        var away = new PersonaManager(awayStore, codec);
        var result = away.RestoreBackup(buffer, secret, "Restored");

        Assert.Equal(altBackup, buffer);
        Assert.Equal(PersonaRestoreStatus.Restored, result.Status);
        Assert.Equal(main.Id, result.Persona!.Id);
        Assert.Equal(mainBackup, Assert.Single(codec.Inspected));
        Assert.Equal(mainBackup, Assert.Single(codec.Opened));
    }

    [Fact]
    public void Restore_CannotBeTurnedIntoAnUnsupportedOpenByChangingTheBufferAfterInspection()
    {
        // The buffer is changed to a malformed container after a supported inspection. The codec
        // still opens what it inspected; nothing unsupported is ever opened under the secret.
        var (backup, main) = Exported();
        var buffer = (byte[])backup.Clone();
        codec.DuringInspect = () => Array.Fill(buffer, (byte)0x41);

        var away = new PersonaManager(awayStore, codec);
        using var secret = Secret();
        var result = away.RestoreBackup(buffer, secret, "Restored");
        Assert.Equal(PersonaRestoreStatus.Restored, result.Status);
        Assert.Equal(main.Id, result.Persona!.Id);
        Assert.Equal(backup, Assert.Single(codec.Opened));
    }

    [Fact]
    public void Restore_ChecksTheLabelBeforeTheCodecSeesAnything()
    {
        var (backup, _) = Exported();
        var away = new PersonaManager(awayStore, codec);
        var inspectsBefore = codec.InspectCalls;
        using var secret = Secret();
        Assert.Equal(PersonaError.InvalidLabel, Assert.Throws<PersonaException>(() => away.RestoreBackup(backup, secret, "\u0007")).Error);
        Assert.Equal(inspectsBefore, codec.InspectCalls);
        Assert.Equal(0, codec.OpenCalls);
    }

    private (byte[] Backup, PersonaRecord Persona) Exported()
    {
        var home = new PersonaManager(homeStore, codec);
        var main = home.Create("Main");
        using var secret = Secret();
        return (home.ExportBackup(main.Slot, secret), main);
    }
}
