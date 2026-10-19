using System.Text;
using System.Text.RegularExpressions;

namespace Shabana.Core.Text;

public enum PhoneKind
{
    Invalid,
    Mobile,
    Landline,
}

/// <summary>
/// Egyptian phone numbers (02 section 3 "Phone validation"; port of <c>src/lib/phone.ts</c>). Spaces, <c>-</c> and parentheses are ignored; a
/// <c>+20</c> / <c>0020</c> / <c>20</c> prefix means a local <c>0</c>. Mobile <c>^01[0125]\d{8}$</c>; landline <c>^0[2-9]\d{6,8}$</c> (structural on
/// purpose: area codes change, and a stale list that refuses a real customer is worse than accepting a typo; the second digit 2-9 keeps mobiles out).
/// </summary>
public static partial class PhoneEg
{
    [GeneratedRegex("^01[0125][0-9]{8}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex MobileRegex();

    [GeneratedRegex("^0[2-9][0-9]{6,8}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex LandlineRegex();

    /// <summary>The digits as the app compares and stores them: trimmed, separators removed, country code turned into <c>0</c>. Arabic-Indic digits are accepted (the web's <c>\d</c> was ASCII-only).</summary>
    public static string Normalize(string? phone)
    {
        if (string.IsNullOrEmpty(phone))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(phone.Length);
        foreach (var ch in phone.Trim())
        {
            if (char.IsWhiteSpace(ch) || ch is '-' or '(' or ')')
            {
                continue;
            }

            sb.Append(ch);
        }

        // Arabic/Persian digits -> ASCII (decimal marks are not digits of a phone number, so only digits are folded here)
        var bare = FoldDigits(sb.ToString());
        foreach (var prefix in new[] { "+20", "0020", "20" })
        {
            if (bare.StartsWith(prefix, StringComparison.Ordinal) && bare.Length > prefix.Length && bare[prefix.Length] is >= '1' and <= '9')
            {
                return "0" + bare[prefix.Length..];
            }
        }

        return bare;
    }

    public static PhoneKind Classify(string? phone)
    {
        var digits = Normalize(phone);
        if (MobileRegex().IsMatch(digits))
        {
            return PhoneKind.Mobile;
        }

        return LandlineRegex().IsMatch(digits) ? PhoneKind.Landline : PhoneKind.Invalid;
    }

    /// <summary>Egyptian mobile only: the form InstaPay is addressed by.</summary>
    public static bool IsValidMobile(string? phone) => Classify(phone) == PhoneKind.Mobile;

    /// <summary>Any phone a contact may give: mobile or landline.</summary>
    public static bool IsValidPhone(string? phone) => Classify(phone) != PhoneKind.Invalid;

    private static string FoldDigits(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is >= (char)0x0660 and <= (char)0x0669)
            {
                sb.Append((char)('0' + (ch - 0x0660)));
            }
            else if (ch is >= (char)0x06F0 and <= (char)0x06F9)
            {
                sb.Append((char)('0' + (ch - 0x06F0)));
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }
}
