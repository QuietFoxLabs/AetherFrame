namespace AetherFrame.Personas;

/// <summary>Whether <see cref="PersonaManager.TryOpenActiveSigner"/> or <see cref="PersonaManager.TryOpenSigner"/> produced a lease.</summary>
public enum PersonaSignerAvailability
{
    /// <summary>A lease was opened for the active persona.</summary>
    Available,

    /// <summary>No persona is selected. The caller asks the player to select one; nothing is selected on the player's behalf.</summary>
    NoActivePersona,

    /// <summary>A persona is selected, but its key store could not open the key now.</summary>
    KeyUnavailable,

    /// <summary>
    /// The active persona is not the one the operation showed the player (decision L10): another is
    /// selected, or none is. Nothing was opened; the operation stops, and the player retries.
    /// </summary>
    ActivePersonaChanged,
}
