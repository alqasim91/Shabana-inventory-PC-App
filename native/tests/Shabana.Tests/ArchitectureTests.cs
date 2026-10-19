using System.Reflection;
using System.Xml.Linq;

namespace Shabana.Tests;

/// <summary>
/// P0 acceptance (5): the project-reference rules of 04 section 3, enforced two ways.
///   * csproj level (every project, including the WPF app that a net10.0 test host cannot load): ProjectReference + PackageReference.
///   * assembly level (Core and Data, which can be loaded here): the compiled references, which also catches transitive leaks.
/// 04: Core references nothing of ours; Data -> Core; App -> Core, Data; Tests -> everything; the tools (generator, analyzer)
/// reference nothing of ours and are consumed by src projects only as analyzers (never as runtime references).
/// </summary>
public class ArchitectureTests
{
    private static readonly Dictionary<string, string[]> AllowedProjectReferences = new()
    {
        ["Shabana.Core"] = [],
        ["Shabana.Data"] = ["Shabana.Core"],
        ["Shabana.App"] = ["Shabana.Core", "Shabana.Data"],
        ["Shabana.LabelsGen"] = [],
        ["Shabana.Analyzers"] = [],
        ["Shabana.Tests"] = ["Shabana.Core", "Shabana.Data", "Shabana.LabelsGen", "Shabana.Analyzers"],
    };

    // Packages a project must never take (04 section 3: "Core may reference System.Data.Common and Dapper but never Microsoft.Data.Sqlite").
    private static readonly Dictionary<string, string[]> ForbiddenPackages = new()
    {
        ["Shabana.Core"] = ["Microsoft.Data.Sqlite", "Microsoft.Data.Sqlite.Core", "SQLitePCLRaw", "CommunityToolkit.Mvvm"],
        ["Shabana.Data"] = ["CommunityToolkit.Mvvm"],
    };

    private static IEnumerable<string> ProjectFiles() =>
        Directory.EnumerateFiles(TestPaths.NativeDir, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    private static string Name(string csproj) => Path.GetFileNameWithoutExtension(csproj);

    private static XDocument Load(string csproj) => XDocument.Load(csproj);

    /// <summary>ProjectReference items of the csproj plus those contributed by a src/Directory.Build.props.</summary>
    private static IEnumerable<(string Name, bool IsAnalyzerOnly)> References(string csproj)
    {
        var docs = new List<XDocument> { Load(csproj) };
        if (csproj.Contains($"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}"))
        {
            docs.Add(Load(Path.Combine(TestPaths.NativeDir, "src", "Directory.Build.props")));
        }

        return docs.SelectMany(d => d.Descendants("ProjectReference"))
            .Select(e => (
                Name: Path.GetFileNameWithoutExtension(((string)e.Attribute("Include")!).Replace('\\', '/')),
                IsAnalyzerOnly: (string?)e.Attribute("OutputItemType") == "Analyzer" && (string?)e.Attribute("ReferenceOutputAssembly") == "false"));
    }

    [Fact]
    public void Every_project_in_the_solution_folder_has_a_rule()
    {
        ProjectFiles().Select(Name).Order().ShouldBe(AllowedProjectReferences.Keys.Order(),
            "add a rule to AllowedProjectReferences when you add a project (and decide what it may reference)");
    }

    [Fact]
    public void Solution_lists_exactly_these_projects()
    {
        var sln = TestPaths.ReadText(Path.Combine(TestPaths.NativeDir, "Shabana.sln"));
        foreach (var name in AllowedProjectReferences.Keys)
        {
            sln.ShouldContain($"\"{name}\"", Case.Sensitive);
        }
    }

    [Fact]
    public void Project_references_follow_the_dependency_rule()
    {
        foreach (var csproj in ProjectFiles())
        {
            var name = Name(csproj);
            var allowed = AllowedProjectReferences[name];
            var shipping = name is "Shabana.Core" or "Shabana.Data" or "Shabana.App";
            var actual = References(csproj)
                .Where(r => !(shipping && r.IsAnalyzerOnly)) // compile-time-only analyzer/generator references are checked separately
                .Select(r => r.Name)
                .Distinct()
                .Order();

            // Shipping projects consume the analyzer / generator ONLY as analyzers (compile-time, no runtime reference).
            actual.ShouldBe(allowed.Order(), $"{name} references");
        }
    }

    [Fact]
    public void Tools_are_only_consumed_by_src_projects_as_analyzers_never_as_runtime_references()
    {
        foreach (var csproj in ProjectFiles().Where(f => Name(f) is "Shabana.Core" or "Shabana.Data" or "Shabana.App"))
        {
            foreach (var r in References(csproj).Where(r => r.Name is "Shabana.Analyzers" or "Shabana.LabelsGen"))
            {
                r.IsAnalyzerOnly.ShouldBeTrue($"{Name(csproj)} -> {r.Name} must be OutputItemType=Analyzer, ReferenceOutputAssembly=false");
            }
        }
    }

    [Fact]
    public void Core_has_the_labels_generator_and_the_other_shipping_projects_have_the_analyzer()
    {
        References(ProjectFiles().Single(f => Name(f) == "Shabana.Core")).Select(r => r.Name).ShouldContain("Shabana.LabelsGen");
        foreach (var n in new[] { "Shabana.Core", "Shabana.Data", "Shabana.App" })
        {
            References(ProjectFiles().Single(f => Name(f) == n)).Select(r => r.Name).ShouldContain("Shabana.Analyzers");
        }
    }

    [Fact]
    public void Forbidden_packages_are_not_referenced()
    {
        foreach (var (project, banned) in ForbiddenPackages)
        {
            var csproj = ProjectFiles().Single(f => Name(f) == project);
            var packages = Load(csproj).Descendants("PackageReference").Select(e => (string)e.Attribute("Include")!).ToList();
            packages.Any(p => banned.Any(b => p.StartsWith(b, StringComparison.OrdinalIgnoreCase)))
                .ShouldBeFalse($"{project} packages: {string.Join(", ", packages)}");
        }
    }

    [Fact]
    public void Platform_and_windows_rules_for_target_frameworks()
    {
        string Tfm(string project) => (string)Load(ProjectFiles().Single(f => Name(f) == project)).Descendants("TargetFramework").Single();

        Tfm("Shabana.Core").ShouldBe("net10.0");
        Tfm("Shabana.Data").ShouldBe("net10.0");
        Tfm("Shabana.Tests").ShouldBe("net10.0");
        Tfm("Shabana.App").ShouldBe("net10.0-windows");
        Tfm("Shabana.LabelsGen").ShouldBe("netstandard2.0");
        Tfm("Shabana.Analyzers").ShouldBe("netstandard2.0");
    }

    // ---- Compiled assemblies ----

    private static IEnumerable<string> ReferencedNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name!);

