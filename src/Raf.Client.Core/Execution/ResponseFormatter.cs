using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Raf.Client.Core.Execution;

public enum ResponseFormat { Plain, Json, Xml }

public sealed record FormattedText(string Text, ResponseFormat Format);

public static class ResponseFormatter
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Indenta JSON o XML. Si el texto no se puede interpretar (o está truncado), lo devuelve tal cual.</summary>
    public static FormattedText Format(string text, string? mediaType)
    {
        var trimmed = text.AsSpan().TrimStart();
        var looksJson = mediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
            || (mediaType is null && trimmed.Length > 0 && trimmed[0] is '{' or '[');
        var looksXml = mediaType?.Contains("xml", StringComparison.OrdinalIgnoreCase) == true
            || (mediaType is null && trimmed.Length > 0 && trimmed[0] == '<');

        if (looksJson && TryFormatJson(text) is { } json) return new FormattedText(json, ResponseFormat.Json);
        if (looksXml && TryFormatXml(text) is { } xml) return new FormattedText(xml, ResponseFormat.Xml);
        return new FormattedText(text, ResponseFormat.Plain);
    }

    private static string? TryFormatJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(doc.RootElement, Indented);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryFormatXml(string text)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            return XDocument.Load(reader).ToString();
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
