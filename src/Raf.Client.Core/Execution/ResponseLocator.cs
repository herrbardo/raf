using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Raf.Client.Core.Execution;

/// <summary>
/// Un nodo del body de la respuesta: su clave (null si es un elemento de un array o la raíz), su valor listo para
/// copiar y su ruta (JSON path o XPath).
/// </summary>
public sealed record ResponseNode(string? Key, string Value, string Path);

/// <summary>Encuentra el nodo de un body JSON o XML que está en una posición del texto, para el menú contextual.</summary>
public static class ResponseLocator
{
    /// <summary>
    /// El nodo de la línea donde está <paramref name="offset"/>: si en esa línea empiezan varios, el que está bajo la
    /// posición (o el primero); si no empieza ninguno (por ejemplo una línea con <c>}</c>), el más interno que la
    /// contiene. Null si el texto no se puede interpretar o no hay nodo.
    /// </summary>
    public static ResponseNode? Find(string text, ResponseFormat format, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        return format switch
        {
            ResponseFormat.Json => FindJson(text, offset),
            ResponseFormat.Xml => FindXml(text, offset),
            _ => null,
        };
    }

    // ---- JSON ----

    private sealed record JsonSpan(string? Key, string Path, int Start, int End, JsonTokenType Type, int ValueStart);

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static ResponseNode? FindJson(string text, int offset)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        List<JsonSpan> spans;
        try
        {
            spans = JsonSpans(bytes);
        }
        catch (JsonException)
        {
            return null;
        }

        var (lineStart, lineEnd) = LineBounds(text, offset);
        var at = Encoding.UTF8.GetByteCount(text.AsSpan(0, offset));
        var lineStartByte = Encoding.UTF8.GetByteCount(text.AsSpan(0, lineStart));
        var lineEndByte = lineStartByte + Encoding.UTF8.GetByteCount(text.AsSpan(lineStart, lineEnd - lineStart));

        var span = Pick(spans, s => s.Start, s => s.End, at, lineStartByte, lineEndByte);
        if (span is null) return null;

