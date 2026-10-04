using System.Xml.Linq;

namespace Configlue.Tests;

/// <summary>
/// Guards the #261 package boundary: the default <c>Configlue</c> package stays
/// dependency-free (JSON + file + environment + common presets + SingleBinary)
/// while DI, HTTP, CommandLine, AES, external formats, and specialized backends
/// remain opt-ins.
/// </summary>
public sealed class PackageBoundaryTests
{
    private static readonly string[] ForbiddenStandardReferences =
    [
        "src/extensions/Configlue.Extensions.DI/Configlue.Extensions.DI.csproj",
        "src/sources/Configlue.Source.Http/Configlue.Source.Http.csproj",
        "src/sources/Configlue.Source.CommandLine/Configlue.Source.CommandLine.csproj",
        "src/transformers/Configlue.Transformer.AES/Configlue.Transformer.AES.csproj",
        "src/transformers/Configlue.Transformer.Compression/Configlue.Transformer.Compression.csproj",
        "src/providers/Configlue.Provider.Yaml/Configlue.Provider.Yaml.csproj",
        "src/providers/Configlue.Provider.Xml/Configlue.Provider.Xml.csproj",
        "src/providers/Configlue.Provider.MessagePack/Configlue.Provider.MessagePack.csproj",
    ];

    private static readonly string[] ForbiddenTransitivePackages =
    [
        "Configlue.Extensions.DI",
        "Configlue.Source.Http",
        "Configlue.Source.CommandLine",
        "Configlue.Transformer.AES",
        "Configlue.Transformer.Compression",
        "Configlue.Provider.Yaml",
        "Configlue.Provider.Xml",
        "Configlue.Provider.MessagePack",
        "SharpYaml",
        "MessagePack",
        "System.CommandLine",
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.Http",
        "Microsoft.Extensions.Hosting",
    ];

    private static readonly string[] RequiredStandardReferences =
    [
        "src/basic/Configlue.Core/Configlue.Core.csproj",
        "src/basic/Configlue.Abstraction/Configlue.Abstraction.csproj",
        "src/providers/Configlue.Provider.Json/Configlue.Provider.Json.csproj",
        "src/sources/Configlue.Source.Environment/Configlue.Source.Environment.csproj",
    ];

    [Test]
    public void StandardPackageReferencesOnlyDependencyFreeExperience()
    {
        var repositoryRoot = FindRepositoryRoot();
        var standard = Path.Combine(
            repositoryRoot,
            "src",
            "basic",
            "Configlue",
            "Configlue.csproj"
        );
        var references = GetProjectReferences(standard, repositoryRoot);

        foreach (var required in RequiredStandardReferences)
        {
            references.ShouldContain(required);
        }

        foreach (var forbidden in ForbiddenStandardReferences)
        {
            references.ShouldNotContain(forbidden);
        }
    }

    [Test]
    public void StandardPackageTransitiveClosureStaysDependencyFree()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceDirectory = Path.Combine(repositoryRoot, "src");
        var projects = Directory.GetFiles(sourceDirectory, "*.csproj", SearchOption.AllDirectories);
        var edges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var packageEdges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            var relative = Path.GetRelativePath(repositoryRoot, project)
                .Replace(Path.DirectorySeparatorChar, '/');
            edges[relative] = GetProjectReferences(project, repositoryRoot);
            packageEdges[relative] = GetPackageReferences(project);
        }

        const string standard = "src/basic/Configlue/Configlue.csproj";
        var reachable = CollectTransitiveProjects(standard, edges);
        reachable.ShouldNotContain(standard);

        foreach (var forbidden in ForbiddenStandardReferences)
        {
            reachable.ShouldNotContain(forbidden);
        }

        var transitivePackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        transitivePackages.UnionWith(packageEdges[standard]);
        foreach (var project in reachable)
        {
            if (packageEdges.TryGetValue(project, out var packages))
            {
                foreach (var package in packages)
                {
                    transitivePackages.Add(package);
                }
            }
        }

        foreach (var forbidden in ForbiddenTransitivePackages)
        {
            transitivePackages.ShouldNotContain(
                forbidden,
                $"Standard Configlue must not transitively bring '{forbidden}'."
            );
        }
    }

    [Test]
    public void StandardPackageSupportsBaselineTargetFrameworks()
    {
        var repositoryRoot = FindRepositoryRoot();
        var standard = Path.Combine(
            repositoryRoot,
            "src",
            "basic",
            "Configlue",
            "Configlue.csproj"
        );
        var document = XDocument.Load(standard);
        var frameworks =
            document
                .Descendants("TargetFrameworks")
                .Select(static element => element.Value)
                .FirstOrDefault()
            ?? document.Descendants("TargetFramework").Select(static e => e.Value).FirstOrDefault()
            ?? string.Empty;
        var list = frameworks
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => part.Trim())
            .Where(static part => part.Length > 0)
            .ToArray();
        list.ShouldContain("netstandard2.0");
        list.ShouldContain("netstandard2.1");
        list.ShouldContain("net10.0");
    }

    [Test]
    public void StandardPackageDescriptionPresentsPrimaryPackage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var standard = Path.Combine(
            repositoryRoot,
            "src",
            "basic",
            "Configlue",
            "Configlue.csproj"
        );
        var document = XDocument.Load(standard);
        var description =
            document.Descendants("PackageDescription").Select(static e => e.Value).FirstOrDefault()
            ?? string.Empty;
        description.ShouldNotBeNullOrWhiteSpace();
        description.ShouldNotContain("Convenience", Case.Insensitive);
    }

    private static HashSet<string> CollectTransitiveProjects(
        string root,
        Dictionary<string, List<string>> edges
    )
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>(edges.TryGetValue(root, out var direct) ? direct : []);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            if (edges.TryGetValue(current, out var next))
            {
                foreach (var child in next)
                {
                    stack.Push(child);
                }
            }
        }

        return visited;
    }

    private static List<string> GetProjectReferences(string projectPath, string repositoryRoot)
    {
        var document = XDocument.Load(projectPath);
        var directory = Path.GetDirectoryName(projectPath)!;
        var references = new List<string>();
        foreach (var element in document.Descendants("ProjectReference"))
        {
            var include = element.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            var resolved = Path.GetFullPath(Path.Combine(directory, include));
            if (!resolved.StartsWith(repositoryRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            references.Add(
                Path.GetRelativePath(repositoryRoot, resolved)
                    .Replace(Path.DirectorySeparatorChar, '/')
            );
        }

        return references;
    }

    private static List<string> GetPackageReferences(string projectPath)
    {
        var document = XDocument.Load(projectPath);
        var references = new List<string>();
        foreach (var element in document.Descendants("PackageReference"))
        {
            var include = element.Attribute("Include")?.Value;
            if (!string.IsNullOrWhiteSpace(include))
            {
                references.Add(include!);
            }
        }

        return references;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Configlue.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root from the test output directory."
        );
    }
}
