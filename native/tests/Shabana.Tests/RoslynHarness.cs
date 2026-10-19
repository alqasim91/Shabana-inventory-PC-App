using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Shabana.Tests;

internal sealed class InMemoryAdditionalText(string path, string text) : AdditionalText
{
    public override string Path { get; } = path;

    public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text, Encoding.UTF8);
}

internal static class RoslynHarness
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        // Shabana.Core is in the test host's TPA and already defines Labels; leaving it out keeps the in-memory Labels unambiguous.
        return [.. tpa.Split(Path.PathSeparator)
            .Where(p => !Path.GetFileName(p).StartsWith("Shabana.", StringComparison.Ordinal))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))];
    });

    public static CSharpCompilation CreateCompilation(string assemblyName, params (string Path, string Text)[] sources) =>
        CSharpCompilation.Create(
            assemblyName,
            sources.Select(s => CSharpSyntaxTree.ParseText(s.Text, path: s.Path)),
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    /// <summary>Runs <see cref="Shabana.LabelsGen.LabelsGenerator"/> on in-memory labels files.</summary>
    public static (ImmutableArray<Diagnostic> Diagnostics, Compilation Output, string? Source) RunLabelsGenerator(params (string Path, string Text)[] files)
    {
        var generator = new Shabana.LabelsGen.LabelsGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [generator.AsSourceGenerator()],
            additionalTexts: files.Select(f => (AdditionalText)new InMemoryAdditionalText(f.Path, f.Text)));
        var input = CreateCompilation("generated-input");
        driver = driver.RunGeneratorsAndUpdateCompilation(input, out var output, out var diagnostics);
        var source = output.SyntaxTrees.FirstOrDefault(t => t.FilePath.EndsWith("Labels.g.cs", StringComparison.Ordinal))?.GetText().ToString();
        return (diagnostics, output, source);
    }

    /// <summary>Emits a compilation to memory and loads it; fails the test with the compiler errors otherwise.</summary>
    public static Assembly Load(Compilation compilation)
    {
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        }

        return Assembly.Load(stream.ToArray());
    }
}
