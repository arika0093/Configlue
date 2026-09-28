namespace Configlue.Source.Environment;

/// <summary>
/// A case-insensitive index over one schema's members that distinguishes a unique match from an
/// ambiguous one, so canonical environment paths can be resolved without rescanning members per segment.
/// </summary>
internal sealed class SchemaMemberLookup
{
    private readonly Dictionary<string, ConfiglueMemberSchema> _membersByName;
    private readonly HashSet<string> _ambiguousNames;

    private SchemaMemberLookup(
        Dictionary<string, ConfiglueMemberSchema> membersByName,
        HashSet<string> ambiguousNames
    )
    {
        _membersByName = membersByName;
        _ambiguousNames = ambiguousNames;
    }

    public static SchemaMemberLookup Create(ConfiglueModelSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var membersByName = new Dictionary<string, ConfiglueMemberSchema>(
            StringComparer.OrdinalIgnoreCase
        );
        var ambiguousNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in schema.Members)
        {
            if (ambiguousNames.Contains(member.Name))
            {
                continue;
            }

            if (!membersByName.TryAdd(member.Name, member))
            {
                ambiguousNames.Add(member.Name);
                membersByName.Remove(member.Name);
            }
        }

        return new SchemaMemberLookup(membersByName, ambiguousNames);
    }

    /// <summary>Resolves a path segment to a unique member or reports an ambiguous segment.</summary>
    public bool TryResolve(string segment, out ConfiglueMemberSchema member, out bool isAmbiguous)
    {
        if (_ambiguousNames.Contains(segment))
        {
            member = default;
            isAmbiguous = true;
            return false;
        }

        if (_membersByName.TryGetValue(segment, out member))
        {
            isAmbiguous = false;
            return true;
        }

        member = default;
        isAmbiguous = false;
        return false;
    }
}
