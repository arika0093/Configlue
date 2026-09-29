namespace Configlue;

/// <summary>Applies common routing and capability settings to a registered provider source.</summary>
public sealed class ConfiglueSourceRegistration
{
    private readonly Action _ensureMutable;
    private string? _name;
    private int? _priority;
    private StateFallbackCondition? _fallbackCondition;
    private bool? _readOnly;
    private bool? _explicitOnly;

    internal ConfiglueSourceRegistration(Action ensureMutable) => _ensureMutable = ensureMutable;

    /// <summary>Assigns a stable application-defined logical source name.</summary>
    public ConfiglueSourceRegistration Named(string name)
    {
        _ensureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
        return this;
    }

    /// <summary>Sets the read priority shared by all providers.</summary>
    public ConfiglueSourceRegistration Priority(int priority)
    {
        _ensureMutable();
        _priority = priority;
        return this;
    }

    /// <summary>Sets the statuses that allow resolution to fall back to lower-priority sources.</summary>
    public ConfiglueSourceRegistration FallbackWhen(StateFallbackCondition condition)
    {
        _ensureMutable();
        if (
            (
                condition
                & ~(StateFallbackCondition.NotFoundOrUnavailable | StateFallbackCondition.Invalid)
            ) != 0
        )
        {
            throw new ArgumentOutOfRangeException(nameof(condition));
        }

        _fallbackCondition = condition;
        return this;
    }

    /// <summary>Excludes this source from inferred writes, or restores its existing writer.</summary>
    public ConfiglueSourceRegistration ReadOnly(bool readOnly = true)
    {
        _ensureMutable();
        _readOnly = readOnly;
        return this;
    }

    /// <summary>Requires this source to have a writer.</summary>
    public ConfiglueSourceRegistration Writable()
    {
        _ensureMutable();
        _readOnly = false;
        return this;
    }

    /// <summary>Excludes this source from inferred ordinary write routing.</summary>
    public ConfiglueSourceRegistration ExplicitOnly(bool explicitOnly = true)
    {
        _ensureMutable();
        _explicitOnly = explicitOnly;
        return this;
    }

    internal StateSource<TFragment> Apply<TFragment>(StateSource<TFragment> source)
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        if (_readOnly == false && source.Writer is null)
        {
            throw new InvalidOperationException(
                $"Source '{_name ?? source.Id}' was configured as writable but its provider has no writer."
            );
        }

        if (
            _name is null
            && _priority is null
            && _fallbackCondition is null
            && _readOnly is null
            && _explicitOnly is null
        )
        {
            return source;
        }

        var configured = new StateSource<TFragment>(
            _name ?? source.Id,
            source.Reader,
            _priority ?? source.Priority,
            _fallbackCondition ?? source.FallbackCondition,
            _readOnly == true ? null : source.Writer,
            source.Watcher,
            source.PhysicalOrigin,
            source.ConfiguredResourceId,
            _explicitOnly ?? source.ExplicitOnly,
            source.GetSubjectKey
        );
        source.CopyRoutingMetadataTo(configured, _explicitOnly);
        return configured;
    }
}
