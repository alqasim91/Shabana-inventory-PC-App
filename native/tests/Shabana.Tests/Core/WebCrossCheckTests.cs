using System.Text.Json;
using Shabana.Core.Auth;
using Shabana.Core.Formatting;
using Shabana.Core.Numerics;
using Shabana.Core.Text;

namespace Shabana.Tests.Core;

/// <summary>
/// Cross-check against the web app's ACTUAL code. <c>Fixtures/web-crosscheck.json</c> was produced by running src/lib/search.ts, phone.ts, username.ts,
/// digits.ts, date.ts and the formatMoney/formatQty of components/shared/MoneyDisplay.tsx under Node 22 (full ICU, ar-EG) on seeded random inputs
/// (<c>Fixtures/gen-web-crosscheck.mjs</c> regenerates it). The native code must agree except where a divergence is deliberate (listed in the test names).
/// </summary>
public class WebCrossCheckTests
{
    private static readonly JsonElement Vectors = JsonDocument.Parse(File.ReadAllText(TestPaths.Fixture("web-crosscheck.json"))).RootElement;

    private static IEnumerable<JsonElement> Rows(string name) => Vectors.GetProperty(name).EnumerateArray();

    [Fact]
    public void Search_normalize_equals_the_web_on_thousands_of_random_arabic_and_latin_strings()
    {
        var n = 0;
        foreach (var r in Rows("search"))
        {
            SearchNormalizer.Normalize(r.GetProperty("input").GetString()).ShouldBe(r.GetProperty("normalized").GetString(), r.GetProperty("input").GetString());
            n++;
        }

        n.ShouldBeGreaterThan(4000);
    }

    [Fact]
    public void Phone_validation_equals_the_web()
    {
        var n = 0;
        foreach (var r in Rows("phone"))
        {
            var input = r.GetProperty("input").GetString();
            PhoneEg.IsValidMobile(input).ShouldBe(r.GetProperty("mobile").GetBoolean(), $"mobile '{input}'");
            PhoneEg.IsValidPhone(input).ShouldBe(r.GetProperty("phone").GetBoolean(), $"phone '{input}'");
            n++;
        }

        n.ShouldBeGreaterThan(1500);
    }

    [Fact]
    public void Username_equals_the_web_for_normalised_input_and_differs_only_by_the_64_character_schema_limit()
    {
        foreach (var r in Rows("username"))
        {
            var input = r.GetProperty("input").GetString()!;
            var web = r.GetProperty("valid").GetBoolean();
            if (input != Username.Normalize(input))
            {
                continue; // the web validates AFTER lower-casing; native IsValid normalises itself (checked in the golden tests)
            }

            if (input.Length > Username.MaxLength)
            {
                web.ShouldBeTrue(); // the web has no length limit ...
                Username.IsValid(input).ShouldBeFalse(); // ... the schema CHECK (1..64) does: deliberate divergence
                continue;
            }

            Username.IsValid(input).ShouldBe(web, input);
        }
    }

    [Fact]
    public void Digit_folding_and_typing_filter_equal_the_web()
    {
        foreach (var r in Rows("digits"))
        {
            var input = r.GetProperty("input").GetString();
            NumberInput.ToLatinDigits(input).ShouldBe(r.GetProperty("latin").GetString(), input);
            NumberInput.NormalizeForTyping(input).ShouldBe(r.GetProperty("typing").GetString(), input);
        }
    }

    [Fact]
    public void FormatMoney_equals_the_web_toLocaleString_ar_EG_for_1500_amounts()
    {
        var n = 0;
        foreach (var r in Rows("money"))
        {
            var piasters = r.GetProperty("piasters").GetInt64();
            MoneyFormat.Format(new Money(piasters)).ShouldBe(r.GetProperty("web").GetString(), piasters.ToString());
            n++;
        }

        n.ShouldBeGreaterThan(1500);
    }

    [Fact]
    public void FormatQty_equals_the_web_toLocaleString_ar_EG_for_1500_quantities()
    {
        foreach (var r in Rows("qty"))
        {
            var milli = r.GetProperty("milli").GetInt64();
            QtyFormat.Format(new Qty(milli)).ShouldBe(r.GetProperty("web").GetString(), milli.ToString());
            QtyFormat.Format(new Qty(milli), "KGU").ShouldBe(r.GetProperty("webUnit").GetString(), milli.ToString());
        }
    }

    [Fact]
    public void Short_and_long_dates_equal_the_web()
    {
        foreach (var r in Rows("dateShort"))
        {
            ArabicDate.Short(r.GetProperty("iso").GetString()!).ShouldBe(r.GetProperty("web").GetString(), r.GetProperty("iso").GetString());
        }

        foreach (var r in Rows("dateLong"))
        {
            ArabicDate.Long(r.GetProperty("iso").GetString()!).ShouldBe(r.GetProperty("web").GetString(), r.GetProperty("iso").GetString());
        }
    }
}
