using AetherFrame.Protocol.Encoding;

namespace AetherFrame.Protocol.Remote;

/// <summary>
/// Timestamps on the wire: unsigned 64-bit Unix seconds, 0 to <see cref="ProtocolLimits.MaxUnixSeconds"/>.
/// Whole seconds, no time zone, no local time: a client converts its instant to Unix seconds before
/// building a model, so two clients in different zones describing the same instant produce the same bytes.
/// </summary>
internal static class ProtocolTimestamps
{
    public static void Check(long unixSeconds, string field)
    {
        if (unixSeconds < 0 || unixSeconds > ProtocolLimits.MaxUnixSeconds)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, $"'{field}' is outside 0 to {ProtocolText.Number(ProtocolLimits.MaxUnixSeconds)} Unix seconds.");
        }
    }

    public static long Read(ref CanonicalReader reader, string field)
    {
        var value = reader.ReadU64(field);
        if (value > ProtocolLimits.MaxUnixSeconds)
        {
            throw new ProtocolException(ProtocolError.InvalidValue, $"'{field}' is outside 0 to {ProtocolText.Number(ProtocolLimits.MaxUnixSeconds)} Unix seconds.");
        }

        return (long)value;
    }
}
