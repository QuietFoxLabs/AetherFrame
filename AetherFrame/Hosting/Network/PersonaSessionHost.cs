using System.IO;
using AetherFrame.Personas.Storage;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Lifecycle;
using AetherFrame.Services.Network.Personas;

namespace AetherFrame.Hosting.Network;

/// <summary>
/// Builds the plugin's persona session over the real files, protector, log and unload tracking
/// (docs/networking/DecisionRegister.md, K2, K3 and P3). Compiled only in the networking preview
/// flavour.
/// <list type="bullet">
/// <item><c>Network\Personas\</c> under the plugin's configuration directory holds the registry
/// and the lock; the key files have a directory of their own inside it, <c>keys\</c>, so a
/// listing of key files never meets the registry or the lock.</item>
/// <item>The capability probe gets a scratch root in the system's temporary folder, never the key
/// directory, and only the probe's public entry point is called.</item>
/// <item>The key store's report lines name a slot and a reason, never an identity or a path, and
/// go through the session's log, which writes nothing once the session is closed.</item>
/// </list>
/// </summary>
internal static class PersonaSessionHost
{
    /// <summary>The persona files' directory, relative to the plugin's configuration directory.</summary>
    internal static readonly string PersonasFolder = Path.Combine("Network", "Personas");

    /// <summary>The key files' directory inside it.</summary>
    internal const string KeysFolder = "keys";

    /// <summary>The persona files' directory under <paramref name="configDirectory"/>.</summary>
    internal static string PersonasDirectory(string configDirectory) => Path.Combine(configDirectory, PersonasFolder);

    /// <summary>The key files' directory (<c>keys\</c> inside the persona files' directory).</summary>
    internal static string KeysDirectory(string configDirectory) => Path.Combine(PersonasDirectory(configDirectory), KeysFolder);

    internal static PersonaSession Create(string configDirectory, IAetherFrameLog log, OwnedOperations operations)
    {
        var personas = PersonasDirectory(configDirectory);
        var keys = KeysDirectory(configDirectory);
        var protector = new DpapiPersonaKeyProtector();
        return new PersonaSession(new PersonaSessionSeams
        {
            Probe = () => PersonaCapabilityProbe.Run(protector, Path.GetTempPath()),
            AcquireLock = () => (PersonaInstanceLock.TryAcquire(personas, out var held), held),
            OpenKeyStore = report => new ProtectedPersonaKeyStore(new PersonaKeyFileStorage(keys), protector, report),
            OpenRegistry = () => new PersonaRegistryFileStorage(personas),
            BeginOperation = () => operations.TryBegin(out var lease) ? lease : null,
            Stopping = operations.Stopping,
            Log = log.Information,
        });
    }
}
