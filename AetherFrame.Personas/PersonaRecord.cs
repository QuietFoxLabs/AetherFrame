using AetherFrame.Protocol.Identity;

namespace AetherFrame.Personas;

/// <summary>
/// What this installation knows about one persona, and everything a listing shows: its local
/// <see cref="Slot"/>, its public key and the identity derived from it, and the private
/// <see cref="Label"/>. Nothing here is secret, and nothing here says anything about a character,
/// an account or a Plate. Immutable: renaming produces a new record for the same slot.
/// <see cref="ToString"/> gives the slot only, so a record can be logged without naming the persona.
/// </summary>
public sealed class PersonaRecord
{
    internal PersonaRecord(PersonaSlotId slot, PersonaPublicKey publicKey, string label)
    {
        Slot = slot;
        PublicKey = publicKey;
        Label = label;
    }

    /// <summary>The local handle, unrelated to the persona's identity.</summary>
    public PersonaSlotId Slot { get; }

    /// <summary>The persona's public key, exactly as the protocol puts it in every document the persona signs.</summary>
    public PersonaPublicKey PublicKey { get; }

    /// <summary>The persona's public identity, derived from the key by the protocol and recomputable by anyone holding the key.</summary>
    public PersonaId Id => PublicKey.Id;

    /// <summary>The private label (<see cref="PersonaLabel"/>).</summary>
    public string Label { get; }

    internal PersonaRecord WithLabel(string label) => new(Slot, PublicKey, label);

    /// <summary>The slot, never the identity or the label.</summary>
    public override string ToString() => Slot.ToString();
}
