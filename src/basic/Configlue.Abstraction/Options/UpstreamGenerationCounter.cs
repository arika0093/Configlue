namespace Configlue;

internal sealed class UpstreamGenerationCounter
{
    private long _value;

    public long Capture() => Volatile.Read(ref _value);

    public void Advance() => Interlocked.Increment(ref _value);
}
