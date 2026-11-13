using System.Collections;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using Raf.Domain.Requests;

namespace Raf.Client.Core.Execution;

/// <summary>Resultado de una extracción: el valor, o por qué no se obtuvo.</summary>
public sealed record ExtractionOutcome(ResponseExtraction Extraction, string? Value, string? Problem)
{
    public bool Found => Value is not null;
}

/// <summary>
/// Saca valores de una respuesta con las mismas rutas que copia el menú del body: JSON path
/// (<c>$.a.b[0]</c>, <c>$['otra clave']</c>) o XPath (<c>/a/b[2]/@id</c>), y en un header, su nombre.
/// </summary>
public static class ResponseExtractor
{
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static ExtractionOutcome Evaluate(
        ResponseExtraction extraction, string? body, IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        try
        {
            var value = extraction.Source == ExtractionSource.Header
                ? FromHeader(headers, extraction.Path.Trim())
                : FromBody(body ?? "", extraction.Path.Trim());
            return new ExtractionOutcome(extraction, value, value is null ? CoreTexts.Extract_NotFound : null);
        }
        catch (FormatException e)
        {
            return new ExtractionOutcome(extraction, null, e.Message);
        }
    }

    private static string? FromHeader(IReadOnlyList<KeyValuePair<string, string>> headers, string name)
    {
        var values = headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).ToList();
        return values.Count == 0 ? null : string.Join(", ", values);
    }

    private static string? FromBody(string body, string path)
    {
        if (path.StartsWith('$')) return FromJson(body, path);
        if (path.StartsWith('/')) return FromXml(body, path);
        throw new FormatException(CoreTexts.Extract_UnknownPath);
    }

    // ---- JSON ----

    private static string? FromJson(string body, string path)
    {
        var steps = ParseJsonPath(path);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new FormatException(CoreTexts.Extract_BodyNotJson);
        }

        using (document)
        {
            var node = document.RootElement;
            foreach (var step in steps)
            {
                if (step is int index)
                {
                    if (node.ValueKind != JsonValueKind.Array || index >= node.GetArrayLength()) return null;
                    node = node[index];
                }
                else if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty((string)step, out node))
                {
                    return null;
                }
            }

            return node.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => node.GetString(),
                JsonValueKind.Object or JsonValueKind.Array => JsonSerializer.Serialize(node, Compact),
                _ => node.GetRawText(),
            };
        }
    }

    /// <summary>Pasos de <c>$.a['b c'][2]</c>: un string por propiedad y un int por índice.</summary>
    internal static List<object> ParseJsonPath(string path)
    {
        var steps = new List<object>();
        var i = 1; // después de '$'
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                var start = ++i;
                while (i < path.Length && path[i] is not ('.' or '[')) i++;
                if (i == start) throw Invalid();
                steps.Add(path[start..i]);
            }
            else if (path[i] == '[' && i + 1 < path.Length && path[i + 1] is '\'' or '"')
            {
                var quote = path[i + 1];
                var name = new StringBuilder();
                i += 2;
                while (i < path.Length && path[i] != quote)
                {
                    if (path[i] == '\\' && i + 1 < path.Length) i++;
                    name.Append(path[i++]);
                }
                if (i + 1 >= path.Length || path[i + 1] != ']') throw Invalid();
                steps.Add(name.ToString());
                i += 2;
            }
            else if (path[i] == '[')
            {
                var end = path.IndexOf(']', i);
                if (end < 0 || !int.TryParse(path.AsSpan(i + 1, end - i - 1), out var index) || index < 0) throw Invalid();
                steps.Add(index);
                i = end + 1;
            }
            else
            {
                throw Invalid();
            }
        }
        return steps;

        FormatException Invalid() => new(string.Format(CoreTexts.Extract_InvalidJsonPath, path));
    }

    // ---- XML ----

    private static string? FromXml(string body, string path)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(body, LoadOptions.None);
        }
        catch (XmlException)
        {
            throw new FormatException(CoreTexts.Extract_BodyNotXml);
        }

        object result;
        try
        {
            result = document.XPathEvaluate(path);
        }
        catch (XPathException)
        {
            throw new FormatException(string.Format(CoreTexts.Extract_InvalidXPath, path));
        }

        var first = result is IEnumerable items and not string ? items.Cast<object>().FirstOrDefault() : result;
        return first switch
        {
            null => null,
            XAttribute a => a.Value,
            XElement e => e.HasElements ? e.ToString(SaveOptions.DisableFormatting) : e.Value,
            XText t => t.Value,
            bool b => b ? "true" : "false",
            double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            var other => other.ToString(),
        };
    }
}
