using System.ComponentModel;
using System.Reflection;

namespace Configlue.Tests.PublicApi;

/// <summary>
/// Guards the audience ownership model from <c>docs/public-api-audiences.md</c>.
/// Namespace growth and missing IntelliSense hiding fail here before they can
/// become accidental compatibility commitments.
/// </summary>
public sealed class ApiAudienceOwnershipTests
{
    private static readonly string[] AbstractionNamespaces =
    [
        "Configlue",
        "Configlue.Codecs",
        "Configlue.CompilerServices",
        "Configlue.Extensibility",
        "Configlue.Migrations",
        "Configlue.Resources",
        "Configlue.Sources",
        "Configlue.State",
        "Configlue.Transformers",
    ];

    private static readonly string[] CoreNamespaces =
    [
        "Configlue",
        "Configlue.CompilerServices",
        "Configlue.Extensibility",
        "Configlue.Resource.Zip",
        "Configlue.Resources",
        "Configlue.Source.Presets",
        "Configlue.State",
    ];

    private static readonly string[] LowLevelSpiNamespaces =
    [
        "Configlue.Codecs",
        "Configlue.Migrations",
        "Configlue.Resources",
        "Configlue.Sources",
        "Configlue.State",
        "Configlue.Transformers",
    ];

    [Test]
    public void AbstractionNamespacesStayClosed()
    {
        var offenders = AbstractionAssembly()
            .GetExportedTypes()
            .Select(static type => type.Namespace ?? string.Empty)
            .Distinct()
            .Where(static namespaceName => !AbstractionNamespaces.Contains(namespaceName))
            .OrderBy(static namespaceName => namespaceName)
            .ToArray();
        offenders.ShouldBeEmpty();
    }

    [Test]
    public void CoreNamespacesStayClosed()
    {
        var offenders = CoreAssembly()
            .GetExportedTypes()
            .Select(static type => type.Namespace ?? string.Empty)
            .Distinct()
            .Where(static namespaceName => !CoreNamespaces.Contains(namespaceName))
            .OrderBy(static namespaceName => namespaceName)
            .ToArray();
        offenders.ShouldBeEmpty();
    }

    [Test]
    public void CompilerServicesTypesStayHiddenFromOrdinaryCompletion()
    {
        var offenders = CompilerServicesTypes()
            .Where(static type => !HasBrowsableHiding(type))
            .Select(static type => type.FullName ?? type.Name)
            .OrderBy(static name => name)
            .ToArray();
        offenders.ShouldBeEmpty();
    }

    [Test]
    public void ProviderSpiTypesStayHiddenFromOrdinaryCompletion()
    {
        var offenders = AbstractionAssembly()
            .GetExportedTypes()
            .Concat(CoreAssembly().GetExportedTypes())
            .Where(static type => type.Namespace == "Configlue.Extensibility")
            .Where(static type => !HasBrowsableHiding(type))
            .Select(static type => type.FullName ?? type.Name)
            .OrderBy(static name => name)
            .ToArray();
        offenders.ShouldBeEmpty();
    }

    [Test]
    public void LowLevelSpiNamespacesStayHiddenFromOrdinaryCompletion()
    {
        var offenders = AbstractionAssembly()
            .GetExportedTypes()
            .Where(static type => LowLevelSpiNamespaces.Contains(type.Namespace))
            .Where(static type => !HasBrowsableHiding(type))
            .Select(static type => type.FullName ?? type.Name)
            .OrderBy(static name => name)
            .ToArray();
        offenders.ShouldBeEmpty();
    }

    private static IEnumerable<Type> CompilerServicesTypes() =>
        AbstractionAssembly()
            .GetExportedTypes()
            .Concat(CoreAssembly().GetExportedTypes())
            .Where(static type => type.Namespace == "Configlue.CompilerServices");

    private static Assembly AbstractionAssembly() => typeof(ConfiglueModelAttribute).Assembly;

    private static Assembly CoreAssembly() => typeof(ConfiglueApp).Assembly;

    private static bool HasBrowsableHiding(MemberInfo member) =>
        member.GetCustomAttribute<EditorBrowsableAttribute>()
            is { State: not EditorBrowsableState.Always };
}
