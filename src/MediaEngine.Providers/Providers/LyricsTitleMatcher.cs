using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Providers;

/// <summary>
/// Decides whether a title or artist found by a lyrics source is the one asked for, by the rules in the source's
/// <c>match</c> block. Both sides are reduced to the same plain form first; they match when those forms are equal and not
/// empty. With no rules set the forms are only trimmed and compared ignoring capitals, which is the plain rule.
/// </summary>
internal sealed class LyricsTitleMatcher
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex Bracketed = new(@"\([^)]*\)|\[[^\]]*\]", RegexOptions.CultureInvariant, PatternTimeout);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant, PatternTimeout);

    private readonly TextTrackLyricsMatchConfiguration _rules;
    private readonly List<Regex> _suffixPatterns = [];
    private readonly ILogger _logger;
    private readonly string _provider;

    public LyricsTitleMatcher(TextTrackLyricsMatchConfiguration rules, ILogger logger, string provider)
    {
        _rules = rules;
        _logger = logger;
        _provider = provider;
        foreach (var pattern in rules.StripSuffixPatterns.Where(pattern => !string.IsNullOrWhiteSpace(pattern)))
        {
            try
            {
                _suffixPatterns.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout));
            }
            catch (ArgumentException ex)
            {
                // The file validator reports a bad pattern when the file loads; a lookup must still work without it.
                _logger.LogWarning(ex, "{Provider} lyrics match pattern {Pattern} is not a valid regular expression and is ignored", provider, pattern);
            }
        }
    }

    /// <summary>The plain form of <paramref name="text"/> that titles are compared by.</summary>
    public string Normalize(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (_rules.IgnoreCase)
        {
            value = value.ToLowerInvariant();
        }

        if (_rules.IgnoreBracketed)
        {
            value = Bracketed.Replace(value, " ");
        }

        foreach (var pattern in _suffixPatterns)
        {
            try
            {
                value = pattern.Replace(value, string.Empty);
            }
            catch (RegexMatchTimeoutException ex)
            {
                _logger.LogWarning(ex, "{Provider} lyrics match pattern {Pattern} took too long and was skipped", _provider, pattern.ToString());
            }
        }

        if (_rules.IgnoreDiacritics)
        {
            value = FoldDiacritics(value);
        }

        if (_rules.IgnorePunctuation)
        {
            value = Spaces.Replace(DropPunctuation(value), " ");
        }

        return value.Trim();
    }

    /// <summary>True when both texts reduce to the same non-empty plain form.</summary>
    public bool Matches(string? found, string? wanted)
    {
        var left = Normalize(found);
        return left.Length > 0 && string.Equals(left, Normalize(wanted), StringComparison.Ordinal);
    }

    private static string FoldDiacritics(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Apostrophes join the letters around them ("don't" becomes "dont"); other punctuation separates words.</summary>
    private static string DropPunctuation(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '\'' or '’' or '‘' or '`')
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) ? character : ' ');
        }

        return builder.ToString();
    }
}
