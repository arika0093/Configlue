using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.CompilerServices;
using Configlue.Sources;

/// <summary>Measures revision work for ignored transport keys, including repeated origins.</summary>
[MemoryDiagnoser]
public class TextAssignmentRevisionBenchmarks
{
    [Params(0, 1, 16, 256)]
    public int AssignmentCount { get; set; }

    [Params(false, true)]
    public bool RepeatedOrigin { get; set; }

    private ConfiglueModelSchema _schema = null!;
    private TextAssignment[] _assignments = null!;
    private readonly TextAssignmentBinderOptions _options = new();

    [GlobalSetup]
    public void Setup()
    {
        _schema = FragmentEqualitySixteenSettings.ConfiglueSchema;
        _assignments = Enumerable
            .Range(0, AssignmentCount)
            .Select(index => new TextAssignment(
                ["Missing"],
                index,
                RepeatedOrigin ? "未登録" : $"未登録/{index:D3}"
            ))
            .ToArray();
        VerifyRevision(_assignments);
        VerifyRevision(_assignments.Reverse().ToArray());
    }

    private void VerifyRevision(TextAssignment[] assignments)
    {
        // Repeated origins retain the first raw value, hashed once per occurrence.
        var firstValues = assignments
            .GroupBy(assignment => assignment.Origin, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First().RawValue,
                StringComparer.Ordinal
            );
        var entries = assignments
            .OrderBy(assignment => assignment.Origin, StringComparer.Ordinal)
            .Select(assignment => new KeyValuePair<string, object?>(
                assignment.Origin,
                firstValues[assignment.Origin]
            ));
        var expected = TextAssignmentRevisionReference.Create(entries);
        var result = TextAssignmentBinder.Bind(_schema, assignments, _options);
        if (
            result.MatchedAny
            || result.Fragment.EnumeratePresentMembers().Any()
            || result.Revision != expected
        )
        {
            throw new InvalidOperationException(
                "Unmatched revision fixture changed its hash contract."
            );
        }
    }

    [Benchmark]
    public string UnmatchedRevision() =>
        TextAssignmentBinder.Bind(_schema, _assignments, _options).Revision;
}

internal static class TextAssignmentRevisionReference
{
    internal static string Create(IEnumerable<KeyValuePair<string, object?>> entries)
    {
        var input = new StringBuilder();
        foreach (var entry in entries)
        {
            Append(entry.Key);
            Append(TextAssignmentBinder.Render(entry.Value));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.ToString())));

        void Append(string value) =>
            input
                .Append(value.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(value);
    }
}
