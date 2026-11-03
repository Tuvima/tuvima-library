namespace MediaEngine.Api.Services.Plugins.Files;

/// <summary>How serious a finding in a plugin file is: an error blocks saving, a note (a "thing to know") does not.</summary>
public enum PluginFileSeverity
{
    Error,
    Note,
}

/// <summary>The kinds of finding the checker reports (the wire word is in <see cref="PluginFileProblemCodes"/>).</summary>
public static class PluginFileProblemCodes
{
    public const string Syntax = "syntax";
    public const string Type = "type";
    public const string Range = "range";
    public const string Value = "value";
    public const string Missing = "missing";
    public const string UnknownWord = "unknown_word";
    public const string Rule = "rule";
    public const string ChangedLocked = "changed_locked";
}

/// <summary>One finding: a 1-based line and column, how serious it is, and what it means in plain words.</summary>
public sealed record PluginFileProblem(int Line, int Column, PluginFileSeverity Severity, string Code, string Message);

/// <summary>What the checker found in a piece of plugin text.</summary>
public sealed record PluginFileCheckResult(IReadOnlyList<PluginFileProblem> Problems)
{
    public int ErrorCount => Problems.Count(problem => problem.Severity == PluginFileSeverity.Error);

    public int NoteCount => Problems.Count(problem => problem.Severity == PluginFileSeverity.Note);

    /// <summary>True when nothing blocks saving. Notes do not count.</summary>
    public bool IsValid => ErrorCount == 0;
}
