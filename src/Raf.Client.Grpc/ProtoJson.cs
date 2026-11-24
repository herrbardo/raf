using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Raf.Client.Core.Grpc;

namespace Raf.Client.Grpc;

/// <summary>
/// Traduce entre JSON y el formato binario de protobuf a partir del descriptor del mensaje, porque Google.Protobuf
/// para .NET no tiene mensajes dinámicos. Sigue el mapeo JSON de proto3: nombres en lowerCamelCase (se acepta
/// también el original), enteros de 64 bits como texto, enums por nombre, bytes en base64 y la forma especial de
/// los tipos conocidos (Timestamp, Duration, wrappers, Struct, Value, ListValue, FieldMask, Empty y Any).
/// </summary>
internal sealed class ProtoJson(Func<string, MessageDescriptor?> findType)
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonWriterOptions WriteOptions = new()
    {
        Indented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ---------- JSON → protobuf ----------

    /// <summary>El mensaje en binario. Lanza <see cref="GrpcEngineException"/> con el campo que falló.</summary>
    public byte[] Encode(string json, MessageDescriptor type)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json, ReadOptions);
        }
        catch (JsonException e)
        {
            throw new GrpcEngineException(string.Format(CoreTexts.Grpc_InvalidJson, e.Message), e);
        }

        using (document) return EncodeMessage(document.RootElement, type, "");
    }

    private byte[] EncodeMessage(JsonElement json, MessageDescriptor type, string path)
    {
        using var buffer = new MemoryStream();
        var output = new CodedOutputStream(buffer);
        WriteMessageBody(output, json, type, path);
        output.Flush();
        return buffer.ToArray();
    }

    private void WriteMessageBody(CodedOutputStream output, JsonElement json, MessageDescriptor type, string path)
    {
        if (WellKnown.IsSpecial(type.FullName))
        {
            WriteWellKnown(output, json, type, path);
            return;
        }

        if (json.ValueKind != JsonValueKind.Object) throw Invalid(path, CoreTexts.Grpc_ExpectedValue, "{ }");
        foreach (var property in json.EnumerateObject())
        {
            var field = type.Fields.InDeclarationOrder().FirstOrDefault(f => f.JsonName == property.Name)
                ?? type.FindFieldByName(property.Name);
            var fieldPath = Join(path, property.Name);
            if (field is null) throw new GrpcEngineException(string.Format(CoreTexts.Grpc_InvalidField, fieldPath,
                string.Format(CoreTexts.Grpc_UnknownField, type.FullName)));

            // null es "sin valor", salvo en google.protobuf.Value, donde es el valor null.
            if (property.Value.ValueKind == JsonValueKind.Null && field.FieldType != FieldType.Message) continue;
            if (property.Value.ValueKind == JsonValueKind.Null && field.MessageType.FullName != WellKnown.Value) continue;
            WriteField(output, field, property.Value, fieldPath);
        }
    }

    private void WriteField(CodedOutputStream output, FieldDescriptor field, JsonElement value, string path)
    {
        if (field.IsMap)
        {
            if (value.ValueKind != JsonValueKind.Object) throw Invalid(path, CoreTexts.Grpc_ExpectedValue, "{ }");
            var keyField = field.MessageType.FindFieldByNumber(1);
            var valueField = field.MessageType.FindFieldByNumber(2);
            foreach (var entry in value.EnumerateObject())
            {
                var entryPath = $"{path}[\"{entry.Name}\"]";
                using var buffer = new MemoryStream();
                var inner = new CodedOutputStream(buffer);
                WriteSingle(inner, keyField, MapKey(entry.Name, keyField, entryPath), entryPath);
                if (entry.Value.ValueKind != JsonValueKind.Null || valueField.FieldType == FieldType.Message)
                    WriteSingle(inner, valueField, entry.Value, entryPath);
                inner.Flush();
                output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ByteString.CopyFrom(buffer.ToArray()));
            }
            return;
        }

        if (field.IsRepeated)
        {
            if (value.ValueKind != JsonValueKind.Array) throw Invalid(path, CoreTexts.Grpc_ExpectedValue, "[ ]");
            var i = 0;
            foreach (var item in value.EnumerateArray()) WriteSingle(output, field, item, $"{path}[{i++}]");
            return;
        }

        WriteSingle(output, field, value, path);
    }

    /// <summary>Las claves de un map llegan como texto: se pasan al tipo de la clave.</summary>
    private static JsonElement MapKey(string key, FieldDescriptor keyField, string path)
    {
        var json = keyField.FieldType switch
        {
            FieldType.String => JsonSerializer.Serialize(key),
            FieldType.Bool when key is "true" or "false" => key,
            FieldType.Bool => throw Invalid(path, CoreTexts.Grpc_ExpectedValue, "true/false"),
            _ => JsonSerializer.Serialize(key), // los enteros se aceptan como texto
        };
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private void WriteSingle(CodedOutputStream output, FieldDescriptor field, JsonElement value, string path)
    {
        var number = field.FieldNumber;
        switch (field.FieldType)
        {
            case FieldType.Double:
                output.WriteTag(number, WireFormat.WireType.Fixed64);
                output.WriteDouble(ReadDouble(value, path));
                break;
            case FieldType.Float:
                output.WriteTag(number, WireFormat.WireType.Fixed32);
                output.WriteFloat((float)ReadDouble(value, path));
                break;
            case FieldType.Int64:
                output.WriteTag(number, WireFormat.WireType.Varint);
                output.WriteInt64(ReadInteger<long>(value, path));
                break;
            case FieldType.SInt64:
                output.WriteTag(number, WireFormat.WireType.Varint);
                output.WriteSInt64(ReadInteger<long>(value, path));
                break;
            case FieldType.SFixed64:
                output.WriteTag(number, WireFormat.WireType.Fixed64);
                output.WriteSFixed64(ReadInteger<long>(value, path));
                break;
            case FieldType.UInt64:
                output.WriteTag(number, WireFormat.WireType.Varint);
                output.WriteUInt64(ReadInteger<ulong>(value, path));
                break;
            case FieldType.Fixed64:
                output.WriteTag(number, WireFormat.WireType.Fixed64);
                output.WriteFixed64(ReadInteger<ulong>(value, path));
                break;
            case FieldType.Int32:
                output.WriteTag(number, WireFormat.WireType.Varint);
                output.WriteInt32(ReadInteger<int>(value, path));
                break;
            case FieldType.SInt32:
                output.WriteTag(number, WireFormat.WireType.Varint);
                output.WriteSInt32(ReadInteger<int>(value, path));
                break;
            case FieldType.SFixed32:
                output.WriteTag(number, WireFormat.WireType.Fixed32);
                output.WriteSFixed32(ReadInteger<int>(value, path));
                break;
            case FieldType.UInt32:
                output.WriteTag(number, WireFormat.WireType.Varint);
                output.WriteUInt32(ReadInteger<uint>(value, path));
                break;
            case FieldType.Fixed32:
                output.WriteTag(number, WireFormat.WireType.Fixed32);
                output.WriteFixed32(ReadInteger<uint>(value, path));
                break;
            case FieldType.Bool:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid(path, CoreTexts.Grpc_ExpectedValue, "true/false");
                output.WriteTag(number, WireFormat.WireType.Varint);
                output.WriteBool(value.GetBoolean());
                break;
            case FieldType.String:
                if (value.ValueKind != JsonValueKind.String) throw Invalid(path, CoreTexts.Grpc_ExpectedValue, "\"texto\"");
                output.WriteTag(number, WireFormat.WireType.LengthDelimited);
                output.WriteString(value.GetString()!);
                break;
            case FieldType.Bytes:
                output.WriteTag(number, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ByteString.CopyFrom(ReadBytes(value, path)));
                break;
            case FieldType.Enum:
                output.WriteTag(number, WireFormat.WireType.Varint);
                output.WriteEnum(ReadEnum(value, field.EnumType, path));
                break;
            case FieldType.Message:
                output.WriteTag(number, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ByteString.CopyFrom(EncodeMessage(value, field.MessageType, path)));
                break;
            default:
                throw Invalid(path, CoreTexts.Grpc_ExpectedValue, field.FieldType.ToString());
        }
    }

    private static double ReadDouble(JsonElement value, string path) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String => value.GetString() switch
        {
            "NaN" => double.NaN,
            "Infinity" => double.PositiveInfinity,
            "-Infinity" => double.NegativeInfinity,
            var s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => throw Invalid(path, CoreTexts.Grpc_ExpectedValue, "número"),
        },
        _ => throw Invalid(path, CoreTexts.Grpc_ExpectedValue, "número"),
    };

    /// <summary>Enteros como número o como texto (así viajan los de 64 bits); se aceptan "1e3" y "5.0" si son exactos.</summary>
    private static T ReadInteger<T>(JsonElement value, string path) where T : struct, INumber<T>, IMinMaxValue<T>
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString()!,
            _ => throw Invalid(path, CoreTexts.Grpc_ExpectedValue, "entero"),
        };
        if (T.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exact)) return exact;
        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d == decimal.Truncate(d)
            && d >= decimal.CreateTruncating(T.MinValue) && d <= decimal.CreateTruncating(T.MaxValue))
            return T.CreateChecked(d);
        throw Invalid(path, CoreTexts.Grpc_ExpectedValue, $"entero ({typeof(T).Name})");
    }

    private static byte[] ReadBytes(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid(path, CoreTexts.Grpc_ExpectedValue, "base64");
        var text = value.GetString()!.Replace('-', '+').Replace('_', '/');
        text = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            throw Invalid(path, CoreTexts.Grpc_ExpectedValue, "base64");
        }
    }

    private static int ReadEnum(JsonElement value, EnumDescriptor type, string path)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && type.FindValueByName(value.GetString()!) is { } named) return named.Number;
        throw Invalid(path, CoreTexts.Grpc_ExpectedValue, string.Join(" | ", type.Values.Select(v => v.Name)));
    }

    private void WriteWellKnown(CodedOutputStream output, JsonElement json, MessageDescriptor type, string path)
    {
        switch (type.FullName)
        {
            case WellKnown.Timestamp:
                if (json.ValueKind != JsonValueKind.String || !WellKnown.TryParseTimestamp(json.GetString()!, out var seconds, out var nanos))
                    throw Invalid(path, CoreTexts.Grpc_ExpectedValue, "\"2024-01-31T12:00:00Z\"");
                WriteSecondsNanos(output, seconds, nanos);
                break;
            case WellKnown.Duration:
                if (json.ValueKind != JsonValueKind.String || !WellKnown.TryParseDuration(json.GetString()!, out seconds, out nanos))
                    throw Invalid(path, CoreTexts.Grpc_ExpectedValue, "\"1.5s\"");
                WriteSecondsNanos(output, seconds, nanos);
                break;
            case WellKnown.FieldMask:
                if (json.ValueKind != JsonValueKind.String) throw Invalid(path, CoreTexts.Grpc_ExpectedValue, "\"campo.uno,campo.dos\"");
                foreach (var part in json.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    output.WriteTag(1, WireFormat.WireType.LengthDelimited);
                    output.WriteString(WellKnown.ToSnakeCase(part));
                }
                break;
            case WellKnown.Struct:
                if (json.ValueKind != JsonValueKind.Object) throw Invalid(path, CoreTexts.Grpc_ExpectedValue, "{ }");
                WriteField(output, type.FindFieldByNumber(1), json, path);
                break;
            case WellKnown.ListValue:
                if (json.ValueKind != JsonValueKind.Array) throw Invalid(path, CoreTexts.Grpc_ExpectedValue, "[ ]");
                WriteField(output, type.FindFieldByNumber(1), json, path);
                break;
            case WellKnown.Value:
                WriteValue(output, json, type, path);
                break;
            case WellKnown.Any:
                WriteAny(output, json, path);
                break;
            default: // wrappers: el valor va directo, en el campo 1
                WriteSingle(output, type.FindFieldByNumber(1), json, path);
                break;
        }
    }

    private static void WriteSecondsNanos(CodedOutputStream output, long seconds, int nanos)
    {
        if (seconds != 0)
        {
            output.WriteTag(1, WireFormat.WireType.Varint);
            output.WriteInt64(seconds);
        }
        if (nanos != 0)
        {
            output.WriteTag(2, WireFormat.WireType.Varint);
            output.WriteInt32(nanos);
        }
    }

    private void WriteValue(CodedOutputStream output, JsonElement json, MessageDescriptor type, string path)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.Null:
                output.WriteTag(1, WireFormat.WireType.Varint);
                output.WriteEnum(0);
                break;
            case JsonValueKind.Number:
                output.WriteTag(2, WireFormat.WireType.Fixed64);
                output.WriteDouble(json.GetDouble());
                break;
            case JsonValueKind.String:
                output.WriteTag(3, WireFormat.WireType.LengthDelimited);
                output.WriteString(json.GetString()!);
                break;
            case JsonValueKind.True or JsonValueKind.False:
                output.WriteTag(4, WireFormat.WireType.Varint);
                output.WriteBool(json.GetBoolean());
                break;
            case JsonValueKind.Object:
                WriteSingle(output, type.FindFieldByNumber(5), json, path);
                break;
            case JsonValueKind.Array:
                WriteSingle(output, type.FindFieldByNumber(6), json, path);
                break;
        }
    }

    private void WriteAny(CodedOutputStream output, JsonElement json, string path)
    {
        if (json.ValueKind != JsonValueKind.Object) throw Invalid(path, CoreTexts.Grpc_ExpectedValue, "{ \"@type\": ... }");
        if (!json.TryGetProperty("@type", out var typeUrl) || typeUrl.ValueKind != JsonValueKind.String) return; // Any vacío
        var url = typeUrl.GetString()!;
        var name = url[(url.LastIndexOf('/') + 1)..];
        var inner = findType(name) ?? throw new GrpcEngineException(string.Format(CoreTexts.Grpc_UnknownType, name));

        // Los tipos conocidos van en "value"; el resto, con sus campos al lado de "@type".
        byte[] bytes;
        if (WellKnown.IsSpecial(inner.FullName))
        {
            bytes = json.TryGetProperty("value", out var wrapped) ? EncodeMessage(wrapped, inner, Join(path, "value")) : [];
        }
        else
        {
            using var rest = new MemoryStream();
            using (var writer = new Utf8JsonWriter(rest))
            {
                writer.WriteStartObject();
                foreach (var property in json.EnumerateObject().Where(p => p.Name != "@type")) property.WriteTo(writer);
                writer.WriteEndObject();
            }
            using var document = JsonDocument.Parse(rest.ToArray());
            bytes = EncodeMessage(document.RootElement, inner, path);
        }

        output.WriteTag(1, WireFormat.WireType.LengthDelimited);
        output.WriteString(url);
        output.WriteTag(2, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(ByteString.CopyFrom(bytes));
    }

    // ---------- protobuf → JSON ----------

    /// <summary>
    /// El mensaje en JSON con sangría. Muestra todos los campos, también los que tienen el valor por defecto (en
    /// proto3 no viajan y sin ellos no se sabría que existen), salvo los mensajes ausentes y los de un oneof o
    /// <c>optional</c> que no vinieron.
    /// </summary>
    public string Decode(byte[] data, MessageDescriptor type)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriteOptions))
        {
            try
            {
                WriteJson(writer, data, type);
            }
            catch (InvalidProtocolBufferException e)
            {
                throw new GrpcEngineException(string.Format(CoreTexts.Grpc_InvalidResponse, type.FullName, e.Message), e);
            }
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Lo que trae el binario, campo por campo: varios valores si es repeated o si se repitió.</summary>
    private static Dictionary<int, List<object>> ReadFields(byte[] data, MessageDescriptor type)
    {
        var values = new Dictionary<int, List<object>>();
        var input = new CodedInputStream(data);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var field = type.FindFieldByNumber(WireFormat.GetTagFieldNumber(tag));
            if (field is null)
            {
                input.SkipLastField();
                continue;
            }
            if (!values.TryGetValue(field.FieldNumber, out var list)) values[field.FieldNumber] = list = [];

            var wire = WireFormat.GetTagWireType(tag);
            if (wire == WireFormat.WireType.LengthDelimited && IsPackable(field.FieldType))
            {
                var packed = new CodedInputStream(input.ReadBytes().ToByteArray());
                while (!packed.IsAtEnd) list.Add(ReadScalar(packed, field.FieldType));
            }
            else
            {
                list.Add(ReadScalar(input, field.FieldType));
            }
        }
        return values;
    }

    private static bool IsPackable(FieldType type) => type is not (FieldType.String or FieldType.Bytes or FieldType.Message or FieldType.Group);

    private static object ReadScalar(CodedInputStream input, FieldType type) => type switch
    {
        FieldType.Double => input.ReadDouble(),
        FieldType.Float => input.ReadFloat(),
        FieldType.Int64 => input.ReadInt64(),
        FieldType.SInt64 => input.ReadSInt64(),
        FieldType.SFixed64 => input.ReadSFixed64(),
        FieldType.UInt64 => input.ReadUInt64(),
        FieldType.Fixed64 => input.ReadFixed64(),
        FieldType.Int32 => input.ReadInt32(),
        FieldType.SInt32 => input.ReadSInt32(),
        FieldType.SFixed32 => input.ReadSFixed32(),
        FieldType.UInt32 => input.ReadUInt32(),
        FieldType.Fixed32 => input.ReadFixed32(),
        FieldType.Bool => input.ReadBool(),
        FieldType.Enum => input.ReadEnum(),
        FieldType.String => input.ReadString(),
        FieldType.Bytes or FieldType.Message => input.ReadBytes().ToByteArray(),
        _ => throw new GrpcEngineException($"tipo {type} no soportado"),
    };

    private void WriteJson(Utf8JsonWriter writer, byte[] data, MessageDescriptor type)
    {
        if (WellKnown.IsSpecial(type.FullName))
        {
            WriteWellKnownJson(writer, data, type);
            return;
        }

        var values = ReadFields(data, type);
        writer.WriteStartObject();
        foreach (var field in type.Fields.InDeclarationOrder())
        {
            values.TryGetValue(field.FieldNumber, out var list);
            var present = list is { Count: > 0 };
            var omitWhenAbsent = field.FieldType == FieldType.Message && !field.IsRepeated
                || field.RealContainingOneof is not null || field.HasPresence && !field.IsRepeated;
            if (!present && omitWhenAbsent) continue;

            writer.WritePropertyName(field.JsonName);
            WriteFieldJson(writer, field, list ?? []);
        }
        writer.WriteEndObject();
    }

    private void WriteFieldJson(Utf8JsonWriter writer, FieldDescriptor field, List<object> values)
    {
        if (field.IsMap)
        {
            var keyField = field.MessageType.FindFieldByNumber(1);
            var valueField = field.MessageType.FindFieldByNumber(2);
            writer.WriteStartObject();
            foreach (var entry in values.Cast<byte[]>())
            {
                var parts = ReadFields(entry, field.MessageType);
                var key = parts.TryGetValue(1, out var k) ? k[^1] : Default(keyField.FieldType);
                writer.WritePropertyName(key switch
                {
                    bool b => b ? "true" : "false",
                    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                    var other => other.ToString()!,
                });
                if (parts.TryGetValue(2, out var v)) WriteValueJson(writer, valueField, v[^1]);
                else if (valueField.FieldType == FieldType.Message) WriteJson(writer, [], valueField.MessageType);
                else WriteValueJson(writer, valueField, Default(valueField.FieldType));
            }
            writer.WriteEndObject();
            return;
        }

        if (field.IsRepeated)
        {
            writer.WriteStartArray();
            foreach (var value in values) WriteValueJson(writer, field, value);
            writer.WriteEndArray();
            return;
        }

        // Si un campo singular llegó varias veces vale el último; un mensaje se combina, como hace protobuf.
        if (field.FieldType == FieldType.Message)
            WriteJson(writer, values.Cast<byte[]>().SelectMany(b => b).ToArray(), field.MessageType);
        else
            WriteValueJson(writer, field, values.Count > 0 ? values[^1] : Default(field.FieldType));
    }

    private static object Default(FieldType type) => type switch
    {
        FieldType.Double => 0d,
        FieldType.Float => 0f,
        FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => 0L,
        FieldType.UInt64 or FieldType.Fixed64 => 0UL,
        FieldType.UInt32 or FieldType.Fixed32 => 0U,
        FieldType.Bool => false,
        FieldType.String => "",
        FieldType.Bytes => Array.Empty<byte>(),
        _ => 0,
    };

    private void WriteValueJson(Utf8JsonWriter writer, FieldDescriptor field, object value)
    {
        switch (field.FieldType)
        {
            case FieldType.Double:
                WriteFloating(writer, (double)value);
                break;
            case FieldType.Float:
                WriteFloating(writer, (float)value);
                break;
            case FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64:
                writer.WriteStringValue(((long)value).ToString(CultureInfo.InvariantCulture));
                break;
            case FieldType.UInt64 or FieldType.Fixed64:
                writer.WriteStringValue(((ulong)value).ToString(CultureInfo.InvariantCulture));
                break;
            case FieldType.UInt32 or FieldType.Fixed32:
                writer.WriteNumberValue((uint)value);
                break;
            case FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32:
                writer.WriteNumberValue((int)value);
                break;
            case FieldType.Bool:
                writer.WriteBooleanValue((bool)value);
                break;
            case FieldType.String:
                writer.WriteStringValue((string)value);
                break;
            case FieldType.Bytes:
                writer.WriteStringValue(Convert.ToBase64String((byte[])value));
                break;
            case FieldType.Enum:
                var number = (int)value;
                if (field.EnumType.FullName == "google.protobuf.NullValue") writer.WriteNullValue();
                else if (field.EnumType.FindValueByNumber(number) is { } named) writer.WriteStringValue(named.Name);
                else writer.WriteNumberValue(number);
                break;
            case FieldType.Message:
                WriteJson(writer, (byte[])value, field.MessageType);
                break;
        }
    }

    private static void WriteFloating(Utf8JsonWriter writer, double value)
    {
        if (double.IsNaN(value)) writer.WriteStringValue("NaN");
        else if (double.IsPositiveInfinity(value)) writer.WriteStringValue("Infinity");
        else if (double.IsNegativeInfinity(value)) writer.WriteStringValue("-Infinity");
        else writer.WriteNumberValue(value);
    }

    private static void WriteFloating(Utf8JsonWriter writer, float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) WriteFloating(writer, (double)value);
        else writer.WriteRawValue(value.ToString("R", CultureInfo.InvariantCulture)); // sin los decimales de más del double
    }

    private void WriteWellKnownJson(Utf8JsonWriter writer, byte[] data, MessageDescriptor type)
    {
        var values = ReadFields(data, type);
        object? Last(int number) => values.TryGetValue(number, out var list) && list.Count > 0 ? list[^1] : null;
        switch (type.FullName)
        {
            case WellKnown.Timestamp:
                writer.WriteStringValue(WellKnown.FormatTimestamp((long)(Last(1) ?? 0L), (int)(Last(2) ?? 0)));
                break;
            case WellKnown.Duration:
                writer.WriteStringValue(WellKnown.FormatDuration((long)(Last(1) ?? 0L), (int)(Last(2) ?? 0)));
                break;
            case WellKnown.FieldMask:
                writer.WriteStringValue(string.Join(",",
                    (values.GetValueOrDefault(1) ?? []).Cast<string>().Select(WellKnown.ToCamelCase)));
                break;
            case WellKnown.Struct:
                WriteFieldJson(writer, type.FindFieldByNumber(1), values.GetValueOrDefault(1) ?? []);
                break;
            case WellKnown.ListValue:
                WriteFieldJson(writer, type.FindFieldByNumber(1), values.GetValueOrDefault(1) ?? []);
                break;
            case WellKnown.Value:
                var kind = type.Fields.InFieldNumberOrder().LastOrDefault(f => values.ContainsKey(f.FieldNumber));
                if (kind is null || kind.FieldNumber == 1) writer.WriteNullValue();
                else WriteValueJson(writer, kind, values[kind.FieldNumber][^1]);
                break;
            case WellKnown.Any:
                WriteAnyJson(writer, Last(1) as string, Last(2) as byte[] ?? []);
                break;
            default:
                var inner = type.FindFieldByNumber(1);
                WriteValueJson(writer, inner, Last(1) ?? Default(inner.FieldType));
                break;
        }
    }

    private void WriteAnyJson(Utf8JsonWriter writer, string? url, byte[] value)
    {
        writer.WriteStartObject();
        if (url is null)
        {
            writer.WriteEndObject();
            return;
        }

        writer.WriteString("@type", url);
        var name = url[(url.LastIndexOf('/') + 1)..];
        if (findType(name) is not { } inner)
        {
            // Sin el tipo no se puede leer: se muestra en base64, que es mejor que nada.
            writer.WriteString("value", Convert.ToBase64String(value));
        }
        else if (WellKnown.IsSpecial(inner.FullName))
        {
            writer.WritePropertyName("value");
            WriteJson(writer, value, inner);
        }
        else
        {
            using var document = JsonDocument.Parse(Decode(value, inner));
            foreach (var property in document.RootElement.EnumerateObject()) property.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    // ---------- Plantilla ----------

    /// <summary>
    /// Un mensaje de ejemplo con todos los campos en su valor por defecto. En un oneof se muestra solo la primera
    /// opción; los mensajes anidados se expanden hasta cuatro niveles y sin repetir un tipo que ya se está armando.
    /// </summary>
    public static string Template(MessageDescriptor type)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriteOptions)) WriteTemplate(writer, type, [], 0);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteTemplate(Utf8JsonWriter writer, MessageDescriptor type, HashSet<string> building, int depth)
    {
        switch (type.FullName)
        {
            case WellKnown.Timestamp: writer.WriteStringValue("1970-01-01T00:00:00Z"); return;
            case WellKnown.Duration: writer.WriteStringValue("0s"); return;
            case WellKnown.FieldMask: writer.WriteStringValue(""); return;
            case WellKnown.Struct or WellKnown.Any: writer.WriteStartObject(); writer.WriteEndObject(); return;
            case WellKnown.ListValue: writer.WriteStartArray(); writer.WriteEndArray(); return;
            case WellKnown.Value: writer.WriteNullValue(); return;
        }
        if (WellKnown.IsWrapper(type.FullName))
        {
            WriteTemplateScalar(writer, type.FindFieldByNumber(1));
            return;
        }

        writer.WriteStartObject();
        if (depth < 4 && building.Add(type.FullName))
        {
            var oneofsShown = new HashSet<OneofDescriptor>();
            foreach (var field in type.Fields.InDeclarationOrder())
            {
                if (field.RealContainingOneof is { } oneof && !oneofsShown.Add(oneof)) continue;
                writer.WritePropertyName(field.JsonName);
                if (field.IsMap)
                {
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                }
                else if (field.IsRepeated)
                {
                    writer.WriteStartArray();
                    writer.WriteEndArray();
                }
                else if (field.FieldType == FieldType.Message)
                {
                    WriteTemplate(writer, field.MessageType, building, depth + 1);
                }
                else
                {
                    WriteTemplateScalar(writer, field);
                }
            }
            building.Remove(type.FullName);
        }
        writer.WriteEndObject();
    }

    private static void WriteTemplateScalar(Utf8JsonWriter writer, FieldDescriptor field)
    {
        switch (field.FieldType)
        {
            case FieldType.Bool: writer.WriteBooleanValue(false); break;
            case FieldType.String or FieldType.Bytes: writer.WriteStringValue(""); break;
            case FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 or FieldType.UInt64 or FieldType.Fixed64:
                writer.WriteStringValue("0");
                break;
            case FieldType.Enum: writer.WriteStringValue(field.EnumType.Values[0].Name); break;
            default: writer.WriteNumberValue(0); break;
        }
    }

    // ---------- Ayudas ----------

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    private static GrpcEngineException Invalid(string path, string format, string expected) =>
        new(string.Format(CoreTexts.Grpc_InvalidField, path.Length == 0 ? "(mensaje)" : path, string.Format(format, expected)));
}
