using System.Globalization;
using Shabana.Core.Formatting;
using Shabana.Core.Numerics;
using Shabana.Core.Resources;
using Shabana.Core.Sales;
using Shabana.Core.Text;
using Shabana.Core.Auth;

namespace Shabana.Tests.Core;

/// <summary>
/// P1 acceptance (1): every example of 02 section 3 ("Formatting and locale rules"), row by row. A shop reads these numbers all day (04 section 4.6).
/// Each test names the row of the table it pins.
/// </summary>
public class GoldenFormattingTests
{
    // ---- Row "Digits": Arabic-Indic digits, thousands U+066C, decimal U+066B, on every PC ----

    [Fact]
    public void Digits_use_arabic_indic_with_thousands_and_decimal_marks()
    {
        ArabicNumber.FormatInteger(1234567890).ShouldBe("١٬٢٣٤٬٥٦٧٬٨٩٠");
        ArabicNumber.FormatInteger(0).ShouldBe("٠");
        ArabicNumber.FormatInteger(999).ShouldBe("٩٩٩");
        ArabicNumber.FormatInteger(1000).ShouldBe("١٬٠٠٠");
        ArabicNumber.FormatScaled(12345, 1, 1).ShouldBe("١٬٢٣٤٫٥");
        ((int)ArabicNumber.ThousandsSeparator).ShouldBe(0x066C);
        ((int)ArabicNumber.DecimalSeparator).ShouldBe(0x066B);
        ((int)ArabicNumber.PercentSign).ShouldBe(0x066A);
        ((int)ArabicNumber.MinusSign).ShouldBe(0x2212);
    }

    [Fact]
    public void Formatting_does_not_depend_on_the_machine_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "en-US", "ar-EG", "de-DE", "fa-IR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                MoneyFormat.Format(new Money(1234550)).ShouldBe("١٢٬٣٤٥٫٥٠ ج.م", name);
                QtyFormat.Format(new Qty(2500), "كجم").ShouldBe("٢٫٥ كجم", name);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // ---- Row "formatMoney(x)" ----

    [Theory]
    [InlineData(1234550, "١٢٬٣٤٥٫٥٠ ج.م")] // the two examples of the spec
    [InlineData(50000, "٥٠٠ ج.م")]
    [InlineData(0, "٠ ج.م")]
    [InlineData(50, "٠٫٥٠ ج.م")] // not an integer: exactly two digits
    [InlineData(1, "٠٫٠١ ج.م")]
    [InlineData(100, "١ ج.م")] // integer: no fraction
    [InlineData(5550, "٥٥٫٥٠ ج.م")] // 18.50 x 3: the piasters must show (MoneyDisplay.tsx comment)
    [InlineData(123456789, "١٬٢٣٤٬٥٦٧٫٨٩ ج.م")]
    [InlineData(99999999999999, "٩٩٩٬٩٩٩٬٩٩٩٬٩٩٩٫٩٩ ج.م")]
    [InlineData(-500, "−٥ ج.م")]
    [InlineData(-1550, "−١٥٫٥٠ ج.م")]
    public void FormatMoney(long piasters, string expected) => MoneyFormat.Format(new Money(piasters)).ShouldBe(expected);

    [Fact]
    public void FormatMoney_handles_the_extremes_of_long_exactly()
    {
        MoneyFormat.Format(new Money(long.MaxValue)).ShouldBe("٩٢٬٢٣٣٬٧٢٠٬٣٦٨٬٥٤٧٬٧٥٨٫٠٧ ج.م");
        MoneyFormat.Format(new Money(long.MinValue)).ShouldBe("−٩٢٬٢٣٣٬٧٢٠٬٣٦٨٬٥٤٧٬٧٥٨٫٠٨ ج.م");
    }

