using System.Reflection;
using System.Reflection.Emit;
using System.Xml.Linq;
using Configlue.Source.Presets;

namespace Configlue.Tests;

/// <summary>
/// Guards the #260 ownership model: file-source composition (physical
/// <see cref="FileResource"/> construction plus codec/section composition) is
/// owned by the standard Configlue layer, while format providers contribute only
/// codecs and format document/section semantics.
/// </summary>
public sealed class FileSourceOwnershipTests
{
    private static readonly string[] ProviderAssemblyNames =
    [
        "Configlue.Provider.Json",
        "Configlue.Provider.Yaml",
        "Configlue.Provider.Xml",
        "Configlue.Provider.MessagePack",
    ];

    private static readonly string[] StandardFileResourceSources =
    [
        "FileResource.cs",
        "FileResource.Backups.cs",
        "FileResource.Locks.cs",
        "FileResource.Persistence.cs",
        "FileResource.Watching.cs",
    ];

    [Test]
    public void CoreAssemblyNeedsNoConcreteFileStorage()
    {
        typeof(ConfiglueApp).Assembly.GetType("Configlue.Resources.FileResource").ShouldBeNull();
    }

    [Test]
    public void FileResourceIsOwnedByStandardLayer()
    {
        typeof(FileResource).Assembly.ShouldBe(typeof(CommonSourceBuilder).Assembly);
    }

    [Test]
    public void FileResourceSourcesLiveInStandardLayer()
    {
        var repositoryRoot = FindRepositoryRoot();
        var standardResources = Path.Combine(
            repositoryRoot,
            "src",
            "basic",
            "Configlue",
            "Resources"
        );
        foreach (var fileName in StandardFileResourceSources)
        {
            File.Exists(Path.Combine(standardResources, fileName)).ShouldBeTrue();
        }

        var coreResources = Path.Combine(
            repositoryRoot,
            "src",
            "basic",
            "Configlue.Core",
            "Resources"
        );
        foreach (var fileName in StandardFileResourceSources)
        {
            File.Exists(Path.Combine(coreResources, fileName)).ShouldBeFalse();
        }

        foreach (var provider in new[] { "Json", "Yaml", "Xml", "MessagePack" })
        {
            var providerDirectory = Path.Combine(
                repositoryRoot,
                "src",
                "providers",
                $"Configlue.Provider.{provider}"
            );
            foreach (var fileName in StandardFileResourceSources)
            {
                Directory
                    .GetFiles(providerDirectory, fileName, SearchOption.AllDirectories)
                    .ShouldBeEmpty();
            }
        }
    }

