using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Internal;

internal static class JsonHelpers
{
    public static string? GetString(JsonObject? obj, string key) => TryGetValue<string>(obj, key, out var value) ? value : null;

    public static string GetRequiredString(JsonObject obj, string key)
    {
        ArgumentNullException.ThrowIfNull(obj);

        return GetString(obj, key) ?? throw new KeyNotFoundException(key);
    }

    public static long? GetInt64(JsonObject? obj, string key) => TryGetValue<long>(obj, key, out var value) ? value : null;

    public static int? GetInt32(JsonObject? obj, string key) => TryGetValue<int>(obj, key, out var value) ? value : null;

    public static double? GetDouble(JsonObject? obj, string key) => TryGetValue<double>(obj, key, out var value) ? value : null;

    public static bool? GetBool(JsonObject? obj, string key) => TryGetValue<bool>(obj, key, out var value) ? value : null;

    public static JsonObject? GetObject(JsonObject? obj, string key)
    {
        return obj is not null && obj.TryGetPropertyValue(key, out var node) ? node as JsonObject : null;
    }

    public static JsonArray? GetArray(JsonObject? obj, string key)
    {
        return obj is not null && obj.TryGetPropertyValue(key, out var node) ? node as JsonArray : null;
    }

    public static JsonNode? GetNode(JsonObject? obj, string key)
    {
        return obj is not null && obj.TryGetPropertyValue(key, out var node) ? node : null;
    }

    public static void SetStringArray(JsonObject obj, string key, IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return;
        }

        obj[key] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    }

    private static bool TryGetValue<T>(JsonObject? obj, string key, out T value)
    {
        if (obj is not null
            && obj.TryGetPropertyValue(key, out var node)
            && node is JsonValue jsonValue
            && jsonValue.TryGetValue(out T? result)
            && result is not null)
        {
            value = result;
            return true;
        }

        value = default!;
        return false;
    }
}
