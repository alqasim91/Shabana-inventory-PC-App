using Shabana.Core.Auth;
using Shabana.Core.Errors;
using Shabana.Core.Numerics;
using Shabana.Core.Sales;

namespace Shabana.Tests.Core;

public class PolicyAndRulesTests
{
    // ---- 04 section 6.2 ----

    [Theory]
    [InlineData("abcdefgh", "bob", true)]
    [InlineData("abcdefg", "bob", false)] // 7
    [InlineData("aaaaaaaa", "bob", false)] // single repeated character
    [InlineData("11111111", "bob", false)]
    [InlineData("password", "password", false)] // equals the username
    [InlineData("Password", "password", false)] // ... ignoring case
    [InlineData("passwor1", "password", true)]
    [InlineData("كلمة_سر_قوية", "bob", true)] // Arabic letters allowed
    [InlineData("سسسسسسسسس", "bob", false)]
    [InlineData("", "bob", false)]
    [InlineData("12345678", "bob", true)] // the policy is the three rules of the spec, nothing more
    public void Password_policy(string password, string username, bool ok) => PasswordPolicy.IsAcceptable(password, username).ShouldBe(ok);

    [Fact]
    public void Password_policy_reports_every_broken_rule_with_an_arabic_message()
    {
        var issues = PasswordPolicy.Check("aaa", "aaa");
        issues.ShouldBe([PasswordIssue.TooShort, PasswordIssue.SameAsUsername, PasswordIssue.RepeatedCharacter]);
        foreach (var issue in Enum.GetValues<PasswordIssue>())
        {
            PasswordPolicy.Message(issue).ShouldNotBeNullOrWhiteSpace();
        }

        PasswordPolicy.Check("abcdefgh", null).ShouldBeEmpty();
    }

    [Fact]
    public void Password_length_counts_code_points_after_nfc_so_composed_and_decomposed_forms_agree()
    {
        var composed = "cafés123"; // 8 characters
        var decomposed = "cafés123"; // 9 UTF-16 units, 8 characters after NFC
        PasswordPolicy.NormalizeForHashing(decomposed).ShouldBe(composed);
        PasswordPolicy.IsAcceptable(decomposed, "x").ShouldBeTrue();
        PasswordPolicy.IsAcceptable("cafés12", "x").ShouldBeFalse(); // 7
        PasswordPolicy.IsAcceptable(new string('é', 8), "x").ShouldBeFalse(); // repeated, even if typed decomposed:
        PasswordPolicy.IsAcceptable(string.Concat(Enumerable.Repeat("é", 8)), "x").ShouldBeFalse();
        PasswordPolicy.IsAcceptable("\U0001F600\U0001F600\U0001F600\U0001F600\U0001F600\U0001F600\U0001F600\U0001F601", "x").ShouldBeTrue(); // astral characters count once
    }

    // ---- 02 section 8.2 / 03 section 2 discount ----

    [Fact]
    public void Discount_amount_percent_and_clamping()
    {
        var sub = new Money(10_000); // 100.00
        DiscountRules.Amount(DiscountType.None, 0, sub).Piasters.ShouldBe(0);
        DiscountRules.Amount(DiscountType.Amount, 2_500, sub).Piasters.ShouldBe(2_500);
        DiscountRules.Amount(DiscountType.Amount, 99_999, sub).Piasters.ShouldBe(10_000); // clamped to the subtotal
        DiscountRules.Amount(DiscountType.Percent, 1_000, sub).Piasters.ShouldBe(1_000);
        DiscountRules.Amount(DiscountType.Percent, 10_000, sub).Piasters.ShouldBe(10_000);
        DiscountRules.Amount(DiscountType.Percent, 20_000, sub).Piasters.ShouldBe(10_000); // never negative total
        DiscountRules.Amount(DiscountType.Percent, 1_250, new Money(12_345)).Piasters.ShouldBe(1_543); // 15.43125 -> 15.43
        DiscountRules.Amount(DiscountType.Amount, -5, sub).Piasters.ShouldBe(0);
        DiscountRules.Amount(DiscountType.Amount, 5, Money.Zero).Piasters.ShouldBe(0);
        DiscountRules.Total(DiscountType.Percent, 1_250, new Money(12_345)).Piasters.ShouldBe(12_345 - 1_543);
        DiscountRules.Amount(DiscountType.Percent, long.MaxValue, new Money(Limits.MaxStoredPiasters)).Piasters.ShouldBe(Limits.MaxStoredPiasters);
    }

