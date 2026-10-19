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

    [Fact]
    public void Enum_matches_the_table_of_03_section_9_text_for_text()
    {
        var doc = TestPaths.NativeConversion("03-DB-PORTING-NOTES.md");
        if (doc is null)
        {
            return; // docs not shipped next to the sources (the other tests still pin the enum)
        }

        var text = File.ReadAllText(doc);
        var section = text.Split("**Error codes raised by the schema**")[1].Split("**Recipes**")[0];
        var rows = Regex.Matches(section, @"^\| `([A-Z0-9_]+)` \| (.+) \|$", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
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
        var sql = File.ReadAllText(Path.Combine(TestPaths.NativeDir, "src", "Shabana.Data", "Migrations", "Scripts", "0001_baseline.sql"));
        var raised = Regex.Matches(sql, @"RAISE\(ABORT,\s*'([A-Z0-9_]+)\|")
            .Select(m => m.Groups[1].Value).Distinct().Order().ToList();
        raised.Count.ShouldBe(34);
        raised.ShouldBe(Enum.GetValues<ErrorCode>().Select(c => c.Code()).Order());
    }
}
