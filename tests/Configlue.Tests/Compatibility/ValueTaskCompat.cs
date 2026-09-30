namespace Configlue.Tests;

internal static class ValueTaskCompat
{
    public static ValueTask<T> FromResult<T>(T result) => new(result);
}
