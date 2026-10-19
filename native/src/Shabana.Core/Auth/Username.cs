using System.Text.RegularExpressions;

namespace Shabana.Core.Auth;

/// <summary>
/// Login handle (02 section 3 / 4.2; schema CHECK <c>length 1..64 AND no char outside [a-z0-9._-]</c>). Lower-cased and trimmed before use,
/// so <c>"Ahmed "</c> and <c>ahmed</c> are the same login. Only ASCII letters are lower-cased (no culture or Unicode case folding: a Kelvin sign is not a "k").
/// </summary>
public static partial class Username
{
    public const int MaxLength = 64;

    [GeneratedRegex("^[a-z0-9._-]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex UsernameRegex();

    public static string Normalize(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var chars = input.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= 'A' and <= 'Z')
            {
                chars[i] = (char)(chars[i] + 32);
            }
        }

        return new string(chars);
    }

    /// <summary>True when the NORMALIZED form is 1..64 characters of <c>[a-z0-9._-]</c>.</summary>
    public static bool IsValid(string? input) => TryNormalize(input, out _);

    public static bool TryNormalize(string? input, out string username)
    {
        username = Normalize(input);
        return username.Length is >= 1 and <= MaxLength && UsernameRegex().IsMatch(username);
    }
}
