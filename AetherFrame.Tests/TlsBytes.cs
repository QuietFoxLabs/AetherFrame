using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AetherFrame.Tests;

/// <summary>
/// TLS records as the server's TLS client sends them toward the Lodestone (RFC 8446, section 5.1),
/// for the pipe's tests: a ClientHello (section 4.1.2) with the extensions a test chooses, and
/// the records that follow it. Nothing here is a working handshake: the pipe reads records and the
/// ClientHello's names, never the cryptography.
/// </summary>
internal static class TlsBytes
{
    internal const string Lodestone = "na.finalfantasyxiv.com";

    /// <summary>A <c>server_name</c> extension's body with one <c>host_name</c> (RFC 6066, section 3).</summary>
    internal static byte[] ServerName(params string[] hosts) => ServerNameOfType(0, hosts);

    /// <summary>A <c>server_name</c> extension's body whose entries have <paramref name="nameType"/>.</summary>
    internal static byte[] ServerNameOfType(byte nameType, params string[] hosts)
    {
        var list = new List<byte>();
        foreach (var host in hosts)
        {
            var name = Encoding.ASCII.GetBytes(host);
            list.Add(nameType);
            list.AddRange(Length2(name.Length));
            list.AddRange(name);
        }

        return [.. Length2(list.Count), .. list];
    }

    /// <summary>A ClientHello record for <paramref name="host"/>, with supported_versions for TLS 1.3, as the server's client sends one.</summary>
    internal static byte[] ClientHello(string host = Lodestone) =>
        ClientHelloWith((0x0000, ServerName(host)), (0x002B, [0x02, 0x03, 0x04]));

    /// <summary>A ClientHello record carrying exactly <paramref name="extensions"/>, each a type and its body.</summary>
    internal static byte[] ClientHelloWith(params (int Type, byte[] Body)[] extensions)
    {
        var hello = new List<byte> { 0x03, 0x03 };
        hello.AddRange(Enumerable.Repeat((byte)0x5A, 32));      // random
        hello.Add(32);
        hello.AddRange(Enumerable.Repeat((byte)0xA5, 32));      // legacy_session_id
        hello.AddRange([0x00, 0x04, 0x13, 0x01, 0x13, 0x02]);  // TLS_AES_128_GCM_SHA256, TLS_AES_256_GCM_SHA384
        hello.AddRange([0x01, 0x00]);                          // no compression
        var body = new List<byte>();
        foreach (var (type, data) in extensions)
        {
            body.AddRange(Length2(type));
            body.AddRange(Length2(data.Length));
            body.AddRange(data);
        }

        hello.AddRange(Length2(body.Count));
        hello.AddRange(body);
        return Handshake(1, [.. hello]);
    }

    /// <summary>One handshake message of <paramref name="type"/> in its own record.</summary>
    internal static byte[] Handshake(byte type, byte[] message) =>
        Record(22, [type, (byte)(message.Length >> 16), (byte)(message.Length >> 8), (byte)message.Length, .. message]);

    /// <summary>A record of <paramref name="contentType"/> holding <paramref name="body"/>.</summary>
    internal static byte[] Record(byte contentType, byte[] body) => [contentType, 0x03, 0x03, .. Length2(body.Length), .. body];

    /// <summary>The middlebox compatibility change_cipher_spec record a TLS 1.3 client sends.</summary>
    internal static byte[] ChangeCipherSpec() => Record(20, [0x01]);

    /// <summary>A protected record of <paramref name="length"/> bytes.</summary>
    internal static byte[] ApplicationData(int length) => Record(23, Enumerable.Repeat((byte)0x3C, length).ToArray());

    private static byte[] Length2(int value) => [(byte)(value >> 8), (byte)value];
}
