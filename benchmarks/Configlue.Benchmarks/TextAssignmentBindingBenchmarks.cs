using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.CompilerServices;
using Configlue.Sources;

/// <summary>Separates warm binding from creation of a fresh immutable schema.</summary>
[MemoryDiagnoser]
public class TextAssignmentBindingBenchmarks
{
    [Params(1, 4, 16)]
    public int MemberCount { get; set; }

    private ConfiglueModelSchema _schema = null!;
    private TextAssignment[] _assignments = null!;
    private readonly TextAssignmentBinderOptions _options = new();

    [GlobalSetup]
    public void Setup()
    {
        _schema = FragmentEqualitySixteenSettings.ConfiglueSchema;
        var model = new FragmentEqualitySixteenSettings();
        _assignments = _schema
            .Members.Take(MemberCount)
            .Select(member => new TextAssignment(
                [member.Name.ToLowerInvariant()],
                member.GetValue!(model),
                member.Name
            ))
            .ToArray();
        var result = TextAssignmentBinder.Bind(_schema, _assignments, _options);
        if (!result.MatchedAny || result.Fragment.EnumeratePresentMembers().Count() != MemberCount)
        {
            throw new InvalidOperationException(
                "Text binding fixture did not resolve all assignments."
            );
        }
    }

    [Benchmark]
    public IConfiglueFragment WarmSchema() =>
        TextAssignmentBinder.Bind(_schema, _assignments, _options).Fragment;

    [Benchmark]
    public IConfiglueFragment ColdSchema()
    {
        var schema = new ConfiglueModelSchema(
            _schema.ModelType,
            _schema.Id,
            _schema.Version,
            _schema.Members,
            _schema.CreateEmptyFragment
        );
        return TextAssignmentBinder.Bind(schema, _assignments, _options).Fragment;
    }
}
