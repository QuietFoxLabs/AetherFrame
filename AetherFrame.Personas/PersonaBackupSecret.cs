using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AetherFrame.Personas;

/// <summary>
/// The secret a player gives to protect or open a backup, held as characters that are zeroed on
/// disposal rather than as an immutable string, and written to text as a fixed placeholder. This
/// type applies no policy: what a secret must look like (decision K5) and how it becomes a key are
/// the backup codec's, and neither is decided yet. A copy the caller made before this one, such as
/// an input field's string, is outside its reach.
/// </summary>
public sealed class PersonaBackupSecret : IDisposable
{
    private readonly char[] text;
    private bool disposed;

    private PersonaBackupSecret(char[] text)
    {
        this.text = text;
    }

    /// <summary>A secret holding a copy of <paramref name="text"/>.</summary>
    public static PersonaBackupSecret FromText(ReadOnlySpan<char> text) => new(text.ToArray());

    /// <summary>The characters, for a codec in this assembly.</summary>
    /// <exception cref="ObjectDisposedException">After <see cref="Dispose"/>.</exception>
    internal ReadOnlySpan<char> Text
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return text;
        }
    }

    /// <summary>Zeroes the characters.</summary>
    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(text.AsSpan()));
        }
    }

    /// <summary>A placeholder, never the secret.</summary>
    public override string ToString() => "[backup secret]";
}
