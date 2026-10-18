namespace MediaEngine.Storage.Schema.Upgrades;

/// <summary>
/// Reads just enough of a SQL script to find statements that would break the runner's one-transaction-per-step
/// guarantee. It skips string literals, quoted identifiers and comments, and keeps <c>CREATE TRIGGER ... BEGIN ... END</c>
/// bodies and <c>CASE ... END</c> expressions together so their <c>BEGIN</c>/<c>END</c> are not mistaken for statements.
/// </summary>
internal static class SqlScriptInspector
{
    private static readonly HashSet<string> ForbiddenStatementStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "BEGIN", "COMMIT", "END", "ROLLBACK", "SAVEPOINT", "RELEASE", "VACUUM", "ATTACH", "DETACH",
    };

    /// <summary>
    /// Returns the first statement-leading keyword that takes control of the transaction (or cannot run inside one),
    /// upper-cased, or <c>null</c> when the script has none.
    /// </summary>
    public static string? FindForbiddenStatementStart(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var statementWords = 0;
        var inTrigger = false;
        var sawTriggerBegin = false;
        var triggerEndPending = false;
        var caseDepth = 0;
        string? first = null;

        var index = 0;
        while (index < script.Length)
        {
            var c = script[index];
            if (c == '-' && Peek(script, index + 1) == '-')
            {
                index = SkipLine(script, index);
            }
            else if (c == '/' && Peek(script, index + 1) == '*')
            {
                var end = script.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? script.Length : end + 2;
            }
            else if (c is '\'' or '"' or '`')
            {
                index = SkipQuoted(script, index, c);
            }
            else if (c == '[')
            {
                var end = script.IndexOf(']', index + 1);
                index = end < 0 ? script.Length : end + 1;
            }
            else if (c == ';')
            {
                index++;
                // A trigger statement runs to the END that closes its body; every other statement ends here.
                if (!inTrigger || triggerEndPending)
                {
                    statementWords = 0;
                    inTrigger = false;
                    sawTriggerBegin = false;
                    triggerEndPending = false;
                    caseDepth = 0;
                    first = null;
                }
            }
            else if (char.IsLetter(c) || c == '_')
            {
                var start = index;
                while (index < script.Length && (char.IsLetterOrDigit(script[index]) || script[index] == '_'))
                {
                    index++;
                }

                var word = script[start..index];
                statementWords++;
                if (statementWords == 1)
                {
                    first = word;
                    if (ForbiddenStatementStarts.Contains(word))
                    {
                        return word.ToUpperInvariant();
                    }
                }
                else if (statementWords is 2 or 3
                         && string.Equals(first, "CREATE", StringComparison.OrdinalIgnoreCase)
                         && string.Equals(word, "TRIGGER", StringComparison.OrdinalIgnoreCase))
                {
                    inTrigger = true;
                }

                if (inTrigger)
                {
                    TrackTriggerWord(word, ref sawTriggerBegin, ref triggerEndPending, ref caseDepth);
                }
            }
            else
            {
                index++;
            }
        }

        return null;
    }

    private static void TrackTriggerWord(string word, ref bool sawBegin, ref bool endPending, ref int caseDepth)
    {
        if (string.Equals(word, "CASE", StringComparison.OrdinalIgnoreCase))
        {
            caseDepth++;
            endPending = false;
        }
        else if (string.Equals(word, "END", StringComparison.OrdinalIgnoreCase))
        {
            if (caseDepth > 0)
            {
                caseDepth--;
                endPending = false;
            }
            else
            {
                endPending = sawBegin;
            }
        }
        else
        {
            endPending = false;
            if (string.Equals(word, "BEGIN", StringComparison.OrdinalIgnoreCase))
            {
                sawBegin = true;
            }
        }
    }

    private static char Peek(string script, int index)
        => index < script.Length ? script[index] : '\0';

    private static int SkipLine(string script, int index)
    {
        var end = script.IndexOf('\n', index);
        return end < 0 ? script.Length : end + 1;
    }

    private static int SkipQuoted(string script, int index, char quote)
    {
        index++;
        while (index < script.Length)
        {
            if (script[index] == quote)
            {
                if (Peek(script, index + 1) == quote)
                {
                    index += 2;
                    continue;
                }

                return index + 1;
            }

            index++;
        }

        return script.Length;
    }
}
