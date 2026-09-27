using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AetherFrame.ReleaseTools;

/// <summary>A definite release check failure. The message names what is wrong and what was expected.</summary>
public sealed class ReleaseCheckException : Exception
{
    public ReleaseCheckException(string message)
        : base(message)
    {
    }
}

/// <summary>Wrong command-line usage (exit code 2), as opposed to a failed check (exit code 1).</summary>
public sealed class UsageException : Exception
{
    public UsageException(string message)
        : base(message)
    {
    }
}

/// <summary>One named check and its outcome.</summary>
public sealed record Check(string Name, bool Passed, string Detail);

/// <summary>
/// The checks a command ran, in order. Validators record every pass and failure here so a failed run
/// reports everything it found, and stop (<see cref="ThrowIfFailed"/>) only where later checks could
/// not mean anything.
/// </summary>
public sealed class CheckList
{
    private readonly List<Check> checks = new();

    public IReadOnlyList<Check> Checks => checks;

    public bool HasFailures => checks.Any(c => !c.Passed);

    public int FailureCount => checks.Count(c => !c.Passed);

    public void Pass(string name, string detail = "") => checks.Add(new Check(name, true, detail));

    public void Fail(string name, string detail) => checks.Add(new Check(name, false, detail));

    /// <summary>Records a pass or a failure and returns the condition.</summary>
    public bool Require(bool condition, string name, string passDetail, string failDetail)
    {
        if (condition)
        {
            Pass(name, passDetail);
        }
        else
        {
            Fail(name, failDetail);
        }

        return condition;
    }

    /// <summary>
    /// Runs a step that throws <see cref="ReleaseCheckException"/> on failure, recording either outcome.
    /// Returns the step's value, or default when it failed.
    /// </summary>
    public T? Attempt<T>(string name, Func<T> step, Func<T, string>? passDetail = null)
    {
        try
        {
            var value = step();
            Pass(name, passDetail?.Invoke(value) ?? string.Empty);
            return value;
        }
        catch (ReleaseCheckException e)
        {
            Fail(name, e.Message);
            return default;
        }
    }

    /// <summary>Stops the run when anything so far failed, so dependent checks are not attempted.</summary>
    public void ThrowIfFailed()
    {
        if (HasFailures)
        {
            throw new ReleaseCheckException($"{FailureCount} check(s) failed.");
        }
    }

    public void Print(TextWriter writer)
    {
        foreach (var check in checks)
        {
            var status = check.Passed ? "[ OK ]" : "[FAIL]";
            writer.WriteLine(check.Detail.Length == 0 ? $"{status} {check.Name}" : $"{status} {check.Name}: {check.Detail}");
        }
    }
}
