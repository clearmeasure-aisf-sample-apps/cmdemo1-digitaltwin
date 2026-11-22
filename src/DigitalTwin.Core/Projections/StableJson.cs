using System.Text.Json;
using System.Text.Json.Nodes;

namespace DigitalTwin.Core.Projections;

/// <summary>
/// Deterministic JSON for state payloads: sources return object keys in varying order or embed generation timestamps,
/// and either would make an unchanged entity look changed on every poll.
/// </summary>
public static class StableJson
{
    public static string SortedObject(JsonElement element)
        => element.ValueKind == JsonValueKind.Object ? Canonical(JsonNode.Parse(element.GetRawText())) : "{}";

    /// <summary>Removes top-level properties (e.g. "generated") and sorts keys; non-JSON input is returned unchanged.</summary>
    public static string? Without(string? json, params string[] properties)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }
        if (node is JsonObject obj)
        {
            foreach (var property in properties)
            {
                obj.Remove(property);
            }
        }
        return Canonical(node);
    }

    private static string Canonical(JsonNode? node) => Sort(node)?.ToJsonString() ?? "null";

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sort(p.Value?.DeepClone())))),
        JsonArray array => new JsonArray(array.Select(item => Sort(item?.DeepClone())).ToArray()),
        _ => node?.DeepClone()
    };
}
