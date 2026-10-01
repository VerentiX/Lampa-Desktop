using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Lampa.Desktop.Models;

internal static class JsonText
{
    public static readonly JsonSerializerOptions Indented = Create(true);
    public static readonly JsonSerializerOptions Compact = Create(false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        options.MakeReadOnly();
        return options;
    }

    public static string Write(JsonNode node, bool indented = true)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented }))
            node.WriteTo(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
