using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Shabana.Core.Resources;

namespace Shabana.Tests;

/// <summary>P0 acceptance (2): labels.ar.json -> Labels.g.cs; missing key and malformed JSON fail the build.</summary>
public class LabelsGeneratorTests
{
    private const string SampleJson = """
        {
          "_templates": [
            { "key": "TABLE.showingCount", "params": ["shown", "total"], "source": "showingCount: (shown: number, total: number) => `x`" },
            { "key": "EXP.dueBannerTitle", "params": ["n"], "variants": [ { "when": "n == 1", "text": "single" } ], "source": "dueBannerTitle: (n: number) => x" },
            { "key": "EXP.weekly", "params": ["day"], "source": "weekly: (day: string) => `x`" }
          ],
          "APP_NAME": "app",
          "COMMON": { "save": "Save", "delete": "Del", "bank_transfer": "BT" },
          "TABLE": { "showingCount": "{0} of {1}" },
          "EXP": { "dueBannerTitle": "{0} many", "weekly": "every {0}" }
        }
        """;

    private static (ImmutableArrayDiag Diagnostics, string? Source) Run(string json, string path = "labels.ar.json")
    {
        var (diagnostics, _, source) = RoslynHarness.RunLabelsGenerator((path, json));
        return (new ImmutableArrayDiag(diagnostics), source);
    }

