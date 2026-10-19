using System.Text;
using Shabana.Core.Resources;

namespace Shabana.Core.Errors;

/// <summary>Code string (as raised by the schema: <c>PAYMENT_OVERPAY</c>) &lt;-&gt; <see cref="ErrorCode"/> &lt;-&gt; Arabic message.</summary>
public static class ErrorCodes
{
    private static readonly Dictionary<ErrorCode, string> ToText = Enum.GetValues<ErrorCode>().ToDictionary(c => c, ToSnake);
    private static readonly Dictionary<string, ErrorCode> FromText = ToText.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    /// <summary>The schema's spelling of the code, e.g. <c>ErrorCode.PoNotFound</c> -> <c>PO_NOT_FOUND</c>.</summary>
    public static string Code(this ErrorCode code) =>
        ToText.TryGetValue(code, out var text) ? text : throw new ArgumentOutOfRangeException(nameof(code), code, null);

    /// <summary>Parses the text before the <c>|</c> of a schema error (<c>SO_NOT_FOUND|...</c> is also accepted: only the part before <c>|</c> is read).</summary>
    public static bool TryParse(string? text, out ErrorCode code)
    {
        code = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var bar = text.IndexOf('|');
        var key = (bar >= 0 ? text[..bar] : text).Trim();
        return FromText.TryGetValue(key, out code);
    }

    /// <summary>The Arabic message (03 section 9: the web text without its interpolated figures).</summary>
    public static string Message(this ErrorCode code) => Labels.Err.ByKey[code.Code()];

    /// <summary>Message for a raw schema code; unknown codes get the generic unexpected-error text, never a stack trace.</summary>
    public static string MessageFor(string? text) => TryParse(text, out var code) ? code.Message() : Labels.ErrGeneric.Unexpected;

    private static string ToSnake(ErrorCode code)
    {
        var name = code.ToString();
        var sb = new StringBuilder(name.Length + 6);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var startsWord = i > 0 && (char.IsUpper(c) || (char.IsDigit(c) && !char.IsDigit(name[i - 1])));
            if (startsWord)
            {
                sb.Append('_');
            }

            sb.Append(char.ToUpperInvariant(c));
        }

        return sb.ToString();
    }
}
