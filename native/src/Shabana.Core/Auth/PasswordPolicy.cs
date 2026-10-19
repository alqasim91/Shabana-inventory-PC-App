using System.Text;
using Shabana.Core.Resources;

namespace Shabana.Core.Auth;

public enum PasswordIssue
{
    TooShort,
    SameAsUsername,
    RepeatedCharacter,
}

/// <summary>
/// New-password rules (04 section 6.2): at least 8 characters, not equal to the username, not a single repeated character. Arabic letters are
/// allowed; text is NFC-normalised (<see cref="NormalizeForHashing"/>) before it is counted, compared or hashed. A character is a Unicode code point.
/// (02 section 4.2 describes the web's weaker 6-character rule; 04 wins for the native app.)
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;

    public static string NormalizeForHashing(string password) => password.Normalize(NormalizationForm.FormC);

    /// <summary>Every rule the password breaks (empty list = acceptable). <paramref name="username"/> is compared after <see cref="Username.Normalize"/>, ignoring case.</summary>
    public static IReadOnlyList<PasswordIssue> Check(string? password, string? username)
    {
        var issues = new List<PasswordIssue>();
        var normalized = NormalizeForHashing(password ?? string.Empty);
        var runes = normalized.EnumerateRunes().ToList();

        if (runes.Count < MinLength)
        {
            issues.Add(PasswordIssue.TooShort);
        }

        var name = Username.Normalize(username);
        if (name.Length > 0 && string.Equals(normalized.Trim(), name, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(PasswordIssue.SameAsUsername);
        }

        if (runes.Count > 0 && runes.All(r => r == runes[0]))
        {
            issues.Add(PasswordIssue.RepeatedCharacter);
        }

        return issues;
    }

    public static bool IsAcceptable(string? password, string? username) => Check(password, username).Count == 0;

    public static string Message(PasswordIssue issue) => issue switch
    {
        PasswordIssue.TooShort => Labels.Pwd.TooShort,
        PasswordIssue.SameAsUsername => Labels.Pwd.SameAsUsername,
        PasswordIssue.RepeatedCharacter => Labels.Pwd.RepeatedChar,
        _ => throw new ArgumentOutOfRangeException(nameof(issue), issue, null),
    };
}