    private sealed record ImmutableArrayDiag(System.Collections.Immutable.ImmutableArray<Diagnostic> All)
    {
        public IEnumerable<string> ErrorIds => All.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Id);
    }

    [Fact]
    public void Valid_json_generates_constants_methods_and_compiles_clean()
    {
        var (diagnostics, output, source) = RoslynHarness.RunLabelsGenerator(("labels.ar.json", SampleJson));
        diagnostics.ShouldBeEmpty();
        source.ShouldNotBeNull();
        output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        source.ShouldContain("public const string AppName = \"app\";", Case.Sensitive);
        source.ShouldContain("public const string BankTransfer = \"BT\";", Case.Sensitive);
        source.ShouldContain("public static string ShowingCount(long shown, long total)", Case.Sensitive);
    }

    [Fact]
    public void Generated_template_methods_format_with_arabic_digits_and_variants()
    {
        var (_, output, _) = RoslynHarness.RunLabelsGenerator(("labels.ar.json", SampleJson));
        var asm = RoslynHarness.Load(output);
        var table = asm.GetType("Shabana.Core.Resources.Labels+Table")!;
        var exp = asm.GetType("Shabana.Core.Resources.Labels+Exp")!;

        string Call(Type t, string name, params object[] args) => (string)t.GetMethod(name)!.Invoke(null, args)!;

        Call(table, "ShowingCount", 12L, 1234567L).ShouldBe("١٢ of ١٬٢٣٤٬٥٦٧");
        Call(table, "ShowingCount", 0L, -5L).ShouldBe("٠ of −٥");
        static string Expected(long v) => (v < 0 ? "\u2212" : string.Empty)
            + new string([.. System.Numerics.BigInteger.Abs(v).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                .Select(c => c == ',' ? '\u066C' : (char)('\u0660' + (c - '0')))]);
        Call(table, "ShowingCount", long.MinValue, long.MaxValue).ShouldBe(Expected(long.MinValue) + " of " + Expected(long.MaxValue));
        Expected(long.MaxValue).ShouldBe("\u0669\u066C\u0662\u0662\u0663\u066C\u0663\u0667\u0662\u066C\u0660\u0663\u0666\u066C\u0668\u0665\u0664\u066C\u0667\u0667\u0665\u066C\u0668\u0660\u0667");
        Call(exp, "DueBannerTitle", 1L).ShouldBe("single");
        Call(exp, "DueBannerTitle", 2L).ShouldBe("٢ many");
        Call(exp, "Weekly", "Sat").ShouldBe("every Sat");
    }

    [Fact]
    public void Referencing_a_key_that_is_not_in_the_json_is_a_compile_error()
    {
        var (_, output, _) = RoslynHarness.RunLabelsGenerator(("labels.ar.json", SampleJson));
        var consumer = RoslynHarness.CreateCompilation("consumer", ("Consumer.cs", """
            using Shabana.Core.Resources;
            public static class Consumer
            {
                public static string Good => Labels.Common.Save;
                public static string BadKey => Labels.Common.DoesNotExist;
                public static string BadGroup => Labels.NoSuchGroup.Save;
            }
            """)).AddReferences(output.ToMetadataReference());
        var errors = consumer.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Id).ToList();
        // 'Labels.Common' has no 'DoesNotExist' and 'Labels' has no 'NoSuchGroup': both CS0117, and nothing else is wrong.
        errors.ShouldBe(["CS0117", "CS0117"]);
    }

    [Theory]
    [InlineData("{ \"A\": { \"b\": \"x\" ", "unterminated object")]
    [InlineData("{ \"A\": { \"b\": \"x\", } }", "trailing comma")]
    [InlineData("{ A: 1 }", "unquoted key")]
    [InlineData("{ \"A\": { \"b\": \"x\" } } trailing", "trailing garbage")]
    [InlineData("{ \"A\": { \"b\": \"line\nbreak\" } }", "raw newline in string")]
    [InlineData("", "empty file")]
    public void Malformed_json_reports_SHL0001_and_generates_nothing(string json, string why)
    {
        var (diagnostics, source) = Run(json);
        diagnostics.ErrorIds.ShouldContain("SHL0001", why);
        source.ShouldBeNull();
    }

    [Fact]
    public void Duplicate_keys_are_an_error()
    {
        Run("""{ "A": { "b": "x", "b": "y" } }""").Diagnostics.ErrorIds.ShouldContain("SHL0002");
        Run("""{ "A": { "b": "x" }, "A": { "c": "y" } }""").Diagnostics.ErrorIds.ShouldContain("SHL0002");
    }

    [Fact]
    public void Two_json_keys_mapping_to_the_same_csharp_name_are_an_error()
    {
        Run("""{ "A": { "a_b": "x", "aB": "y" } }""").Diagnostics.ErrorIds.ShouldContain("SHL0002");
    }

    [Fact]
    public void A_non_string_value_is_a_structure_error()
    {
        Run("""{ "A": { "b": 5 } }""").Diagnostics.ErrorIds.ShouldContain("SHL0004");
        Run("""{ "A": [] }""").Diagnostics.ErrorIds.ShouldContain("SHL0004");
    }

    [Fact]
    public void Template_defects_are_errors()
    {
        const string tpl = """{ "key": "A.b", "params": ["n"], "source": "b: (n: number) => x" }""";
        Run($$"""{ "_templates": [ {{tpl}} ], "A": { "b": "{1} out of range" } }""").Diagnostics.ErrorIds.ShouldContain("SHL0003");
        Run($$"""{ "_templates": [ {{tpl}} ], "A": { "b": "stray { brace" } }""").Diagnostics.ErrorIds.ShouldContain("SHL0003");
        Run("""{ "_templates": [ { "key": "A.zzz", "params": ["n"], "source": "z: (n: number) => x" } ], "A": { "b": "{0}" } }""").Diagnostics.ErrorIds.ShouldContain("SHL0003");
        Run("""{ "_templates": [ { "key": "A.b", "params": ["n"] } ], "A": { "b": "{0}" } }""").Diagnostics.ErrorIds.ShouldContain("SHL0003"); // no types
        Run("""{ "_templates": [ { "key": "A.b", "params": ["n"], "source": "b: (n: number) => x", "variants": [ { "when": "n > 1", "text": "t" } ] } ], "A": { "b": "{0}" } }""").Diagnostics.ErrorIds.ShouldContain("SHL0003");
    }

    [Fact]
    public void A_placeholder_in_a_label_that_is_not_registered_as_a_template_is_an_error()
    {
        Run("""{ "A": { "b": "has {0} but no template" } }""").Diagnostics.ErrorIds.ShouldContain("SHL0005");
    }

    [Fact]
    public void Generator_is_silent_when_the_project_has_no_labels_files()
    {
        var (diagnostics, _, source) = RoslynHarness.RunLabelsGenerator(("something.json", "not even json"));
        diagnostics.ShouldBeEmpty();
        source.ShouldBeNull();
    }

    [Fact]
    public void Multiple_labels_files_are_merged_and_a_cross_file_duplicate_is_an_error()
    {
        var (diagnostics, _, source) = RoslynHarness.RunLabelsGenerator(
            ("labels.ar.json", """{ "A": { "x": "1" } }"""),
            ("labels.native.ar.json", """{ "B": { "y": "2" } }"""));
        diagnostics.ShouldBeEmpty();
        source.ShouldNotBeNull();
        source.ShouldContain("public const string X", Case.Sensitive);
        source.ShouldContain("public const string Y", Case.Sensitive);

        var (dup, _, _) = RoslynHarness.RunLabelsGenerator(
            ("labels.ar.json", """{ "A": { "x": "1" } }"""),
            ("labels.native.ar.json", """{ "A": { "x": "2" } }"""));
        dup.Select(d => d.Id).ShouldContain("SHL0002");
    }

    // ---- The real Core/Resources/labels.ar.json, through the real generated Labels class ----

    private static JsonDocument RealJson() =>
        JsonDocument.Parse(TestPaths.ReadText(Path.Combine(TestPaths.NativeDir, "src", "Shabana.Core", "Resources", "labels.ar.json")));

    private static IEnumerable<Type> GroupClasses() =>
        typeof(Labels).GetNestedTypes(BindingFlags.Public).Where(t => t.GetField("ByKey") is not null);

    private static IReadOnlyDictionary<string, string> ByKey(Type t) =>
        (IReadOnlyDictionary<string, string>)t.GetField("ByKey")!.GetValue(null)!;

    [Fact]
    public void Every_string_in_the_real_json_is_a_generated_member()
    {
        using var doc = RealJson();
        var groups = 0;
        var strings = 0;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name == "_templates")
            {
                continue;
            }

            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                strings++;
                continue;
            }

            var expected = prop.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
            var matches = GroupClasses().Where(t => ByKey(t).SequenceEqual(expected)).ToList();
            matches.Count.ShouldBe(1, $"group {prop.Name} must map to exactly one generated class");
            groups++;
            strings += expected.Count;
        }

        // + the groups of labels.native.ar.json (ERR, MONTH, CURRENCY_*, ...), which are generated into the same Labels class
        using var native = JsonDocument.Parse(TestPaths.ReadText(Path.Combine(TestPaths.NativeDir, "src", "Shabana.Core", "Resources", "labels.native.ar.json")));
        GroupClasses().Count().ShouldBe(groups + native.RootElement.EnumerateObject().Count());
        strings.ShouldBeGreaterThan(1000);
        Labels.AppName.ShouldBe(doc.RootElement.GetProperty("APP_NAME").GetString());
        Labels.OrgName.ShouldBe(doc.RootElement.GetProperty("ORG_NAME").GetString());
    }

    [Fact]
    public void All_16_templates_are_generated_as_methods_and_format_every_argument()
    {
        using var doc = RealJson();
        var templates = doc.RootElement.GetProperty("_templates").EnumerateArray().ToList();
        templates.Count.ShouldBe(16);

        foreach (var t in templates)
        {
            var key = t.GetProperty("key").GetString()!;
            var parts = key.Split('.');
            var groupJson = doc.RootElement.GetProperty(parts[0]).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
            var owner = GroupClasses().Single(g => ByKey(g).SequenceEqual(groupJson));
            // The template's format string is exposed as const "<Method>Template"; that names the method unambiguously.
            var templateText = groupJson[parts[1]];
            var constant = owner.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Single(f => f.Name.EndsWith("Template", StringComparison.Ordinal) && (string?)f.GetRawConstantValue() == templateText && ByKey(owner)[parts[1]] == templateText
                             && string.Equals(f.Name, char.ToUpperInvariant(parts[1][0]) + parts[1][1..] + "Template", StringComparison.Ordinal));
            var method = owner.GetMethod(constant.Name[..^"Template".Length], BindingFlags.Public | BindingFlags.Static)!;
            method.GetParameters().Select(p => p.Name).ShouldBe(t.GetProperty("params").EnumerateArray().Select(p => p.GetString()));

            var args = method.GetParameters().Select(p => p.ParameterType == typeof(long) ? (object)4567L : "سس").ToArray();
            var text = (string)method.Invoke(null, args)!;
            text.ShouldNotContain("{");
            text.ShouldNotContain("4567"); // Latin digits never leak
            if (args.Any(a => a is long))
            {
                text.ShouldContain("٤٬٥٦٧"); // ٤٬٥٦٧
            }
        }
    }

    [Fact]
    public void Real_templates_match_the_web_behaviour()
    {
        Labels.Table.ShowingCount(12, 1234).ShouldBe("عرض ١٢ من ١٬٢٣٤");
        Labels.Expenses.DueBannerTitle(1).ShouldBe("مصروف متكرر مستحق");
        Labels.Expenses.DueBannerTitle(3).ShouldBe("٣ مصروفات متكررة مستحقة");
        Labels.Expenses.DueBannerTitle(0).ShouldBe("٠ مصروفات متكررة مستحقة");
        Labels.Common.Save.ShouldBe("حفظ");
    }
}