    [Theory]
    [InlineData(DiscountType.None, 0, 10_000, null, false, DiscountCheck.Ok)]
    [InlineData(DiscountType.Amount, 0, 10_000, null, false, DiscountCheck.ValueRequired)] // type chosen, no value
    [InlineData(DiscountType.Percent, 0, 10_000, null, false, DiscountCheck.ValueRequired)]
    [InlineData(DiscountType.Percent, 10_000, 10_000, null, false, DiscountCheck.Ok)] // exactly 100 %
    [InlineData(DiscountType.Percent, 10_001, 10_000, null, false, DiscountCheck.PercentOver100)]
    [InlineData(DiscountType.Amount, 10_000, 10_000, null, false, DiscountCheck.Ok)]
    [InlineData(DiscountType.Amount, 10_001, 10_000, null, false, DiscountCheck.ExceedsSubtotal)]
    [InlineData(DiscountType.Amount, 10_001, 10_000, null, true, DiscountCheck.Ok)] // admin edit: the lines are rewritten first (B3)
    [InlineData(DiscountType.Percent, 5_000, 10_000, 5_000L, false, DiscountCheck.Ok)] // collected == new total
    [InlineData(DiscountType.Percent, 5_000, 10_000, 5_001L, false, DiscountCheck.BelowPaid)]
    [InlineData(DiscountType.None, 0, 10_000, 10_001L, false, DiscountCheck.BelowPaid)]
    public void Discount_checks(DiscountType type, long value, long subtotal, long? collected, bool editing, DiscountCheck expected) =>
        DiscountRules.Check(type, value, new Money(subtotal), collected is { } c ? new Money(c) : null, editing).ShouldBe(expected);

    [Fact]
    public void Discount_failures_map_to_the_schema_error_codes()
    {
        DiscountCheck.PercentOver100.ToErrorCode().ShouldBe(ErrorCode.DiscountPercentOver100);
        DiscountCheck.ExceedsSubtotal.ToErrorCode().ShouldBe(ErrorCode.DiscountExceedsSubtotal);
        DiscountCheck.BelowPaid.ToErrorCode().ShouldBe(ErrorCode.DiscountBelowPaid);
        DiscountCheck.Ok.ToErrorCode().ShouldBeNull();
        DiscountCheck.ValueRequired.ToErrorCode().ShouldBeNull(); // UI-level message (SO_FORM.discountRequired)
    }

    [Fact]
    public void Discount_amount_matches_the_schema_generated_column_formula()
    {
        // discount_amount_piasters = min(subtotal, (subtotal * bp + 5000) / 10000) for percent; the value itself for amount
        var rnd = new Random(8);
        for (var i = 0; i < 100_000; i++)
        {
            var sub = rnd.NextInt64(0, Limits.MaxStoredPiasters / 1000);
            var bp = rnd.Next(0, 10_001);
            var sql = Math.Min(sub, (sub * bp + 5000) / 10000);
            DiscountRules.Amount(DiscountType.Percent, bp, new Money(sub)).Piasters.ShouldBe(sql);
        }
    }

    // ---- limits ----

    [Fact]
    public void Limits_of_03_section_2()
    {
        Limits.IsValidLineQty(new Qty(1_000_000_000)).ShouldBeTrue();
        Limits.IsValidLineQty(new Qty(1_000_000_001)).ShouldBeFalse();
        Limits.IsValidLineQty(new Qty(0)).ShouldBeFalse();
        Limits.IsValidUnitPrice(new Money(1_000_000_000)).ShouldBeTrue();
        Limits.IsValidUnitPrice(new Money(1_000_000_001)).ShouldBeFalse();
        Limits.IsValidStoredMoney(new Money(999_999_999_999)).ShouldBeTrue();
        Limits.IsValidStoredMoney(new Money(1_000_000_000_000)).ShouldBeFalse();
        Limits.IsValidStoredQty(new Qty(999_999_999_999)).ShouldBeTrue();
    }
}
