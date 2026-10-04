using System.Text;
using System.Text.Json;

namespace SparseFragments.JsonPatch.Tests;

internal static class PatchTestHelpers
{
    public static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    public static string Text(ReadOnlyMemory<byte> utf8) => Encoding.UTF8.GetString(utf8.ToArray());

    public static JsonSerializerOptions CamelCase() =>
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

    public static JsonSerializerOptions CaseInsensitive() =>
        new() { PropertyNameCaseInsensitive = true };
}
