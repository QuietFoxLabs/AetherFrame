using System;
using System.Security.Cryptography;
using AetherFrame.Protocol.Encoding;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Protocol.Requests;

/// <summary>
/// A single-use value a server issues for one request proof (docs/networking/ProtocolSpecification-v1.md,
/// section 14.2): 32 bytes, <c>chl_</c> and 64 lowercase hex digits in text. The client copies it
/// into its proof unchanged; the server accepts each challenge it issued at most once, and only
/// before it expires, so a proof can neither be made in advance nor used twice (decision S1). The
/// all-zero value is never a challenge. Immutable.
/// </summary>
public sealed class RequestChallenge : IEquatable<RequestChallenge>
{
    /// <summary>The text form's prefix.</summary>
    public const string Prefix = "chl_";

    private readonly byte[] bytes;

    private RequestChallenge(byte[] bytes)
    {
        this.bytes = bytes;
    }

    /// <summary>The 32 wire bytes.</summary>
    public ReadOnlySpan<byte> Bytes => bytes;

    /// <summary>A fresh challenge from the platform's cryptographically secure generator, as a server issues one.</summary>
    public static RequestChallenge NewRandom()
    {
        var fresh = new byte[ProtocolConstants.ChallengeLength];
        do
        {
            RandomNumberGenerator.Fill(fresh);
        }
        while (IsAllZero(fresh));

        return new RequestChallenge(fresh);
    }

    /// <summary>Reads the 32 wire bytes, refusing the all-zero value. The bytes are copied before they are checked.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidLength"/> or <see cref="ProtocolError.InvalidValue"/>.</exception>
    public static RequestChallenge FromBytes(ReadOnlySpan<byte> wire)
    {
        if (wire.Length != ProtocolConstants.ChallengeLength)
        {
            throw new ProtocolException(ProtocolError.InvalidLength, $"A challenge is {ProtocolText.Number(ProtocolConstants.ChallengeLength)} bytes; this one is {ProtocolText.Number(wire.Length)}.");
        }

        var copy = wire.ToArray();
        if (IsAllZero(copy))
        {
            throw new ProtocolException(ProtocolError.InvalidValue, "The all-zero value is not a challenge.");
        }

        return new RequestChallenge(copy);
    }

    /// <summary>Parses the text form strictly.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidValue"/>.</exception>
    public static RequestChallenge Parse(string text) =>
        TryParse(text, out var challenge) ? challenge! : throw new ProtocolException(ProtocolError.InvalidValue, "The challenge is not 'chl_' followed by 64 lowercase hex digits, or is all zero.");

    /// <summary>The strict parse of <see cref="Parse"/>, without the exception.</summary>
    public static bool TryParse(string? text, out RequestChallenge? challenge)
    {
        challenge = null;
        if (text is null || text.Length != Prefix.Length + (2 * ProtocolConstants.ChallengeLength) || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parsed = new byte[ProtocolConstants.ChallengeLength];
        if (!ProtocolHex.TryParseLower(text.AsSpan(Prefix.Length), parsed) || IsAllZero(parsed))
        {
            return false;
        }

        challenge = new RequestChallenge(parsed);
        return true;
    }

    /// <summary>The 32 wire bytes, as a copy.</summary>
    public byte[] ToArray() => (byte[])bytes.Clone();

    /// <inheritdoc/>
    public bool Equals(RequestChallenge? other) => other is not null && CryptographicOperations.FixedTimeEquals(bytes, other.bytes);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as RequestChallenge);

    /// <inheritdoc/>
    public override int GetHashCode() => BitConverter.ToInt32(bytes, 0);

    /// <inheritdoc/>
    public override string ToString() => Prefix + ProtocolHex.ToLower(bytes);

    private static bool IsAllZero(ReadOnlySpan<byte> value) => value.IndexOfAnyExcept((byte)0) < 0;
}
