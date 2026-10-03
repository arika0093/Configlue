namespace Configlue.Extensibility;

internal sealed class DisposableBag : IDisposable
{
    private List<IDisposable>? _items = [];

    public void Add(IDisposable item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var items = _items ?? throw new ObjectDisposedException(nameof(DisposableBag));
        items.Add(item);
    }

    public void Dispose()
    {
        var items = System.Threading.Interlocked.Exchange(ref _items, null);
        if (items is null)
        {
            return;
        }
        for (var index = items.Count - 1; index >= 0; index--)
        {
            items[index].Dispose();
        }
    }
}