    [Test]
    public void ProjectReferenceGraphRespectsFileOwnership()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceDirectory = Path.Combine(repositoryRoot, "src");
        var projects = Directory.GetFiles(sourceDirectory, "*.csproj", SearchOption.AllDirectories);
        var edges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            var relative = Path.GetRelativePath(repositoryRoot, project)
                .Replace(Path.DirectorySeparatorChar, '/');
            edges[relative] = GetProjectReferences(project, repositoryRoot);
        }

        const string standard = "src/basic/Configlue/Configlue.csproj";
        const string core = "src/basic/Configlue.Core/Configlue.Core.csproj";
        const string json = "src/providers/Configlue.Provider.Json/Configlue.Provider.Json.csproj";
        const string yaml = "src/providers/Configlue.Provider.Yaml/Configlue.Provider.Yaml.csproj";
        const string xml = "src/providers/Configlue.Provider.Xml/Configlue.Provider.Xml.csproj";
        const string messagePack =
            "src/providers/Configlue.Provider.MessagePack/Configlue.Provider.MessagePack.csproj";

        // Optional providers delegate to the standard composition, so they reference it.
        edges[yaml].ShouldContain(standard);
        edges[xml].ShouldContain(standard);
        edges[messagePack].ShouldContain(standard);

        // JSON file composition is part of the standard experience, so the JSON
        // provider must not reference the standard layer back (no cycle).
        edges[json].ShouldNotContain(standard);
        edges[standard].ShouldNotContain(yaml);
        edges[standard].ShouldNotContain(xml);
        edges[standard].ShouldNotContain(messagePack);

        // The provider-neutral runtime stays below the standard layer.
        edges[core].ShouldNotContain(standard);

        // The whole in-repo graph stays acyclic.
        var failures = FindCycles(edges);
        failures.ShouldBeEmpty();
    }

    [Test]
    public void ProviderAssembliesDoNotConstructFileResource()
    {
        var fileResourceType = typeof(FileResource);
        var scanned = new List<string>();
        var violations = new List<string>();
        foreach (var assemblyName in ProviderAssemblyNames)
        {
            var assembly = FindLoadedAssembly(assemblyName) ?? LoadFromOutput(assemblyName);
            if (assembly is null)
            {
                continue;
            }

            ScanAssembly(assembly, fileResourceType, violations);
            scanned.Add(assemblyName);
        }

        scanned.ShouldNotBeEmpty();
        violations.ShouldBeEmpty();
    }

    private static Assembly? FindLoadedAssembly(string assemblyName) =>
        AppDomain
            .CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, assemblyName, StringComparison.Ordinal)
            );

    private static Assembly? LoadFromOutput(string assemblyName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        return File.Exists(path) ? System.Reflection.Assembly.LoadFrom(path) : null;
    }

    private static void ScanAssembly(
        Assembly assembly,
        Type fileResourceType,
        List<string> violations
    )
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            types = exception.Types.Where(static type => type is not null).ToArray()!;
        }

        const BindingFlags flags =
            BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.DeclaredOnly;
        foreach (var type in types)
        {
            var owner = $"{assembly.GetName().Name}:{type.FullName}";
            CheckTypeReference(type.BaseType, fileResourceType, violations, owner);
            foreach (var implemented in type.GetInterfaces())
            {
                CheckTypeReference(implemented, fileResourceType, violations, owner);
            }

            foreach (var field in type.GetFields(flags))
            {
                CheckTypeReference(field.FieldType, fileResourceType, violations, owner);
            }

            foreach (var property in type.GetProperties(flags))
            {
                CheckTypeReference(property.PropertyType, fileResourceType, violations, owner);
            }

            foreach (var constructor in type.GetConstructors(flags))
            {
                ScanMethod(constructor, fileResourceType, violations, owner);
            }

            foreach (var method in type.GetMethods(flags))
            {
                ScanMethod(method, fileResourceType, violations, owner);
            }
        }
    }

    private static void ScanMethod(
        MethodBase method,
        Type fileResourceType,
        List<string> violations,
        string owner
    )
    {
        var scopedOwner = $"{owner}.{method.Name}";
        if (method is MethodInfo methodInfo)
        {
            CheckTypeReference(methodInfo.ReturnType, fileResourceType, violations, scopedOwner);
        }

        foreach (var parameter in method.GetParameters())
        {
            CheckTypeReference(parameter.ParameterType, fileResourceType, violations, scopedOwner);
        }

        MethodBody? body;
        try
        {
            body = method.GetMethodBody();
        }
        catch
        {
            return;
        }

        if (body is null)
        {
            return;
        }

        foreach (var local in body.LocalVariables)
        {
            CheckTypeReference(local.LocalType, fileResourceType, violations, scopedOwner);
        }

        ScanInstructions(method, body, fileResourceType, violations, scopedOwner);
    }

    private static void ScanInstructions(
        MethodBase method,
        MethodBody body,
        Type fileResourceType,
        List<string> violations,
        string owner
    )
    {
        var instructions = body.GetILAsByteArray();
        if (instructions is null)
        {
            return;
        }

        var (oneByte, twoByte) = OpCodeMaps.Value;
        var module = method.Module;
        var declaringTypeArguments = method.DeclaringType?.GetGenericArguments();
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var position = 0;
        while (position < instructions.Length)
        {
            var code = instructions[position++];
            OpCode operation;
            if (code == 0xFE)
            {
                var fallback = twoByte[instructions[position++]];
                if (fallback is null)
                {
                    return;
                }

                operation = fallback.Value;
            }
            else
            {
                var fallback = oneByte[code];
                if (fallback is null)
                {
                    return;
                }

                operation = fallback.Value;
            }

            if (operation.OperandType == OperandType.InlineSwitch)
            {
                var targetCount = BitConverter.ToInt32(instructions, position);
                position += 4 + (4 * targetCount);
                continue;
            }

            var operandSize = operation.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget => 1,
                OperandType.ShortInlineI => 1,
                OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineBrTarget => 4,
                OperandType.InlineField => 4,
                OperandType.InlineI => 4,
                OperandType.InlineMethod => 4,
                OperandType.InlineSig => 4,
                OperandType.InlineString => 4,
                OperandType.InlineTok => 4,
                OperandType.InlineType => 4,
                OperandType.ShortInlineR => 4,
                OperandType.InlineI8 => 8,
                OperandType.InlineR => 8,
                _ => 0,
            };
            if (
                operation.OperandType
                is OperandType.InlineMethod
                    or OperandType.InlineField
                    or OperandType.InlineType
                    or OperandType.InlineTok
            )
            {
                var token = BitConverter.ToInt32(instructions, position);
                try
                {
                    MemberInfo? member;
                    try
                    {
                        member = module.ResolveMember(
                            token,
                            declaringTypeArguments,
                            methodArguments
                        );
                    }
                    catch
                    {
                        member = module.ResolveMember(token);
                    }

                    CheckMember(member, fileResourceType, violations, owner);
                }
                catch
                {
                    // Unresolvable tokens (for example open generics) carry no
                    // concrete FileResource reference; signature scanning covers types.
                }
            }

            position += operandSize;
        }
    }

    private static void CheckMember(
        MemberInfo? member,
        Type fileResourceType,
        List<string> violations,
        string owner
    )
    {
        switch (member)
        {
            case MethodBase called when called.DeclaringType == fileResourceType:
                violations.Add($"{owner} calls {fileResourceType.FullName}.{called.Name}");
                break;
            case FieldInfo field when field.DeclaringType == fileResourceType:
                violations.Add($"{owner} reads {fileResourceType.FullName}.{field.Name}");
                break;
            case Type referenced:
                CheckTypeReference(referenced, fileResourceType, violations, owner);
                break;
            default:
                break;
        }
    }

    private static void CheckTypeReference(
        Type? type,
        Type fileResourceType,
        List<string> violations,
        string owner
    )
    {
        if (type is null)
        {
            return;
        }

        if (type == fileResourceType)
        {
            violations.Add($"{owner} references {fileResourceType.FullName}");
            return;
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                CheckTypeReference(argument, fileResourceType, violations, owner);
            }
        }

        if (type.HasElementType)
        {
            CheckTypeReference(type.GetElementType(), fileResourceType, violations, owner);
        }
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

    private static List<string> FindCycles(Dictionary<string, List<string>> edges)
    {
        var failures = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inProgress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in edges.Keys)
        {
            Visit(project, edges, visited, inProgress, new Stack<string>(), failures);
        }

        return failures;
    }

    private static void Visit(
        string project,
        Dictionary<string, List<string>> edges,
        HashSet<string> visited,
        HashSet<string> inProgress,
        Stack<string> trail,
        List<string> failures
    )
    {
        if (!inProgress.Add(project))
        {
            failures.Add(
                $"{project} participates in a cycle: {string.Join(" -> ", trail.Reverse())} -> {project}"
            );
            return;
        }

        if (!visited.Add(project))
        {
            inProgress.Remove(project);
            return;
        }

        trail.Push(project);
        if (edges.TryGetValue(project, out var references))
        {
            foreach (var reference in references)
            {
                if (edges.ContainsKey(reference))
                {
                    Visit(reference, edges, visited, inProgress, trail, failures);
                }
            }
        }

        trail.Pop();
        inProgress.Remove(project);
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

    private static readonly Lazy<(OpCode?[] OneByte, OpCode?[] TwoByte)> OpCodeMaps = new(
        BuildOpCodeMaps
    );

    private static (OpCode?[] OneByte, OpCode?[] TwoByte) BuildOpCodeMaps()
    {
        var oneByte = new OpCode?[256];
        var twoByte = new OpCode?[256];
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode operation)
            {
                continue;
            }

            var value = unchecked((ushort)operation.Value);
            if ((value & 0xFE00) == 0xFE00)
            {
                twoByte[value & 0xFF] = operation;
            }
            else
            {
                oneByte[value & 0xFF] = operation;
            }
        }

        return (oneByte, twoByte);
    }
}
