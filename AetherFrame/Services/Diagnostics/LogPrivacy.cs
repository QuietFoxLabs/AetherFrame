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
/// </summary>
internal static partial class LogPrivacy
{
    /// <summary>What a log shows instead of a character binding file's name.</summary>
    internal const string CharacterBindingFile = "[character binding file]";

    /// <summary>The file name of <paramref name="path"/> as a log may show it.</summary>
    internal static string FileName(string path) => Redact(Path.GetFileName(path));

    /// <summary><paramref name="text"/> with every file name made of a number replaced by <see cref="CharacterBindingFile"/>.</summary>
    internal static string Redact(string text) => NumberNamedFile().Replace(text, CharacterBindingFile);

    /// <summary>
    /// The exception to log for <paramref name="exception"/>: itself when its text names no file
    /// made of a number, otherwise a stand-in with its type name, message, inner exceptions and
    /// stack trace, each redacted (see <see cref="Redact"/>).
    /// </summary>
    [return: NotNullIfNotNull(nameof(exception))]
    internal static Exception? ForLog(Exception? exception) =>
        exception is null || !NumberNamedFile().IsMatch(exception.ToString()) ? exception : new RedactedException(exception);

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
