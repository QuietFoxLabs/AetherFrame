using System;
using System.Security.Cryptography;
using AetherFrame.Protocol.Encoding;

namespace AetherFrame.Protocol.Requests;

/// <summary>
/// The name of one deployment of the server: the DNS hostname a client connects to over HTTPS, in
/// its one canonical form (docs/networking/ProtocolSpecification-v1.md, section 14.1). A request
/// proof binds it, so a proof made for one deployment is refused by every other (decision D7). A
/// client takes the name from its own configuration, never from anything a server sends. Immutable.
/// </summary>
public sealed class DeploymentName : IEquatable<DeploymentName>
{
    private readonly byte[] bytes;

    private DeploymentName(byte[] bytes)
    {
        this.bytes = bytes;
        Value = System.Text.Encoding.ASCII.GetString(bytes);
    }

    /// <summary>The name as text: lowercase ASCII letters, digits, hyphens and dots.</summary>
    public string Value { get; }

    /// <summary>The name's ASCII bytes, as a request proof carries them.</summary>
    public ReadOnlySpan<byte> Bytes => bytes;

    /// <summary>
    /// True for a name no real deployment can have, which only tests may use: a single label,
    /// <c>localhost</c> or a name under it, a name under the reserved top-level domains <c>test</c>,
    /// <c>example</c> and <c>invalid</c> (RFC 2606 and RFC 6761), or <c>example.com</c>,
    /// <c>example.net</c>, <c>example.org</c> or a name under them; and a name under the special-use
    /// or infrastructure domains <c>local</c> (RFC 6762), <c>alt</c> (RFC 9476), <c>onion</c>
    /// (RFC 7686), <c>internal</c> and <c>arpa</c>, which are not ordinary public DNS names. A server
    /// that accepts documents signed by real keys refuses to start with such a name (section 13,
    /// rule 10).
    /// </summary>
    public bool IsReservedForTesting
    {
        get
        {
            var labels = Value.Split('.');
            var last = labels[^1];
            if (labels.Length == 1 || last is "localhost" or "test" or "example" or "invalid" or "local" or "alt" or "onion" or "internal" or "arpa")
            {
                return true;
            }

            var secondLevel = labels[^2] + "." + last;
            return secondLevel is "example.com" or "example.net" or "example.org";
        }
    }

    /// <summary>Parses a deployment name, refusing anything but the canonical form.</summary>
    /// <exception cref="ProtocolException">
    /// <see cref="ProtocolError.InvalidLength"/> for an empty name, <see cref="ProtocolError.LimitExceeded"/>
    /// for one over <see cref="ProtocolLimits.MaxDeploymentNameBytes"/>, or <see cref="ProtocolError.InvalidValue"/>.
    /// </exception>
    public static DeploymentName Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        CheckLength(text.Length);
        var copy = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] > 0x7f)
            {
                throw Refused("holds a character outside ASCII");
            }

            copy[i] = (byte)text[i];
        }

        CheckRule(copy);
        return new DeploymentName(copy);
    }

    /// <summary>The strict parse of <see cref="Parse"/>, without the exception.</summary>
    public static bool TryParse(string? text, out DeploymentName? name)
    {
        name = null;
        if (text is null)
        {
            return false;
        }

        try
        {
            name = Parse(text);
            return true;
        }
        catch (ProtocolException)
        {
            return false;
        }
    }

    /// <summary>Reads the wire bytes under the same rule as <see cref="Parse"/>. The bytes are copied before they are checked.</summary>
    internal static DeploymentName FromBytes(ReadOnlySpan<byte> wire)
    {
        CheckLength(wire.Length);
        var copy = wire.ToArray();
        CheckRule(copy);
        return new DeploymentName(copy);
    }

    /// <summary>The length rule alone, which a reader applies to the declared length before it reads the bytes.</summary>
    internal static void CheckLength(int length)
    {
        if (length == 0)
        {
            throw new ProtocolException(ProtocolError.InvalidLength, "A deployment name is never empty.");
        }

        if (length > ProtocolLimits.MaxDeploymentNameBytes)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"The deployment name is {ProtocolText.Number(length)} bytes; the limit is {ProtocolText.Number(ProtocolLimits.MaxDeploymentNameBytes)}.");
        }
    }

    /// <inheritdoc/>
    public bool Equals(DeploymentName? other) => other is not null && CryptographicOperations.FixedTimeEquals(bytes, other.bytes);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as DeploymentName);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc/>
    public override string ToString() => Value;

    /// <summary>
    /// Dot-separated labels of 1 to 63 bytes, each of <c>a</c> to <c>z</c>, <c>0</c> to <c>9</c> and
    /// <c>-</c>, never starting or ending with <c>-</c> (the LDH syntax of RFC 5890, section 2.3.1,
    /// in lowercase); the last label starts with a letter, as every top-level domain does, so no
    /// IPv4 literal in any notation (<c>127.0.0.1</c>, <c>0x7f000001</c>) is a name.
    /// </summary>
    private static void CheckRule(ReadOnlySpan<byte> name)
    {
        var start = 0;
        var lastStartsWithLetter = false;
        for (var i = 0; i <= name.Length; i++)
        {
            if (i < name.Length && name[i] != (byte)'.')
            {
                var c = name[i];
                if (!(c is >= (byte)'a' and <= (byte)'z' || c is >= (byte)'0' and <= (byte)'9' || c == (byte)'-'))
                {
                    throw Refused("holds a byte other than a lowercase letter, a digit, a hyphen or a dot");
                }

                continue;
            }

            var label = name[start..i];
            if (label.Length == 0)
            {
                throw Refused("has an empty label");
            }

            if (label.Length > ProtocolLimits.MaxDeploymentLabelBytes)
            {
                throw Refused($"has a label over {ProtocolText.Number(ProtocolLimits.MaxDeploymentLabelBytes)} bytes");
            }

            if (label[0] == (byte)'-' || label[^1] == (byte)'-')
            {
                throw Refused("has a label starting or ending with a hyphen");
            }

            lastStartsWithLetter = label[0] is >= (byte)'a' and <= (byte)'z';
            start = i + 1;
        }

        if (!lastStartsWithLetter)
        {
            throw Refused("has a last label that does not start with a letter, as an IP address does");
        }
    }

    private static ProtocolException Refused(string why) => new(ProtocolError.InvalidValue, $"The deployment name {why}.");
}