    [Fact]
    public void FormatMoney_symbol_is_the_business_currency_and_unknown_falls_back_to_EGP()
    {
        MoneyFormat.Format(new Money(1050), "SAR").ShouldBe("١٠٫٥٠ ر.س");
        MoneyFormat.Format(new Money(1050), "USD").ShouldBe("١٠٫٥٠ $");
        MoneyFormat.Format(new Money(1050), "XXX").ShouldBe("١٠٫٥٠ ج.م");
        MoneyFormat.Format(new Money(1050), null).ShouldBe("١٠٫٥٠ ج.م");
    }

    // ---- Row "Business currency": 12 currencies, EGP default ----

    [Fact]
    public void The_twelve_currency_symbols_of_the_spec()
    {
        var expected = new Dictionary<string, string>
        {
            ["EGP"] = "ج.م", ["SAR"] = "ر.س", ["AED"] = "د.إ", ["QAR"] = "ر.ق", ["USD"] = "$", ["EUR"] = "€",
            ["TRY"] = "₺", ["MAD"] = "د.م", ["LBP"] = "ل.ل", ["ILS"] = "₪", ["DZD"] = "د.ج", ["SYP"] = "ل.س",
        };
        Currencies.All.Select(c => c.Code).ShouldBe(expected.Keys, ignoreOrder: false);
        foreach (var c in Currencies.All)
        {
            c.Symbol.ShouldBe(expected[c.Code]);
            MoneyFormat.Format(new Money(100), c.Code).ShouldBe("١ " + expected[c.Code]);
        }

        Currencies.All.Count.ShouldBe(12);
        Currencies.DefaultCode.ShouldBe("EGP");
        Labels.Common.Currency.ShouldBe("ج.م");
    }

    // ---- Row "formatQty(x, unit?)" ----

    [Theory]
    [InlineData(40000, null, "٤٠")] // integer as is
    [InlineData(2500, null, "٢٫٥")] // trailing zeros trimmed
    [InlineData(1235, null, "١٫٢٣٥")] // 3 dp
    [InlineData(1000, "كجم", "١ كجم")]
    [InlineData(2500, "كجم", "٢٫٥ كجم")]
    [InlineData(40000, "وحدة", "٤٠ وحدة")]
    [InlineData(1, null, "٠٫٠٠١")]
    [InlineData(1234567891, null, "١٬٢٣٤٬٥٦٧٫٨٩١")]
    [InlineData(0, "كجم", "٠ كجم")]
    [InlineData(-1500, "كجم", "−١٫٥ كجم")]
    public void FormatQty(long milli, string? unit, string expected) => QtyFormat.Format(new Qty(milli), unit).ShouldBe(expected);

    [Fact]
    public void FormatQty_unit_labels_come_from_the_labels_file()
    {
        QtyFormat.Format(new Qty(2500), UnitType.Kg).ShouldBe("٢٫٥ كجم");
        QtyFormat.Format(new Qty(2000), UnitType.Unit).ShouldBe("٢ وحدة");
    }

    // ---- Row "Percent" ----

    [Theory]
    [InlineData(1000, "١٠٪")]
    [InlineData(10000, "١٠٠٪")]
    [InlineData(1250, "١٢٫٥٪")]
    [InlineData(0, "٠٪")]
    [InlineData(1, "٠٫٠١٪")]
    public void Percent(int bp, string expected) => PercentFormat.Format(new Bp(bp)).ShouldBe(expected);

    // ---- Row "Signed amounts" ----

    [Theory]
    [InlineData(10000, "+١٠٠ ج.م")]
    [InlineData(-5050, "−٥٠٫٥٠ ج.م")]
    [InlineData(0, "+٠ ج.م")]
    public void Signed_amounts_use_plus_or_u2212_then_the_absolute_amount(long piasters, string expected) =>
        MoneyFormat.FormatSigned(new Money(piasters)).ShouldBe(expected);

    // ---- Rows "Calendar dates" and "ISO weekday" ----

