using System;

namespace AetherFrame.Services.Art;

/// <summary>Why an artwork's download failed (art on demand).</summary>
internal enum ArtDownloadProblem
{
    /// <summary>The host couldn't be reached, or the connection broke.</summary>
    Offline,

    /// <summary>The host didn't answer in time.</summary>
    TimedOut,

    /// <summary>The host asked to come back later (429).</summary>
    Busy,

    /// <summary>The host answered with anything but the file (another status, a redirect included).</summary>
    Refused,

    /// <summary>The answer wasn't exactly the file's length.</summary>
    WrongSize,

    /// <summary>The answer's SHA-256 wasn't the file's.</summary>
    WrongDigest,
}

/// <summary>
/// An artwork's download failed. Its message is what a player reads ("Couldn't download Allagan Tech
/// artwork: ..."). Free of any networking type, so the rest of the plugin can catch it (decision R3).
/// </summary>
internal sealed class ArtDownloadException : Exception
{
    internal ArtDownloadException(ArtDownloadProblem problem, int? status = null)
        : base(Describe(problem, status))
    {
        Problem = problem;
        Status = status;
    }

    internal ArtDownloadProblem Problem { get; }

    /// <summary>The host's status code, when it answered.</summary>
    internal int? Status { get; }

    internal static string Describe(ArtDownloadProblem problem, int? status) => problem switch
    {
        ArtDownloadProblem.Offline => "GitHub couldn't be reached",
        ArtDownloadProblem.TimedOut => "GitHub didn't answer in time",
        ArtDownloadProblem.Busy => "GitHub is busy; try again in a few minutes",
        ArtDownloadProblem.Refused => status is { } code ? $"GitHub answered {code}" : "GitHub didn't send the file",
        _ => "the file wasn't the one AetherFrame expects, so it wasn't used",
    };
}
