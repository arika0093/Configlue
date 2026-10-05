using Configlue.State;

namespace Configlue.Source.Http;

/// <summary>Thread-safe canonical-baseline cache for PATCH-vs-PUT decisions and ETag state.</summary>
/// <remarks>
/// Owns mutable revision/baseline bytes shared by the read, write, and watch paths;
/// watch-loop convergence writes both revision and baseline bytes via
/// <c>CacheGetResult</c>.
/// </remarks>
internal sealed class HttpStateBaselineCache
{
    private readonly object _gate = new();
    private string? _lastRevision;
    private byte[]? _baselineJson;

    public string? GetRevision()
    {
        lock (_gate)
        {
            return _lastRevision;
        }
    }

    public void SetRevision(string? revision)
    {
        lock (_gate)
        {
            _lastRevision = revision;
            _baselineJson = null;
        }
    }

    public void SetBaseline(byte[] json, string? revision)
    {
        ArgumentNullException.ThrowIfNull(json);
        lock (_gate)
        {
            _lastRevision = revision;
            _baselineJson = json;
        }
    }

    public void ClearBaselineJson()
    {
        lock (_gate)
        {
            _baselineJson = null;
        }
    }

    public void CacheGetResult(string? revision, byte[]? content)
    {
        if (content is { Length: > 0 } && revision is not null)
        {
            SetBaseline(content, revision);
        }
        else
        {
            SetRevision(revision);
        }
    }

    public void CachePutResult(string? revision, byte[] content, Func<byte[], bool> isParsable)
    {
        if (content.Length > 0 && isParsable(content))
        {
            SetBaseline(content, revision);
        }
        else
        {
            SetRevision(revision);
        }
    }

    public bool TryGetPatchBaseline<TFragment>(
        StateWriteRequest<TFragment> request,
        out string revision,
        out byte[] baselineJson
    )
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        lock (_gate)
        {
            if (
                request.Condition.IsMatch
                && request.Condition.Revision is not null
                && _baselineJson is not null
                && string.Equals(
                    _lastRevision,
                    request.Condition.Revision,
                    StringComparison.Ordinal
                )
            )
            {
                revision = request.Condition.Revision;
                baselineJson = _baselineJson;
                return true;
            }
        }

        revision = "";
        baselineJson = [];
        return false;
    }
}
