using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.Resources;

[MemoryDiagnoser]
public class SubjectIdentityBenchmarks
{
    private readonly string[] _segments = ["tenant/a", "e\u0301", "user-1"];
    private ConfiglueResourceContext _context;
    private ConfiglueResourceContext _equivalent;

    [GlobalSetup]
    public void Setup()
    {
        _context = new(new Subject(), ResourceKey.Default, RouteKey.Default);
        _equivalent = new(new Subject(), ResourceKey.Default, RouteKey.Default);
    }

    [Benchmark]
    public SubjectKey SingleSegment() => SubjectKey.From("benchmark-subject");

    [Benchmark]
    public SubjectKey MultipleSegments() => SubjectKey.FromSegments(_segments);

    [Benchmark]
    public bool SameSubjectContext() => _context.Equals(_context);

    [Benchmark]
    public bool EquivalentSubjectContext() => _context.Equals(_equivalent);

    private sealed class Subject : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From("benchmark-subject");
    }
}
