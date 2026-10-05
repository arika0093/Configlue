namespace Configlue.DevTools;

using Configlue.CompilerServices;

/// <summary>
/// Schema/editability validation behind the DevTools editor session.
/// </summary>
/// <remarks>
/// <para>
/// Internal to the DevTools package. Maps changed draft paths to generated
/// member editability: read-only or shadowed members are rejected with an
/// explanation, never bypassed. Unknown members fall through to strict model
/// binding, which reports them as schema errors.
/// </para>
/// </remarks>
internal sealed class ConfiglueDevToolsEditorEditabilityGuard
{
    private readonly ConfiglueModelSchema _schema;

    public ConfiglueDevToolsEditorEditabilityGuard(ConfiglueModelSchema schema)
    {
        _schema = schema;
    }

    /// <summary>
    /// Finds changed paths (or their ancestors) that are not editable.
    /// </summary>
    public List<(string Path, string Editability)> FindBlockedPaths(
        IReadOnlyList<string> changedPaths,
        ConfiglueDetailsSnapshot details
    )
    {
        var blocked = new List<(string Path, string Editability)>();
        foreach (var changed in changedPaths)
        {
            foreach (var candidate in Prefixes(changed))
            {
                var truncated = TruncateAtCollection(candidate);
                ConfiglueMemberPath path;
                try
                {
                    path = ConfiglueMemberPath.FromNames(_schema, truncated);
                }
                catch (ArgumentException)
                {
                    // Unknown members are reported by strict model binding instead.
                    continue;
                }

                ConfiglueEditability editability;
                try
                {
                    editability = details.Editability(path);
                }
                catch (Exception)
                {
                    continue;
                }

                if (editability != ConfiglueEditability.Editable)
                {
                    blocked.Add((candidate, editability.ToString()));
                    break;
                }
            }
        }

        return blocked;
    }

    /// <summary>
    /// Describes changed paths with their current editability, deduplicated and sorted.
    /// </summary>
    public List<ConfiglueEditorChangedPath> DescribeChanges(
        IReadOnlyList<string> changedPaths,
        ConfiglueDetailsSnapshot? details
    )
    {
        var described = new List<ConfiglueEditorChangedPath>(changedPaths.Count);
        foreach (
            var changed in changedPaths
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static path => path, StringComparer.Ordinal)
        )
        {
            described.Add(
                new ConfiglueEditorChangedPath(changed, ResolveEditability(details, changed))
            );
        }

        return described;
    }

    /// <summary>
    /// Resolves one member path to its editability display name.
    /// </summary>
    public string ResolveEditability(ConfiglueDetailsSnapshot? details, string memberPath)
    {
        if (details is null)
        {
            return ConfiglueEditability.Editable.ToString();
        }

        try
        {
            var truncated = TruncateAtCollection(memberPath);
            return details
                .Editability(ConfiglueMemberPath.FromNames(_schema, truncated))
                .ToString();
        }
        catch (Exception)
        {
            return ConfiglueEditability.Editable.ToString();
        }
    }

    private static IEnumerable<string> Prefixes(string dottedPath)
    {
        var parts = dottedPath.Split('.');
        for (var length = 1; length <= parts.Length; length++)
        {
            yield return string.Join(".", parts.Take(length));
        }
    }

    private string TruncateAtCollection(string dottedPath)
    {
        // Collection elements (Tags[0]) and their descendants resolve editability
        // at the owning collection member; index segments never reach FromNames.
        var cleaned = StripIndices(dottedPath);
        var parts = cleaned.Split('.');
        var current = _schema;
        var kept = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            ConfiglueMemberSchema? found = null;
            foreach (var member in current.Members)
            {
                if (!member.IsDefault && string.Equals(member.Name, part, StringComparison.Ordinal))
                {
                    found = member;
                    break;
                }
            }

            if (found is null)
            {
                break;
            }

            kept.Add(part);
            if (IsCollection(found.Value))
            {
                break;
            }

            try
            {
                current = found.Value.NestedSchemaFactory?.Invoke() ?? current;
            }
            catch (Exception)
            {
                break;
            }

            if (found.Value.NestedSchemaFactory is null)
            {
                // Leaf reached; remaining parts (if any) belong to strict binding.
                break;
            }
        }

        return kept.Count == 0 ? StripIndices(dottedPath) : string.Join(".", kept);
    }

    private static string StripIndices(string path)
    {
        var builder = new System.Text.StringBuilder(path.Length);
        var depth = 0;
        foreach (var ch in path)
        {
            if (ch == '[')
            {
                depth++;
                continue;
            }

            if (ch == ']')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth == 0)
            {
                builder.Append(ch);
            }
        }

        return builder.ToString().Trim('.');
    }

    private static bool IsCollection(ConfiglueMemberSchema member)
    {
        try
        {
            var type = Nullable.GetUnderlyingType(member.ValueType) ?? member.ValueType;
            return type != typeof(string)
                && typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
