using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeamCreate;

/// <summary>
/// What a project file contributes to the baseline and the manifest. Everything is its own bytes except the engine's <c>.meta</c> files, which carry
/// bookkeeping the engine rewrites by itself: it sets <c>sceneCompileDirty</c> the moment a scene is edited. That flag says nothing about the project, but
/// hashed as raw bytes it made a collaborator's first edit change the baseline, so Save refused with "Source/assets changed during collaboration" and two
/// machines could disagree about whether they had the same project. It is left out; every other field of a .meta still counts.
/// </summary>
public static class MetaFileRules
{
    private static readonly string[] IgnoredFields = { "sceneCompileDirty" };

    /// <summary>The bytes to hash for a file. Anything that is not a readable JSON-object .meta file is returned exactly as given.</summary>
    public static byte[] Canonical(string path, byte[] content)
    {
        if (content == null || string.IsNullOrEmpty(path) || !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
        {
            return content;
        }
        try
        {
            ReadOnlySpan<byte> body = content;
            if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
            {
                body = body.Slice(3);
            }
            if (!(JsonNode.Parse(body) is JsonObject document))
            {
                return content;
            }
            foreach (string field in IgnoredFields)
            {
                document.Remove(field);
            }
            // Sorted by field name so the order the engine happened to write them in does not matter, and compact so whitespace does not either.
            SortedObject(document, out JsonObject sorted);
            return Encoding.UTF8.GetBytes(sorted.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        }
        catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is ArgumentException)
        {
            return content;
        }
    }

    private static void SortedObject(JsonObject source, out JsonObject result)
    {
        result = new JsonObject();
        foreach (var pair in System.Linq.Enumerable.OrderBy(source, (System.Collections.Generic.KeyValuePair<string, JsonNode> x) => x.Key, StringComparer.Ordinal))
        {
            result[pair.Key] = pair.Value == null ? null : JsonNode.Parse(pair.Value.ToJsonString());
        }
    }
}
