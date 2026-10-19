using System.Text.RegularExpressions;
using Shabana.Core.Errors;
using Shabana.Core.Resources;

namespace Shabana.Tests.Core;

/// <summary>P1 acceptance (5): every <see cref="ErrorCode"/> of 03 section 9 has an Arabic message (the test enumerates all values).</summary>
public class ErrorCodeTests
{
    private static readonly Regex ArabicLetter = new("[ء-ي]", RegexOptions.CultureInvariant);

    [Fact]
    public void Every_error_code_has_a_non_empty_arabic_message()
    {
        var all = Enum.GetValues<ErrorCode>();
        all.Length.ShouldBe(34);
        foreach (var code in all)
        {
            var message = code.Message();
            message.ShouldNotBeNullOrWhiteSpace(code.ToString());
            ArabicLetter.IsMatch(message).ShouldBeTrue($"{code}: {message}");
            message.ShouldNotContain("{", Case.Sensitive, code.ToString()); // 03 section 9: the text is the web message WITHOUT its interpolated figures
            message.ShouldNotContain("|", Case.Sensitive, code.ToString());
            message.ShouldBe(message.Trim());
        }
    }

    [Fact]
    public void Code_strings_round_trip_and_follow_the_schema_spelling()
    {
        ErrorCode.PaymentOverpay.Code().ShouldBe("PAYMENT_OVERPAY");
        ErrorCode.DiscountPercentOver100.Code().ShouldBe("DISCOUNT_PERCENT_OVER_100");
        ErrorCode.PoConversionExceedsRemaining.Code().ShouldBe("PO_CONVERSION_EXCEEDS_REMAINING");
        ErrorCode.PlcExceedsRemaining.Code().ShouldBe("PLC_EXCEEDS_REMAINING");
        foreach (var code in Enum.GetValues<ErrorCode>())
        {
            ErrorCodes.TryParse(code.Code(), out var parsed).ShouldBeTrue(code.ToString());
            parsed.ShouldBe(code);
            Regex.IsMatch(code.Code(), "^[A-Z][A-Z0-9_]*$").ShouldBeTrue(code.Code());
        }

        Enum.GetValues<ErrorCode>().Select(c => c.Code()).Distinct().Count().ShouldBe(34);
    }

    [Fact]
    public void Schema_messages_with_a_bar_are_parsed_by_their_code()
    {
        ErrorCodes.TryParse("SO_NOT_FOUND|أمر البيع غير موجود", out var code).ShouldBeTrue();
        code.ShouldBe(ErrorCode.SoNotFound);
        ErrorCodes.MessageFor("CASH_FLOOR|whatever the trigger appended").ShouldBe(ErrorCode.CashFloor.Message());
        ErrorCodes.TryParse("NOT_A_CODE", out _).ShouldBeFalse();
        ErrorCodes.TryParse("", out _).ShouldBeFalse();
        ErrorCodes.TryParse(null, out _).ShouldBeFalse();
        ErrorCodes.MessageFor("NOT_A_CODE").ShouldBe(Labels.ErrGeneric.Unexpected);
        ErrorCodes.MessageFor(null).ShouldBe(Labels.ErrGeneric.Unexpected);
    }

    [Fact]
    public void Labels_ERR_group_and_the_enum_are_one_to_one()
    {
        Labels.Err.ByKey.Keys.Order().ShouldBe(Enum.GetValues<ErrorCode>().Select(c => c.Code()).Order());
    }

    /// <summary>Parses the "Error codes raised by the schema" table of 03-DB-PORTING-NOTES.md (CRLF or LF input).</summary>
    internal static Dictionary<string, string> ParseErrorTable(string markdown)
    {
        var text = TestPaths.NormalizeEol(markdown);
        var section = text.Split("**Error codes raised by the schema**")[1].Split("**Recipes**")[0];
        return Regex.Matches(section, @"^\| `([A-Z0-9_]+)` \| (.+) \|$", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }

    [Fact]
    public void ParseErrorTable_gives_the_same_rows_for_CRLF_and_LF_text()
    {
        const string lf = "intro\n**Error codes raised by the schema**\n\n| Code | Message |\n|---|---|\n| `A_B` | first |\n| `C_D` | second \"x\" |\n\n**Recipes**\n| `Z` | ignored |\n";
        var viaLf = ParseErrorTable(lf);
        viaLf.Count.ShouldBe(2);
        ParseErrorTable(lf.Replace("\n", "\r\n")).ShouldBe(viaLf);
        ParseErrorTable(lf.Replace("\n", "\r")).ShouldBe(viaLf);

        // the real document, forced to CRLF, as a Windows checkout with core.autocrlf=true would have it
        var doc = TestPaths.NativeConversion("03-DB-PORTING-NOTES.md");
        if (doc is not null)
        {
            var real = File.ReadAllText(doc);
            ParseErrorTable(TestPaths.NormalizeEol(real).Replace("\n", "\r\n")).ShouldBe(ParseErrorTable(real));
        }
    }

    [Fact]
    public void Enum_matches_the_table_of_03_section_9_text_for_text()
    {
        var doc = TestPaths.NativeConversion("03-DB-PORTING-NOTES.md");
        if (doc is null)
        {
            return; // docs not shipped next to the sources (the other tests still pin the enum)
        }

        var rows = ParseErrorTable(File.ReadAllText(doc));
        rows.Count.ShouldBe(34);
        foreach (var code in Enum.GetValues<ErrorCode>())
        {
            rows.ShouldContainKey(code.Code());
            code.Message().ShouldBe(rows[code.Code()], code.Code());
        }
    }

    [Fact]
    public void Enum_matches_the_codes_the_baseline_schema_actually_raises()
    {
        var sql = TestPaths.ReadText(Path.Combine(TestPaths.NativeDir, "src", "Shabana.Data", "Migrations", "Scripts", "0001_baseline.sql"));
        var raised = Regex.Matches(sql, @"RAISE\(ABORT,\s*'([A-Z0-9_]+)\|")
            .Select(m => m.Groups[1].Value).Distinct().Order().ToList();
        raised.Count.ShouldBe(34);
        raised.ShouldBe(Enum.GetValues<ErrorCode>().Select(c => c.Code()).Order());
    }
}