        var raw = bytes.AsSpan(span.ValueStart, span.End - span.ValueStart);
        string value;
        if (span.Type == JsonTokenType.String)
        {
            value = JsonSerializer.Deserialize<string>(raw) ?? "";
        }
        else if (span.Type is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            using var doc = JsonDocument.Parse(raw.ToArray());
            value = JsonSerializer.Serialize(doc.RootElement, Indented);
        }
        else
        {
            value = Encoding.UTF8.GetString(raw);
        }
        return new ResponseNode(span.Key, value, span.Path);
    }

    /// <summary>Recorre el JSON y devuelve cada valor con su ruta y su rango en bytes (desde la clave si tiene).</summary>
    private static List<JsonSpan> JsonSpans(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var spans = new List<JsonSpan>();
        // Contenedores abiertos: su ruta, si es array, el próximo índice, y los datos del valor que lo abrió.
        var stack = new Stack<(string Path, bool IsArray, int Next, string? Key, int Start, int ValueStart, JsonTokenType Type)>();
        string? pendingKey = null;
        var pendingKeyStart = -1;

        while (reader.Read())
        {
            var start = (int)reader.TokenStartIndex;
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    pendingKey = reader.GetString();
                    pendingKeyStart = start;
                    continue;

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    var closed = stack.Pop();
                    spans.Add(new JsonSpan(closed.Key, closed.Path, closed.Start, (int)reader.BytesConsumed, closed.Type, closed.ValueStart));
                    continue;
            }

            // Un valor: su clave y su ruta salen del contenedor donde está.
            string? key = null;
            string path;
            var nodeStart = start;
            if (stack.Count == 0)
            {
                path = "$";
            }
            else if (stack.Peek().IsArray)
            {
                var parent = stack.Pop();
                path = $"{parent.Path}[{parent.Next}]";
                stack.Push(parent with { Next = parent.Next + 1 });
            }
            else
            {
                key = pendingKey!;
                path = stack.Peek().Path + Member(key);
                nodeStart = pendingKeyStart;
            }

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                stack.Push((path, reader.TokenType == JsonTokenType.StartArray, 0, key, nodeStart, start, reader.TokenType));
            else
                spans.Add(new JsonSpan(key, path, nodeStart, (int)reader.BytesConsumed, reader.TokenType, start));
        }
        return spans;
    }

    /// <summary><c>.nombre</c> si es un identificador simple; si no, <c>['otro nombre']</c>.</summary>
    public static string Member(string key)
    {
        var simple = key.Length > 0 && (char.IsLetter(key[0]) || key[0] is '_' or '$')
            && key.All(c => char.IsLetterOrDigit(c) || c is '_' or '$');
        return simple ? "." + key : "['" + key.Replace("\\", "\\\\").Replace("'", "\\'") + "']";
    }

    // ---- XML ----

    private sealed record XmlSpan(XObject Node, int Start);

    private static ResponseNode? FindXml(string text, int offset)
    {
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            doc = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            return null;
        }
        if (doc.Root is null) return null;

        // El XmlReader da línea y columna (desde 1) del nombre: se pasan a offsets del texto.
        var lineOffsets = LineOffsets(text);
        int OffsetOf(XObject node)
        {
            var info = (IXmlLineInfo)node;
            if (!info.HasLineInfo() || info.LineNumber > lineOffsets.Count) return -1;
            return lineOffsets[info.LineNumber - 1] + info.LinePosition - 1;
        }

        var spans = new List<XmlSpan>();
        foreach (var element in doc.Root.DescendantsAndSelf())
        {
            spans.Add(new XmlSpan(element, OffsetOf(element)));
            foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
                spans.Add(new XmlSpan(attribute, OffsetOf(attribute)));
        }

        var (lineStart, lineEnd) = LineBounds(text, offset);
        var onLine = spans.Where(s => s.Start >= lineStart && s.Start <= lineEnd).ToList();
        // Sin un nodo que empiece en la línea (texto suelto o un cierre): el último elemento que empezó antes.
        var span = onLine.LastOrDefault(s => s.Start <= offset) ?? onLine.FirstOrDefault()
            ?? spans.LastOrDefault(s => s.Node is XElement && s.Start >= 0 && s.Start < lineStart
                && IsClosingLineOf((XElement)s.Node, text, lineStart, lineEnd));
        if (span is null) return null;

        return span.Node switch
        {
            XAttribute a => new ResponseNode(a.Name.LocalName, a.Value, XPath(a.Parent!) + "/@" + AttributeStep(a)),
            XElement e => new ResponseNode(e.Name.LocalName, ElementValue(e), XPath(e)),
            _ => null,
        };
    }

    /// <summary>La línea es el cierre <c>&lt;/nombre&gt;</c> de este elemento (el último que empezó antes con ese nombre).</summary>
    private static bool IsClosingLineOf(XElement element, string text, int lineStart, int lineEnd)
    {
        var line = text.AsSpan(lineStart, lineEnd - lineStart).Trim();
        return line.StartsWith("</") && line[2..].TrimEnd('>').Trim().Equals(PrefixedName(element), StringComparison.Ordinal);
    }

    private static string PrefixedName(XElement e) =>
        e.GetPrefixOfNamespace(e.Name.Namespace) is { Length: > 0 } prefix ? prefix + ":" + e.Name.LocalName : e.Name.LocalName;

    /// <summary>Solo texto: el texto. Con hijos: el XML del elemento, indentado.</summary>
    private static string ElementValue(XElement e) => e.HasElements ? e.ToString() : e.Value;

    private static string XPath(XElement element)
    {
        var steps = new List<string>();
        for (var e = element; e is not null; e = e.Parent)
        {
            var step = e.Name.Namespace == XNamespace.None
                ? e.Name.LocalName
                : $"*[local-name()='{e.Name.LocalName}']";
            if (e.Parent is { } parent)
            {
                var same = parent.Elements(e.Name).ToList();
                if (same.Count > 1) step += $"[{same.IndexOf(e) + 1}]";
            }
            steps.Add(step);
        }
        steps.Reverse();
        return "/" + string.Join("/", steps);
    }

    private static string AttributeStep(XAttribute a) =>
        a.Name.Namespace == XNamespace.None ? a.Name.LocalName : $"*[local-name()='{a.Name.LocalName}']";

    // ---- Común ----

    /// <summary>
    /// De los nodos que empiezan en la línea, el más interno bajo la posición, o el primero; si no empieza ninguno,
    /// el más interno que contiene la posición.
    /// </summary>
    private static T? Pick<T>(List<T> spans, Func<T, int> start, Func<T, int> end, int at, int lineStart, int lineEnd)
        where T : class
    {
        var onLine = spans.Where(s => start(s) >= lineStart && start(s) <= lineEnd).ToList();
        if (onLine.Count > 0)
        {
            return onLine.Where(s => start(s) <= at && at < end(s)).MaxBy(start)
                ?? onLine.MinBy(start);
        }
        return spans.Where(s => start(s) <= at && at < end(s)).MaxBy(start);
    }

    /// <summary>Inicio y fin (sin el salto) de la línea que contiene el offset.</summary>
    private static (int Start, int End) LineBounds(string text, int offset)
    {
        var start = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
        var end = text.IndexOf('\n', offset);
        if (end < 0) end = text.Length;
        if (end > start && text[end - 1] == '\r') end--;
        return (start, end);
    }

    private static List<int> LineOffsets(string text)
    {
        var offsets = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') offsets.Add(i + 1);
        return offsets;
    }
}
