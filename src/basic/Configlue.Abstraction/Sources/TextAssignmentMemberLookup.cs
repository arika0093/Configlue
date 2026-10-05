namespace Configlue.Sources;

/// <summary>
/// A case-insensitive index over one schema's members that distinguishes a unique match
/// from an ambiguous one, shared by flat key/value transports.
/// </summary>
internal sealed class TextAssignmentMemberLookup
{
    private readonly Dictionary<string, ConfiglueMemberSchema> _membersByName;
    private readonly HashSet<string> _ambiguousNames;

    private TextAssignmentMemberLookup(
        Dictionary<string, ConfiglueMemberSchema> membersByName,
        HashSet<string> ambiguousNames
    )
    {
        _membersByName = membersByName;
        _ambiguousNames = ambiguousNames;
    }

    public static TextAssignmentMemberLookup Create(ConfiglueModelSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return schema.GetTextAssignmentMemberLookup();
    }

    internal static TextAssignmentMemberLookup Build(ConfiglueModelSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var membersByName = new Dictionary<string, ConfiglueMemberSchema>(
            schema.Members.Count,
            StringComparer.OrdinalIgnoreCase
        );
        var ambiguousNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < schema.Members.Count; index++)
        {
            var member = schema.Members[index];
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

        return new TextAssignmentMemberLookup(membersByName, ambiguousNames);
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
