using System.Text;

namespace Shabana.Core.Text;

/// <summary>
/// Arabic-aware text matching (02 section 3 "Arabic search"; port of <c>src/lib/search.ts</c>). Both sides of a comparison are folded to one shape:
/// Arabic-Indic and Persian digits -&gt; ASCII; tashkeel U+064B..0652, dagger alef U+0670 and tatweel U+0640 removed; alef forms
/// (hamza above/below, madda, wasla) -&gt; bare alef; teh marbuta -&gt; heh; alef maksura and yeh-hamza -&gt; yeh; waw-hamza -&gt; waw;
/// whitespace runs -&gt; one space; trimmed; lower-cased. A query is split on spaces and EVERY term must occur in the haystack.
/// Code points are numeric on purpose (SHB0001: no Arabic literal in src/).
/// </summary>
public static class SearchNormalizer
{
    public static string Normalize(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(input.Length);
        var pendingSpace = false;
        foreach (var ch in input)
        {
            if (IsJsWhitespace(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            var mapped = Map(ch);
            if (mapped == char.MinValue)
            {
                continue; // removed mark: it neither emits nor separates
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(mapped);
        }

        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>True when every space-separated term of the (already normalized) query occurs in the normalized haystack. Empty query matches everything.</summary>
    public static bool MatchesNormalized(string? haystack, string normalizedQuery)
    {
        if (string.IsNullOrEmpty(normalizedQuery))
        {
            return true;
        }

        var hay = Normalize(haystack);
        foreach (var term in normalizedQuery.Split(' '))
        {
            if (!hay.Contains(term, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Convenience: normalizes the query, then <see cref="MatchesNormalized"/>. Normalize the query once per keystroke when matching many rows.</summary>
    public static bool Matches(string? haystack, string? query) => MatchesNormalized(haystack, Normalize(query));

    private static char Map(char c)
    {
        // 0x0660..0669 Arabic-Indic and 0x06F0..06F9 extended (Persian) digits
        if (c is >= (char)0x0660 and <= (char)0x0669)
        {
            return (char)('0' + (c - 0x0660));
        }

        if (c is >= (char)0x06F0 and <= (char)0x06F9)
        {
            return (char)('0' + (c - 0x06F0));
        }

        if (c is >= (char)0x064B and <= (char)0x0652 || c == (char)0x0670 || c == (char)0x0640)
        {
            return char.MinValue;
        }

        return c switch
        {
            (char)0x0623 or (char)0x0625 or (char)0x0622 or (char)0x0671 => (char)0x0627, // alef with hamza/madda/wasla -> alef
            (char)0x0629 => (char)0x0647, // teh marbuta -> heh
            (char)0x0649 or (char)0x0626 => (char)0x064A, // alef maksura, yeh with hamza -> yeh
            (char)0x0624 => (char)0x0648, // waw with hamza -> waw
            _ => c,
        };
    }

    /// <summary>JavaScript's <c>\s</c> / <c>trim()</c> set (differs from <see cref="char.IsWhiteSpace(char)"/> at U+0085, U+001C..1F and U+FEFF).</summary>
    private static bool IsJsWhitespace(char c) =>
        (int)c is 9 or 10 or 11 or 12 or 13 or 32 or 0xA0 or 0x1680 or (>= 0x2000 and <= 0x200A)
            or 0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000 or 0xFEFF;
}
