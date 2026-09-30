using AetherFrame.Protocol.Identity;

namespace AetherFrame.Personas;

/// <summary>
/// What this installation knows about one persona, and everything a listing shows: its local
/// <see cref="Slot"/>, its public key and the identity derived from it, and the private
/// <see cref="Label"/>, and whether the player has acknowledged what a lost key means
/// (<see cref="Acknowledged"/>, decision K4). Nothing here is secret, and nothing here says anything
/// about a character, an account or a Plate. Immutable: a change produces a new record for the same
/// slot.
/// <see cref="ToString"/> gives the slot only, so a record can be logged without naming the persona.
/// </summary>
public sealed class PersonaRecord
{
    internal PersonaRecord(PersonaSlotId slot, PersonaPublicKey publicKey, string label, bool acknowledged = false)
    {
        Slot = slot;
        PublicKey = publicKey;
        Label = label;
        Acknowledged = acknowledged;
    }

    /// <summary>The local handle, unrelated to the persona's identity.</summary>
    public PersonaSlotId Slot { get; }

    /// <summary>The persona's public key, exactly as the protocol puts it in every document the persona signs.</summary>
    public PersonaPublicKey PublicKey { get; }

    /// <summary>The persona's public identity, derived from the key by the protocol and recomputable by anyone holding the key.</summary>
    public PersonaId Id => PublicKey.Id;

    /// <summary>The private label (<see cref="PersonaLabel"/>).</summary>
    public string Label { get; }

    /// <summary>
    /// Whether the player has acknowledged, for this persona, that losing its key means never
    /// updating or unpublishing what it published (decision K4). A created or restored persona
    /// starts without it; nothing may publish for the first time until it is set.
    /// </summary>
    public bool Acknowledged { get; }

    internal PersonaRecord WithLabel(string label) => new(Slot, PublicKey, label, Acknowledged);

    internal PersonaRecord WithAcknowledged() => new(Slot, PublicKey, Label, acknowledged: true);

    /// <summary>The slot, never the identity or the label.</summary>
    public override string ToString() => Slot.ToString();
}
