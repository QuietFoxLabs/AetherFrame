namespace AetherFrame.Personas;

/// <summary>Whether <see cref="PersonaManager.TryOpenActiveSigner"/> produced a lease.</summary>
public enum PersonaSignerAvailability
{
    /// <summary>A lease was opened for the active persona.</summary>
    Available,

    /// <summary>No persona is selected. The caller asks the player to select one; nothing is selected on the player's behalf.</summary>
    NoActivePersona,

    /// <summary>A persona is selected, but its key store could not open the key now.</summary>
    KeyUnavailable,
}