    [Fact]
    public void Compiled_Core_references_nothing_of_ours_nor_sqlite_nor_wpf()
    {
        var core = typeof(Shabana.Core.Resources.Labels).Assembly;
        var refs = ReferencedNames(core).ToList();
        refs.Where(n => n.StartsWith("Shabana.", StringComparison.Ordinal)).ShouldBeEmpty();
        refs.Where(n => n.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) || n.StartsWith("SQLitePCL", StringComparison.Ordinal)).ShouldBeEmpty();
        refs.Where(n => n is "PresentationFramework" or "PresentationCore" or "WindowsBase" or "System.Windows.Forms").ShouldBeEmpty();
    }

    [Fact]
    public void Compiled_Data_references_only_Core_among_ours_and_no_wpf()
    {
        var data = typeof(Shabana.Data.Migrations.EmbeddedSql).Assembly;
        var ours = ReferencedNames(data).Where(n => n.StartsWith("Shabana.", StringComparison.Ordinal)).ToList();
        // (the compiler drops an unused reference, so "subset of {Core}" is the strongest assertion available while Data is a skeleton)
        ours.Except(["Shabana.Core"]).ShouldBeEmpty();
        ReferencedNames(data).Where(n => n is "PresentationFramework" or "PresentationCore" or "WindowsBase").ShouldBeEmpty();
    }

    [Fact]
    public void Analyzer_and_generator_assemblies_reference_no_app_assemblies()
    {
        foreach (var asm in new[] { typeof(Shabana.Analyzers.NoArabicLiteralAnalyzer).Assembly, typeof(Shabana.LabelsGen.LabelsGenerator).Assembly })
        {
            ReferencedNames(asm).Where(n => n.StartsWith("Shabana.", StringComparison.Ordinal)).ShouldBeEmpty();
        }
    }

    [Fact]
    public void Test_host_cannot_see_the_app_assembly_so_its_rules_are_csproj_based()
    {
        // Documented limitation: Shabana.App is net10.0-windows (WPF) and is not referenced by this net10.0 test project.
        // Its dependency rule is enforced by Project_references_follow_the_dependency_rule above.
        AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "Shabana.App").ShouldBeEmpty();
    }
}
