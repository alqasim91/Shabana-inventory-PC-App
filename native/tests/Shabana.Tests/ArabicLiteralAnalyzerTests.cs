using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Shabana.Analyzers;

namespace Shabana.Tests;

/// <summary>P0 acceptance (6): SHB0001 "no Arabic literal outside the labels JSON" fires on violations and stays quiet on clean code.</summary>
public class ArabicLiteralAnalyzerTests
{
    // Arabic text is written with escapes here so this test file itself stays readable in any editor.
    private const string Alef = "ا";

    private static async Task<IReadOnlyList<Diagnostic>> Analyze(
        (string Path, string Text)[] sources, params (string Path, string Text)[] additional)
    {
        var compilation = RoslynHarness.CreateCompilation("analyzed", sources);
        var options = new AnalyzerOptions([.. additional.Select(a => (AdditionalText)new InMemoryAdditionalText(a.Path, a.Text))]);
        var withAnalyzers = compilation.WithAnalyzers([new NoArabicLiteralAnalyzer()], options);
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.Where(d => d.Id == NoArabicLiteralAnalyzer.DiagnosticId)];
    }

    private static Task<IReadOnlyList<Diagnostic>> AnalyzeCs(string code, string path = "Sample.cs") => Analyze([(path, code)]);

    [Theory]
    [InlineData("""public class C { public const string S = "مرحبا"; }""", "plain string")]
    [InlineData("""public class C { public string S(int x) => $"عدد {x}"; }""", "interpolated string text")]
    [InlineData("""public class C { public char Ch = 'ب'; }""", "char literal")]
    [InlineData("""public class C { public string S = "ا"; }""", "unicode escape is decoded before the check")]
    [InlineData("public class C { public string S = \"\"\"\n    سلام\n    \"\"\"; }", "raw string")]
    [InlineData("""public class C { public string S = @"verbatim مرحبا"; }""", "verbatim string")]
    [InlineData("""public class C { public string S = "mixed English و عربي"; }""", "Arabic embedded in English text")]
    [InlineData("""public class C { public byte[] S => "مرحبا"u8.ToArray(); }""", "utf8 literal")]
    public async Task Fires_on_arabic_in_any_kind_of_literal(string code, string why)
    {
        var diagnostics = await AnalyzeCs(code);
        diagnostics.Count.ShouldBe(1, why);
        diagnostics[0].Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostics[0].Location.IsInSource.ShouldBeTrue();
    }

    [Theory]
    [InlineData('؀', true)]
    [InlineData('ۿ', true)]
    [InlineData('ݐ', true)]
    [InlineData('ݿ', true)]
    [InlineData('ﭐ', true)]
    [InlineData('﷿', true)]
    [InlineData('ﹰ', true)]
    [InlineData('﻿', true)]
    [InlineData('׿', false)] // just below: Hebrew
    [InlineData('܀', false)] // Syriac
    [InlineData('ݏ', false)]
    [InlineData('ހ', false)] // Thaana
    [InlineData('ﭏ', false)]
    [InlineData('︀', false)]
    [InlineData('﹯', false)]
    [InlineData('＀', false)]
    [InlineData('A', false)]
    [InlineData('7', false)]
    public async Task Range_boundaries_match_the_four_blocks_in_the_spec(char c, bool expected)
    {
        var code = $"public class C {{ public const string S = \"x\\u{(int)c:X4}y\"; }}";
        var diagnostics = await AnalyzeCs(code);
        diagnostics.Count.ShouldBe(expected ? 1 : 0, $"U+{(int)c:X4}");
        NoArabicLiteralAnalyzer.ContainsArabic(c.ToString()).ShouldBe(expected);
    }

    [Fact]
    public async Task Clean_code_produces_no_diagnostics()
    {
        const string clean = """
            using Shabana.Core.Resources;

            public class Clean
            {
                public const string English = "Save";
                public string Digits => "0123456789 ٠"[..1];
                public string FromLabels => Labels.Common.Save;
                public string Interp(int n) => $"count {n}";
                public char Ch = 'x';
                public string Escapes = "tab\t quote\" newline\n é";
            }
            """;
        // Shabana.Core is on the harness' reference list on purpose? No: it is excluded, so supply a stub Labels.
        var stub = ("Stub.cs", "namespace Shabana.Core.Resources { public static class Labels { public static class Common { public const string Save = \"Save\"; } } }");
        var diagnostics = await Analyze([("Clean.cs", clean.Replace(" ٠", string.Empty)), stub]);
        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task Arabic_in_comments_is_not_a_literal()
    {
        const string code = """
            // تعليق عربي
            /// <summary>وصف</summary>
            /* كتلة */
            public class C
            {
                public int X; // ملاحظة
            #region منطقة
            #endregion
            }
            """;
        (await AnalyzeCs(code)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Labels.g.cs")]
    [InlineData("Generated.generated.cs")]
    [InlineData("MainWindow.g.i.cs")]
    [InlineData("Form.designer.cs")]
    public async Task Generated_files_are_exempt_by_name(string path)
    {
        (await AnalyzeCs("""public class C { public const string S = "مرحبا"; }""", path)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Files_with_an_auto_generated_header_are_exempt()
    {
        const string code = "// <auto-generated>\n//   tool output\n// </auto-generated>\npublic class C { public const string S = \"مرحبا\"; }";
        (await AnalyzeCs(code, "Whatever.cs")).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_same_text_in_a_hand_written_file_is_reported_with_its_position()
    {
        var diagnostics = await AnalyzeCs("public class C\n{\n    public const string S = \"" + Alef + "\";\n}\n");
        diagnostics.Count.ShouldBe(1);
        diagnostics[0].Location.GetLineSpan().StartLinePosition.Line.ShouldBe(2);
    }

    // ---- XAML (passed to the analyzer as AdditionalFiles; see src/Directory.Build.props) ----

    private static Task<IReadOnlyList<Diagnostic>> AnalyzeXaml(string xaml, string path = "MainWindow.xaml") =>
        Analyze([("Empty.cs", "public class Empty { }")], (path, xaml));

    [Fact]
    public async Task Fires_on_arabic_in_xaml_attributes_and_text()
    {
        const string xaml = """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    Title="نافذة">
              <TextBlock>نص</TextBlock>
              <Button Content="{Binding Name}" ToolTip="تلميح" />
            </Window>
            """;
        var diagnostics = await AnalyzeXaml(xaml);
        diagnostics.Count.ShouldBe(3);
        diagnostics.Select(d => d.Location.GetLineSpan().StartLinePosition.Line).ShouldBe([1, 2, 3], ignoreOrder: true);
        diagnostics.ShouldAllBe(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Clean_xaml_and_arabic_xaml_comments_pass()
    {
        const string xaml = """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:res="clr-namespace:Shabana.Core.Resources;assembly=Shabana.Core"
                    xml:lang="ar-EG" FlowDirection="RightToLeft"
                    Title="{x:Static res:Labels.AppName}">
              <!-- تعليق مسموح -->
              <TextBlock Text="{x:Static res:Labels.OrgName}" />
            </Window>
            """;
        (await AnalyzeXaml(xaml)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Malformed_xaml_with_arabic_is_still_reported()
    {
        (await AnalyzeXaml("<Window Title=\"" + Alef + "\"><Unclosed>")).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Non_xaml_additional_files_such_as_the_labels_json_are_ignored()
    {
        var diagnostics = await Analyze([("Empty.cs", "public class Empty { }")], ("labels.ar.json", """{ "A": { "b": "مرحبا" } }"""));
        diagnostics.ShouldBeEmpty();
    }

    // ---- The real source tree ----

    [Fact]
    public void Shipping_projects_attach_the_analyzer()
    {
        var props = File.ReadAllText(Path.Combine(TestPaths.NativeDir, "src", "Directory.Build.props"));
        props.ShouldContain("Shabana.Analyzers.csproj");
        props.ShouldContain("OutputItemType=\"Analyzer\"");
        props.ShouldContain("**/*.xaml");
    }

    [Fact]
    public void No_hand_written_source_file_under_src_contains_arabic()
    {
        var src = Path.Combine(TestPaths.NativeDir, "src");
        var offenders = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => !f.EndsWith(".g.cs", StringComparison.Ordinal))
            .Where(f => NoArabicLiteralAnalyzer.ContainsArabic(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(src, f))
            .ToList();
        offenders.ShouldBeEmpty();
    }
}