    [Fact]
    public void Calendar_dates_short_and_long()
    {
        ArabicDate.Short(new DateOnly(2026, 9, 5)).ShouldBe("٥ سبتمبر");
        ArabicDate.Long(new DateOnly(2026, 9, 5)).ShouldBe("٥ سبتمبر ٢٠٢٦");
        ArabicDate.Short("2026-09-05").ShouldBe("٥ سبتمبر");
        ArabicDate.Long("2026-09-05").ShouldBe("٥ سبتمبر ٢٠٢٦");
        ArabicDate.Long("2023-12-31").ShouldBe("٣١ ديسمبر ٢٠٢٣");
        ArabicDate.ToIso(new DateOnly(2026, 9, 5)).ShouldBe("2026-09-05");
        ArabicDate.TryParseIso("2026-9-5", out _).ShouldBeFalse();
        ArabicDate.TryParseIso("2026-02-30", out _).ShouldBeFalse();
        ArabicDate.TryParseIso("2026-09-05T10:00:00Z", out var d).ShouldBeTrue();
        d.ShouldBe(new DateOnly(2026, 9, 5));
    }

    [Fact]
    public void All_twelve_month_names()
    {
        var months = new[] { "يناير", "فبراير", "مارس", "أبريل", "مايو", "يونيو", "يوليو", "أغسطس", "سبتمبر", "أكتوبر", "نوفمبر", "ديسمبر" };
        for (var m = 1; m <= 12; m++)
        {
            ArabicDate.MonthName(m).ShouldBe(months[m - 1]);
        }
    }

    [Fact]
    public void Iso_weekday_1_is_monday_and_7_is_sunday()
    {
        // 2026-09-07 is a Monday
        var labels = new[] { "الاثنين", "الثلاثاء", "الأربعاء", "الخميس", "الجمعة", "السبت", "الأحد" };
        for (var i = 0; i < 7; i++)
        {
            var date = new DateOnly(2026, 9, 7).AddDays(i);
            ArabicDate.IsoWeekday(date).ShouldBe(i + 1);
            ArabicDate.WeekdayName(date).ShouldBe(labels[i]);
        }
    }

    // ---- Row "Numeric input" ----

    [Theory]
    [InlineData("١٢٣", 12300)] // Arabic-Indic digits
    [InlineData("۱۲۳", 12300)] // Persian digits
    [InlineData("12.5", 1250)]
    [InlineData("12٫5", 1250)] // ٫ is the decimal point
    [InlineData("12,5", 1250)] // , too
    [InlineData("12،5", 1250)] // ، too
    [InlineData("١٬٢٣٤٫٥", 123450)] // ٬ dropped
    [InlineData("1 234", 123400)] // spaces dropped
    [InlineData(".5", 50)]
    [InlineData("5.", 500)]
    [InlineData("0", 0)]
    [InlineData("  7  ", 700)]
    public void Numeric_input_accepts_arabic_digits_and_all_decimal_marks(string text, long expectedPiasters)
    {
        Money.TryParse(text, out var money).ShouldBeTrue(text);
        money.Piasters.ShouldBe(expectedPiasters);
    }

