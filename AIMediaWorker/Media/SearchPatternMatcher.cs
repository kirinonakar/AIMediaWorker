using System.Text.RegularExpressions;

namespace AIMediaWorker.Media;

internal sealed class SearchPatternMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private readonly string? _text;
    private readonly Regex? _regex;

    private SearchPatternMatcher(string text, bool useRegex)
    {
        if (useRegex)
            _regex = new Regex(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        else
            _text = text;
    }

    public static SearchPatternMatcher Create(string query, bool useRegex)
    {
        var value = query.Trim();
        if (value.Length == 0) throw new ArgumentException("A search term is required.", nameof(query));
        return new SearchPatternMatcher(value, useRegex);
    }

    public bool IsMatch(string value) => _regex?.IsMatch(value) ?? value.Contains(_text!, StringComparison.CurrentCultureIgnoreCase);
}
