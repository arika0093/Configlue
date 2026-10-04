using Configlue;
using SparseFragments;

namespace SetClones.Consumer;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length == 1)
        {
            // Run a separately compiled netstandard2.0 consumer using this executable's
            // framework runtime and dependencies, so its generated casts execute too.
            var consumer = System.Reflection.Assembly.LoadFile(System.IO.Path.GetFullPath(args[0]));
            consumer
                .GetType(typeof(Program).FullName!)!
                .GetMethod(nameof(Main))!
                .Invoke(null, new object[] { Array.Empty<string>() });
            return;
        }
        // Representative first-class set shape (issue #280): HashSet comparer
        // semantics with mutable/read-only aliases. SortedSet and other
        // specialized containers are intentionally unsupported.
        ISet<string> source = new PortableHashSet<string>(StringComparer.OrdinalIgnoreCase);
        source.Add("Z");
        source.Add("a");
        var config = new ConfigSettings
        {
            Mutable = source,
            ReadOnly = (IReadOnlySet<string>)source,
        };
        var configClone = config.DeepClone().DeepClone();
        Verify(source, configClone.Mutable, configClone.ReadOnly);
        var sparse = new SparseSettings
        {
            Mutable = source,
            ReadOnly = (IReadOnlySet<string>)source,
        };
        var sparseClone = sparse.DeepClone().DeepClone();
        Verify(source, sparseClone.Mutable, sparseClone.ReadOnly);
        Console.WriteLine("Both generated set clone consumers passed.");
    }

    private static void Verify(
        ISet<string> source,
        ISet<string> mutable,
        IReadOnlySet<string> readOnly
    )
    {
        Require(ReferenceEquals(mutable, readOnly), "mutable/read-only alias");
        Require(!ReferenceEquals(source, mutable), "clone isolation");
        Require(readOnly.Count == 2 && readOnly.Contains("A"), "contents and comparer");
        Require(readOnly.SetEquals(new[] { "z", "A" }), "set comparer semantics");
        Require(mutable.Remove("A") && !readOnly.Contains("a"), "alias shares mutation");
        Require(source.Contains("a"), "original is unchanged");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

[ConfiglueModel("consumer-set-clone")]
public partial class ConfigSettings
{
    public ISet<string> Mutable { get; set; } = new PortableHashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> ReadOnly { get; set; } =
        new PortableHashSet<string>(StringComparer.Ordinal);
}

[SparseFragmentModel]
public partial class SparseSettings
{
    public IReadOnlySet<string> ReadOnly { get; set; } =
        new PortableHashSet<string>(StringComparer.Ordinal);
    public ISet<string> Mutable { get; set; } = new PortableHashSet<string>(StringComparer.Ordinal);
}

internal sealed class PortableHashSet<T> : HashSet<T>, IReadOnlySet<T>
{
    public PortableHashSet(IEqualityComparer<T> comparer)
        : base(comparer) { }
}
