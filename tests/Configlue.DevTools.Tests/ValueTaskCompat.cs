namespace Configlue.DevTools.Tests;

internal static class ValueTaskCompat
{
    public static ValueTask<T> FromResult<T>(T result) => new(result);

    public static ValueTask<T> FromException<T>(Exception exception) =>
        new(Task.FromException<T>(exception));

    public static ValueTask FromException(Exception exception) =>
        new(Task.FromException(exception));
}
