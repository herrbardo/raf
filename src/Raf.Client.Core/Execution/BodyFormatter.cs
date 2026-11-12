using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Raf.Domain.Requests;

namespace Raf.Client.Core.Execution;

/// <summary>Resultado de formatear un body: el texto nuevo, o el motivo por el que no se tocó.</summary>
public sealed record BodyFormatResult(string? Text, string? Error)
{
    public bool Succeeded => Text is not null;
}

/// <summary>
/// Da formato al JSON y al XML del body de un request. Admite <c>{{variables}}</c> sin comillas en el JSON, que un
/// parser estricto rechaza: se reemplazan por un marcador antes de interpretar y se restauran después.
/// </summary>
public static partial class BodyFormatter
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static bool Supports(BodyType type) => type is BodyType.Json or BodyType.Xml;

    public static BodyFormatResult Format(string text, BodyType type) => type switch
    {
        _ when string.IsNullOrWhiteSpace(text) => new BodyFormatResult(null, "El body está vacío: no hay nada para formatear."),
        BodyType.Json => FormatJson(text),
        BodyType.Xml => FormatXml(text),
        _ => new BodyFormatResult(null, "Solo se puede formatear un body JSON o XML."),
    };

    private static BodyFormatResult FormatJson(string text)
    {
        var (masked, placeholders) = MaskVariables(text);
        try
        {
            using var doc = JsonDocument.Parse(masked);
            var formatted = JsonSerializer.Serialize(doc.RootElement, Indented);
            foreach (var (marker, original) in placeholders)
                formatted = formatted.Replace($"\"{marker}\"", original, StringComparison.Ordinal);
            return new BodyFormatResult(formatted, null);
        }
        catch (JsonException e)
        {
            var where = e.LineNumber is { } line ? $" (línea {line + 1})" : "";
            return new BodyFormatResult(null, $"El JSON no es válido{where}: no se cambió el body.");
        }
    }

    /// <summary>
    /// Reemplaza las variables que están fuera de un string por un string marcador único, para que el JSON sea
    /// válido. Las que están dentro de un string ya son válidas y quedan como están.
    /// </summary>
    private static (string Text, List<(string Marker, string Original)> Placeholders) MaskVariables(string text)
    {
        var prefix = "__raf_var_" + Guid.NewGuid().ToString("N") + "_";
        var placeholders = new List<(string, string)>();
        var result = new StringBuilder(text.Length);
        var inString = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                result.Append(c);
                if (c == '\\' && i + 1 < text.Length) result.Append(text[++i]);
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"')
            {
                inString = true;
                result.Append(c);
                continue;
            }
            if (c == '{' && PlaceholderHere().Match(text, i) is { Success: true } m)
            {
                var marker = prefix + placeholders.Count;
                placeholders.Add((marker, m.Value));
                result.Append('"').Append(marker).Append('"');
                i += m.Length - 1;
                continue;
            }
            result.Append(c);
        }
        return (result.ToString(), placeholders);
    }

    private static BodyFormatResult FormatXml(string text)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            var doc = XDocument.Load(reader);
            // ToString omite la declaración <?xml ...?>: se conserva si estaba.
            var formatted = doc.Declaration is { } declaration
                ? declaration + Environment.NewLine + doc
                : doc.ToString();
            return new BodyFormatResult(formatted, null);
        }
        catch (XmlException e)
        {
            return new BodyFormatResult(null, $"El XML no es válido (línea {e.LineNumber}): no se cambió el body.");
        }
    }

    /// <summary>Una variable que empieza justo en la posición pedida (\G).</summary>
    [GeneratedRegex(@"\G" + VariableResolver.PlaceholderPattern)]
    private static partial Regex PlaceholderHere();
}