    [Fact]
    public void Numeric_input_leading_minus_only_where_signed_values_are_legal()
    {
        Money.TryParse("-5", out _).ShouldBeFalse();
        Money.TryParse("-5", true, out var m, out _).ShouldBeTrue();
        m.Piasters.ShouldBe(-500);
        Money.TryParse("−5٫5", true, out m, out _).ShouldBeTrue(); // the U+2212 sign the app prints
        m.Piasters.ShouldBe(-550);
        Qty.TryParse("-2.5", true, out var q, out _).ShouldBeTrue();
        q.Milli.ShouldBe(-2500);
        Money.TryParse("5-", true, out _, out _).ShouldBeFalse(); // only in first position
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.2.3")] // only one dot (the web silently dropped the extra one; we refuse)
    [InlineData("1,234.50")] // ambiguous: a comma is a decimal mark, so two marks are an error rather than a silent 1.23450
    [InlineData(".")]
    [InlineData("-")]
    [InlineData("12abc")]
    [InlineData("99999999999999999999")]
    public void Numeric_input_rejects_what_is_not_a_number(string text) => Money.TryParse(text, true, out _, out _).ShouldBeFalse(text);

    [Theory]
    [InlineData("1.005", 101, true)] // 3rd decimal 5 -> half away from zero
    [InlineData("1.004", 100, true)]
    [InlineData("1.0049999", 100, true)]
    [InlineData("0.995", 100, true)]
    [InlineData("2.50", 250, false)]
    [InlineData("2.500", 250, false)] // dropped digits are zeros: no rounding hint
    public void Numeric_input_rounds_extra_digits_half_away_from_zero_and_says_so(string text, long piasters, bool hint)
    {
        Money.TryParse(text, false, out var m, out var rounded).ShouldBeTrue();
        m.Piasters.ShouldBe(piasters);
        rounded.ShouldBe(hint);
    }

    [Fact]
    public void Negative_extra_digits_round_away_from_zero_too()
    {
        Money.TryParse("-1.005", true, out var m, out var rounded).ShouldBeTrue();
        m.Piasters.ShouldBe(-101);
        rounded.ShouldBeTrue();
    }

    [Fact]
    public void Quantities_parse_to_three_decimals_and_percentages_to_two()
    {
        Qty.TryParse("1.2345", out var q, out var rq).ShouldBeTrue();
        q.Milli.ShouldBe(1235);
        rq.ShouldBeTrue();
        Qty.Parse("٢٫٥").Milli.ShouldBe(2500);
        Bp.TryParse("12.5", out var bp).ShouldBeTrue();
        bp.Value.ShouldBe(1250);
        Bp.TryParse("١٠", out bp).ShouldBeTrue();
        bp.Value.ShouldBe(1000);
        Bp.TryParse("-1", out _).ShouldBeFalse();
    }

    [Fact]
    public void Typing_filter_keeps_half_finished_input()
    {
        NumberInput.NormalizeForTyping("12.").ShouldBe("12.");
        NumberInput.NormalizeForTyping("-").ShouldBe("-");
        NumberInput.NormalizeForTyping("").ShouldBe("");
        NumberInput.NormalizeForTyping("١٢٫٥x").ShouldBe("12.5");
        NumberInput.NormalizeForTyping("1.2.3").ShouldBe("1.23");
        NumberInput.NormalizeForTyping("5-3").ShouldBe("53");
    }

    // ---- Row "Rounding": money 2 dp, quantities 3 dp, exact integers ----

    [Fact]
    public void Money_and_qty_are_exact_scaled_integers_never_floats()
    {
        Money.Parse("1234.56").Piasters.ShouldBe(123456);
        Qty.Parse("2.5").Milli.ShouldBe(2500);
        Money.Parse("0.1").Piasters.ShouldBe(10);
        (Money.Parse("0.1") + Money.Parse("0.2")).ShouldBe(Money.Parse("0.3")); // the classic double failure
        new Money(123456).ToString().ShouldBe("1234.56");
        new Money(-5).ToString().ShouldBe("-0.05");
        new Qty(2500).ToString().ShouldBe("2.5");
        new Bp(1250).ToString().ShouldBe("12.5%");
    }

    [Fact]
    public void Money_arithmetic_is_checked()
    {
        Should.Throw<OverflowException>(() => new Money(long.MaxValue) + new Money(1));
        Should.Throw<OverflowException>(() => -new Money(long.MinValue));
        Should.Throw<OverflowException>(() => Qty.FromWhole(long.MaxValue));
        (new Money(5) < new Money(6)).ShouldBeTrue();
        new Money(5).CompareTo(new Money(6)).ShouldBeLessThan(0);
    }

    // ---- Row "Percent discount rounding": round(subtotal * pct / 100, 2) once ----

    [Theory]
    [InlineData(10000, 1000, 1000)] // 100.00 x 10 % = 10.00
    [InlineData(333, 1000, 33)] // 3.33 x 10 % = 0.333 -> 0.33
    [InlineData(335, 1000, 34)] // 3.35 x 10 % = 0.335 -> 0.34 (half up)
    [InlineData(12345, 1250, 1543)] // 123.45 x 12.5 % = 15.43125 -> 15.43
    [InlineData(1, 5000, 1)] // 0.01 x 50 % = 0.005 -> 0.01
    [InlineData(1, 4999, 0)]
    [InlineData(99999, 10000, 99999)]
    [InlineData(0, 10000, 0)]
    public void Percent_discount_is_rounded_once(long subtotal, int bp, long expected) =>
        MoneyMath.PercentDiscount(new Money(subtotal), new Bp(bp)).Piasters.ShouldBe(expected);

    [Theory]
    [InlineData(1000, 1850, 1850)] // 1 x 18.50
    [InlineData(3000, 1850, 5550)] // 3 x 18.50 (the invoice must show the piasters)
    [InlineData(2500, 4000, 10000)] // 2.5 kg x 40.00
    [InlineData(333, 1000, 333)] // 0.333 x 10.00 = 3.33
    [InlineData(1, 1, 0)] // 0.001 x 0.01 = 0.00001
    [InlineData(500, 1, 1)] // 0.5 x 0.01 = 0.005 -> 0.01 (half up)
    [InlineData(499, 1, 0)]
    [InlineData(1500, 333, 500)] // 499.5 piasters -> 500
    public void Line_total_is_round_of_qty_times_price(long qtyMilli, long price, long expected) =>
        MoneyMath.LineTotal(new Qty(qtyMilli), new Money(price)).Piasters.ShouldBe(expected);

    // ---- Row "Order number" ----

    [Fact]
    public void Order_numbers_are_the_noun_then_the_sequence_in_arabic_digits()
    {
        OrderNumber.Sales(7).ShouldBe("أمر بيع ٧");
        OrderNumber.Purchase(12).ShouldBe("أمر شراء ١٢");
        OrderNumber.Sales(1234).ShouldBe("أمر بيع ١٬٢٣٤"); // toLocaleString('ar-EG') groups
        OrderNumber.Sales(null).ShouldBe("أمر بيع");
        OrderNumber.Purchase(null).ShouldBe("أمر شراء");
    }

    // ---- Row "Phone validation" ----

    [Theory]
    [InlineData("01012345678", true, true)]
    [InlineData("+201012345678", true, true)]
    [InlineData("00201012345678", true, true)]
    [InlineData("201012345678", true, true)]
    [InlineData("010 1234 5678", true, true)]
    [InlineData("010-1234-5678", true, true)]
    [InlineData("(010)12345678", true, true)]
    [InlineData("+20 100 123 4567", true, true)]
    [InlineData("٠١٠١٢٣٤٥٦٧٨", true, true)] // Arabic-Indic digits (deliberate improvement on the web)
    [InlineData("01312345678", false, false)] // 013 is not a carrier
    [InlineData("0101234567", false, false)] // too short
    [InlineData("010123456789", false, false)] // too long
    [InlineData("057-2401180", false, true)] // Damietta landline
    [InlineData("0572401180", false, true)]
    [InlineData("02-23456789", false, true)] // Cairo
    [InlineData("0223456789", false, true)]
    [InlineData("023456789", false, true)]
    [InlineData("0212345", false, false)] // landline needs 6..8 subscriber digits
    [InlineData("", false, false)]
    [InlineData("abc", false, false)]
    public void Phone_validation(string input, bool mobile, bool any)
    {
        PhoneEg.IsValidMobile(input).ShouldBe(mobile, input);
        PhoneEg.IsValidPhone(input).ShouldBe(any, input);
    }

    [Fact]
    public void Phone_normalisation_turns_the_country_code_into_a_zero()
    {
        PhoneEg.Normalize("+20 100 123 4567").ShouldBe("01001234567");
        PhoneEg.Normalize("0020 100 1234567").ShouldBe("01001234567");
        PhoneEg.Normalize("(057) 240-1180").ShouldBe("0572401180");
        PhoneEg.Normalize("  ").ShouldBe("");
        PhoneEg.Classify("01001234567").ShouldBe(PhoneKind.Mobile);
        PhoneEg.Classify("0572401180").ShouldBe(PhoneKind.Landline);
        PhoneEg.Classify("12").ShouldBe(PhoneKind.Invalid);
    }

    // ---- Row "Username" ----

    [Theory]
    [InlineData("ahmed", "ahmed", true)]
    [InlineData("  Ahmed ", "ahmed", true)]
    [InlineData("a.b_c-d9", "a.b_c-d9", true)]
    [InlineData("user name", "user name", false)]
    [InlineData("a@b", "a@b", false)]
    [InlineData("أحمد", "أحمد", false)]
    [InlineData("", "", false)]
    public void Username_is_trimmed_lowercased_and_restricted(string input, string normalized, bool valid)
    {
        Username.Normalize(input).ShouldBe(normalized);
        Username.IsValid(input).ShouldBe(valid);
    }

    [Fact]
    public void Username_is_at_most_64_characters_like_the_schema_check()
    {
        Username.IsValid(new string('a', 64)).ShouldBeTrue();
        Username.IsValid(new string('a', 65)).ShouldBeFalse();
        Username.IsValid("\u212A").ShouldBeFalse(); // Kelvin sign: no Unicode case folding into the ASCII set
    }

    // ---- Row "Arabic search" ----

    [Theory]
    [InlineData("أحمد", "احمد")]
    [InlineData("إبراهيم", "ابراهيم")]
    [InlineData("آمنة", "امنه")]
    [InlineData("ٱلله", "الله")]
    [InlineData("شبانة", "شبانه")]
    [InlineData("على", "علي")]
    [InlineData("مئة", "ميه")]
    [InlineData("مؤسسة", "موسسه")]
    [InlineData("مُحَمَّد", "محمد")] // tashkeel
    [InlineData("مــحمد", "محمد")] // tatweel
    [InlineData("محمدٰ", "محمد")] // dagger alef
    [InlineData("١٢٣", "123")] // Arabic-Indic digits
    [InlineData("۱۲۳", "123")] // Persian digits
    [InlineData("  a   B\t c ", "a b c")] // whitespace collapsed, trimmed, lower-cased
    [InlineData("", "")]
    public void Search_normalisation(string input, string expected) => SearchNormalizer.Normalize(input).ShouldBe(expected);

    [Fact]
    public void Search_terms_are_anded_and_order_independent()
    {
        // the doc-comment example of lib/search.ts
        SearchNormalizer.Matches("أمر بيع ١٢ · الامام عياد", "عياد ١٢").ShouldBeTrue();
        SearchNormalizer.Matches("أمر بيع ١٢ · الامام عياد", "12 عياد").ShouldBeTrue();
        SearchNormalizer.Matches("أمر بيع ١٢ · الامام عياد", "عياد ١٣").ShouldBeFalse();
        SearchNormalizer.Matches("أحمد شبانة", "احمد شبانه").ShouldBeTrue();
        SearchNormalizer.Matches("anything", "").ShouldBeTrue();
        SearchNormalizer.Matches("anything", "   ").ShouldBeTrue();
        SearchNormalizer.Matches(null, "x").ShouldBeFalse();
        SearchNormalizer.MatchesNormalized("Abc", SearchNormalizer.Normalize("ABC")).ShouldBeTrue();
    }
}
