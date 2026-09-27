using System;
using System.Linq;

namespace AetherFrame.ReleaseTools;

/// <summary>
/// The only URL shape the repository metadata may carry: absolute https on a dotted host name, default
/// port, no user information, no query, no fragment, nothing but URL characters, and written in canonical
/// form. Everything Dalamud downloads comes from such a URL, so a malformed or surprising one fails here
/// instead of in players' clients.
/// </summary>
public static class Urls
{
    public static Uri ValidateHttps(string? url, string what)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ReleaseCheckException($"{what} is empty.");
        }

        if (url.Any(c => c <= ' ' || c == (char)0x7F || c == '<' || c == '>' || c == '"' || c == '{' || c == '}' || c == '|' || c == '\\' || c == '^' || c == '`' || c > '~'))
        {
            throw new ReleaseCheckException($"{what} '{url}' contains characters that do not belong in a URL.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ReleaseCheckException($"{what} '{url}' is not an absolute URL.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ReleaseCheckException($"{what} '{url}' must use https.");
        }

        if (uri.UserInfo.Length > 0)
        {
            throw new ReleaseCheckException($"{what} '{url}' must not carry user information.");
        }

        if (uri.Host.Length == 0 || uri.HostNameType is not (UriHostNameType.Dns))
        {
            throw new ReleaseCheckException($"{what} '{url}' must name a host.");
        }

        // A dotted domain name: not localhost or another single-label name, and not the "name." form.
        if (!uri.Host.Contains('.', StringComparison.Ordinal) || uri.Host.EndsWith('.'))
        {
            throw new ReleaseCheckException($"{what} '{url}' must name a public host by a dotted domain name, not localhost or a single-label name.");
        }

        if (!uri.IsDefaultPort)
        {
            throw new ReleaseCheckException($"{what} '{url}' must use the default https port.");
        }

        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new ReleaseCheckException($"{what} '{url}' must not have a query string or a fragment.");
        }

        // The text must be what a client requests: lowercase scheme and host, no explicit default port, and
        // no '.' or '..' segments, plain or percent-encoded. Otherwise a reviewer reads one address and a
        // client fetches another, which would defeat checks such as the owner and repository at the start
        // of the download template.
        if (uri.AbsoluteUri != url)
        {
            throw new ReleaseCheckException($"{what} '{url}' is not written in canonical form; a client would request '{uri.AbsoluteUri}'.");
        }

        return uri;
    }

    /// <summary>The last path segment of a validated URL, as written.</summary>
    public static string LastSegment(Uri uri)
    {
        var path = uri.AbsolutePath;
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }
}
