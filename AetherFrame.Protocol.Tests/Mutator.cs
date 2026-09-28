using System;
using System.Buffers.Binary;

namespace AetherFrame.Protocol.Tests;

/// <summary>Seeded byte-level mutations of a document: bit flips, inserted and deleted bytes, truncation, extreme length fields, moved and repeated regions.</summary>
internal static class Mutator
{
    public static readonly string[] Strategies =
    [
        "flip bit", "set byte", "insert byte", "delete byte", "truncate", "append", "extreme length at random offset",
        "extreme payload length", "version bytes", "type byte", "key bytes", "signature bytes", "duplicate region", "zero region", "swap regions",
    ];

    public static byte[] Apply(Random random, byte[] document, out string strategy)
    {
        strategy = Strategies[random.Next(Strategies.Length)];
        var copy = (byte[])document.Clone();
        var offset = copy.Length == 0 ? 0 : random.Next(copy.Length);
        switch (strategy)
        {
            case "flip bit":
                copy[offset] ^= (byte)(1 << random.Next(8));
                return copy;
            case "set byte":
                copy[offset] = (byte)random.Next(256);
                return copy;
            case "insert byte":
                return [.. copy.AsSpan(0, offset), (byte)random.Next(256), .. copy.AsSpan(offset)];
            case "delete byte":
                return [.. copy.AsSpan(0, offset), .. copy.AsSpan(offset + 1)];
            case "truncate":
                return copy.AsSpan(0, offset).ToArray();
            case "append":
                return [.. copy, .. new byte[random.Next(1, 8)]];
            case "extreme length at random offset":
                WriteExtreme(random, copy, Math.Min(offset, copy.Length - 4));
                return copy;
            case "extreme payload length":
                WriteExtreme(random, copy, Layout.PayloadLength);
                return copy;
            case "version bytes":
                copy[Layout.Version + random.Next(2)] = (byte)random.Next(256);
                return copy;
            case "type byte":
                copy[Layout.Type] = (byte)random.Next(256);
                return copy;
            case "key bytes":
                copy[Layout.Key + random.Next(65)] = (byte)random.Next(256);
                return copy;
            case "signature bytes":
                copy[copy.Length - 1 - random.Next(64)] = (byte)random.Next(256);
                return copy;
            case "duplicate region":
            {
                var length = random.Next(1, Math.Min(32, copy.Length - offset + 1));
                return [.. copy.AsSpan(0, offset + length), .. copy.AsSpan(offset, length), .. copy.AsSpan(offset + length)];
            }
            case "zero region":
            {
                var length = random.Next(1, Math.Min(32, copy.Length - offset + 1));
                Array.Clear(copy, offset, length);
                return copy;
            }
            case "swap regions":
            {
                var other = random.Next(copy.Length);
                var length = random.Next(1, Math.Min(16, Math.Min(copy.Length - offset, copy.Length - other) + 1));
                var temp = copy.AsSpan(offset, length).ToArray();
                copy.AsSpan(other, length).CopyTo(copy.AsSpan(offset, length));
                temp.CopyTo(copy.AsSpan(other, length));
                return copy;
            }
            default:
                throw new InvalidOperationException(strategy);
        }
    }

    private static void WriteExtreme(Random random, byte[] target, int offset)
    {
        uint[] extremes = [0, 1, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 0xFFFFFFFE, (uint)ProtocolLimits.MaxPayloadBytes, (uint)ProtocolLimits.MaxPayloadBytes + 1, (uint)ProtocolLimits.MaxTextBytes + 1, 9, 257, (uint)random.Next()];
        BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(offset), extremes[random.Next(extremes.Length)]);
    }
}
