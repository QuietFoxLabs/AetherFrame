using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AetherFrame.Services.Diagnostics;

/// <summary>
/// Keeps characters' Content IDs out of AetherFrame's log. A character's binding is stored as
/// "{ContentId}.json" (its Recovery, migration-backup and temporary copies start with the same
/// number), AetherFrame treats the Content ID as local binding data only, and a log is what
/// players paste into bug reports. So log text never names such a file:
/// <list type="bullet">
/// <item>a message about a binding says "a character binding file", and one that names any file
/// uses <see cref="FileName"/>, which shows <see cref="CharacterBindingFile"/> for a binding's;</item>
/// <item>everything the plugin logs passes <see cref="RedactingAetherFrameLog"/> (or, for the few
/// direct Dalamud log calls, <see cref="ForLog"/>), which does the same inside any text that still
/// carries such a name — the path in an I/O exception's message, say.</item>
/// </list>
/// Only files named by a number are affected: Plates, Templates and images are named by Guids, so
/// every other file keeps its name.
/// <para>
/// The same pass hides the remote protocol's identifiers (docs/networking/NETWORK1.md, safeguard 7):
/// a persona identity (<c>psn_</c> and 64 hex digits) and the profile, revision and asset ids
/// (<c>prf_</c>, <c>rev_</c>, <c>ast_</c> and 32 hex digits). They are public values, but a log is
/// pasted into public bug reports, and two mentions of one identity would let a reader link them,
/// so a log shows their kind and never their value. Only the player flavour (no networking code)
/// produces none; the sharing build, released since 0.1.8, does whenever a character shares.
/// </para>
/// </summary>
internal static partial class LogPrivacy
{
    /// <summary>What a log shows instead of a character binding file's name.</summary>
    internal const string CharacterBindingFile = "[character binding file]";

    /// <summary>What a log shows instead of a persona identity.</summary>
    internal const string PersonaIdentity = "[persona id]";

    /// <summary>What a log shows instead of a remote profile id.</summary>
    internal const string ProfileIdentifier = "[profile id]";

    /// <summary>What a log shows instead of a revision id.</summary>
    internal const string RevisionIdentifier = "[revision id]";

    /// <summary>What a log shows instead of an asset id.</summary>
    internal const string AssetIdentifier = "[asset id]";

    /// <summary>The file name of <paramref name="path"/> as a log may show it.</summary>
    internal static string FileName(string path) => Redact(Path.GetFileName(path));

    /// <summary>
    /// <paramref name="text"/> with every file name made of a number replaced by
    /// <see cref="CharacterBindingFile"/>, and every protocol identifier by the placeholder of its kind.
    /// </summary>
    internal static string Redact(string text) =>
        NetworkIdentifier().Replace(NumberNamedFile().Replace(text, CharacterBindingFile), static match => match.Value[..3] switch
        {
            "psn" => PersonaIdentity,
            "prf" => ProfileIdentifier,
            "rev" => RevisionIdentifier,
            _ => AssetIdentifier,
        });

    /// <summary>
    /// The exception to log for <paramref name="exception"/>: itself when its text names no file
    /// made of a number and no protocol identifier, otherwise a stand-in with its type name,
    /// message, inner exceptions and stack trace, each redacted (see <see cref="Redact"/>).
    /// </summary>
    [return: NotNullIfNotNull(nameof(exception))]
    internal static Exception? ForLog(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        var text = exception.ToString();
        return NumberNamedFile().IsMatch(text) || NetworkIdentifier().IsMatch(text) ? new RedactedException(exception) : exception;
    }

    // The protocol's text forms exactly (docs/networking/ProtocolSpecification-v1.md): the prefix
    // and the lowercase hex digits, as a whole word. Nothing shorter, longer or uppercase is one,
    // and a prefix inside another word is left alone; one beside a hyphen, slash or bracket is hidden.
    [GeneratedRegex(@"(?<!\w)(?:psn_[0-9a-f]{64}|(?:prf|rev|ast)_[0-9a-f]{32})(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex NetworkIdentifier();

    // A whole file name (nothing but a separator, quote, bracket, space or the start of the text
    // before it) whose stem is only digits, ending .json or .tmp after any further extensions: a
    // binding ("1001.json"), its Recovery copy ("1001.damaged-20260927-010203-004.json") and its
    // migration backup, Dalamud's temporary file ("1001.json.tmp") and AetherFrame's own
    // (".1001.json.{id}.tmp"). A Guid-named file never matches: its last group follows a dash.
    [GeneratedRegex(@"(?<![\w.\-])\.?\d+(?:\.[\w\-]+)*\.(?:json|tmp)\b", RegexOptions.CultureInvariant)]
    private static partial Regex NumberNamedFile();

    /// <summary>An exception as <see cref="ForLog"/> logs it: the original's text, redacted.</summary>
    private sealed class RedactedException : Exception
    {
        private readonly string typeName;
        private readonly string? stackTrace;

        internal RedactedException(Exception original)
            : base(Redact(original.Message), ForLog(original.InnerException))
        {
            typeName = original.GetType().FullName ?? original.GetType().Name;
            stackTrace = original.StackTrace is { } trace ? Redact(trace) : null;
            HResult = original.HResult;
        }

        public override string? StackTrace => stackTrace;

        /// <summary>As <see cref="Exception.ToString"/> writes the original, under its own type name.</summary>
        public override string ToString()
        {
            var text = new StringBuilder(typeName).Append(": ").Append(Message);
            if (InnerException is { } inner)
            {
                text.Append(" ---> ").Append(inner).AppendLine().Append("   --- End of inner exception stack trace ---");
            }

            if (stackTrace is not null)
            {
                text.AppendLine().Append(stackTrace);
            }

            return text.ToString();
        }
    }
}
