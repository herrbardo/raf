using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Raf.Client.Core.Grpc;
using Raf.Client.Grpc.Tests.Proto;

namespace Raf.Client.Grpc.Tests;

/// <summary>
/// El traductor se compara con Google.Protobuf y sus clases generadas: lo que RAF codifica tiene que leerse igual
/// que lo que codifica la librería oficial, y al revés.
/// </summary>
public class ProtoJsonTests
{
    private static readonly TypeRegistry Registry = TypeRegistry.FromMessages(Inner.Descriptor, Timestamp.Descriptor);

    private static readonly ProtoJson Json = new(name => Find(name));

    private static MessageDescriptor? Find(string name) => name switch
    {
        "raf.test.Inner" => Inner.Descriptor,
        "raf.test.Everything" => Everything.Descriptor,
        "google.protobuf.Timestamp" => Timestamp.Descriptor,
        _ => null,
    };

    private static Everything Full() => new()
    {
        D = -1.5, F = 2.25f, I64 = long.MinValue, U64 = ulong.MaxValue, I32 = -7, F64 = 1UL << 60, F32 = 9, B = true,
        S = "hola ñandú \"comillas\" \n", By = ByteString.CopyFrom(0, 255, 128), U32 = uint.MaxValue, Sf32 = -3, Sf64 = -4,
        Si32 = int.MinValue, Si64 = -99, Color = Color.Green,
        Inner = new Inner { Name = "adentro", Values = { 1, 2, 3 } },
        Inners = { new Inner { Name = "a" }, new Inner { Values = { -1 } } },
        Numbers = { 1, long.MaxValue },
        ByName = { ["x"] = new Inner { Name = "equis" }, ["vacío"] = new Inner() },
        Labels = { [1] = "uno", [-5] = "menos cinco" },
        Nested = new Inner { Name = "en el oneof" },
        Maybe = 0,
        At = Timestamp.FromDateTimeOffset(new DateTimeOffset(2024, 2, 29, 23, 59, 58, 123, TimeSpan.Zero)),
        Took = Duration.FromTimeSpan(TimeSpan.FromMilliseconds(-1500)),
        Wrapped = 42,
        Meta = Struct.Parser.ParseJson("""{ "a": 1, "b": [true, null, "x"], "c": { "d": 2.5 } }"""),
        AnyValue = Value.ForString("valor"),
        Mask = FieldMask.FromString("snake_case_name,inner.name"),
        Extra = Any.Pack(new Inner { Name = "empacado" }),
        Colors = { Color.Red, Color.Unspecified },
        List = ListValue.Parser.ParseJson("[1, \"dos\", false]"),
        SnakeCaseName = "snake",
        Ratios = { double.NaN, double.PositiveInfinity, 0.1 },
    };

    [Fact]
    public void Encodes_the_official_json_into_the_same_message()
    {
        var original = Full();
        var json = new JsonFormatter(new JsonFormatter.Settings(true, Registry)).Format(original);

        var parsed = Everything.Parser.ParseFrom(Json.Encode(json, Everything.Descriptor));

        Assert.Equal(original, parsed);
    }

    [Fact]
    public void Decodes_into_json_the_official_parser_reads_back()
    {
        var original = Full();

        var json = Json.Decode(original.ToByteArray(), Everything.Descriptor);
        var parsed = new JsonParser(new JsonParser.Settings(20, Registry)).Parse<Everything>(json);

        Assert.Equal(original, parsed);
    }

    [Fact]
    public void Decodes_default_values_too_but_not_absent_messages_or_unset_oneofs()
    {
        var json = JsonDocument.Parse(Json.Decode(new Everything().ToByteArray(), Everything.Descriptor)).RootElement;

        Assert.Equal(0, json.GetProperty("i32").GetInt32());
        Assert.Equal("0", json.GetProperty("i64").GetString());
        Assert.Equal("", json.GetProperty("s").GetString());
        Assert.Equal("COLOR_UNSPECIFIED", json.GetProperty("color").GetString());
        Assert.Equal(JsonValueKind.Array, json.GetProperty("inners").ValueKind);
        Assert.Equal(JsonValueKind.Object, json.GetProperty("byName").ValueKind);
        Assert.False(json.TryGetProperty("inner", out _));
        Assert.False(json.TryGetProperty("text", out _));
        Assert.False(json.TryGetProperty("maybe", out _));
        Assert.False(json.TryGetProperty("at", out _));
    }

    [Fact]
    public void Accepts_original_names_numbers_as_text_and_comments()
    {
        const string json = """
            {
              // un comentario
              "snake_case_name": "original",
              "i64": 12,
              "i32": "34",
              "u64": "1e3",
              "color": 1,
              "by": "__8",
              "took": "2s",
              "at": "2024-01-01T10:00:00-03:00",
              "inner": null,
            }
            """;

        var parsed = Everything.Parser.ParseFrom(Json.Encode(json, Everything.Descriptor));

        Assert.Equal(("original", 12L, 34, 1000UL, Color.Red), (parsed.SnakeCaseName, parsed.I64, parsed.I32, parsed.U64, parsed.Color));
        Assert.Equal(new byte[] { 255, 255 }, parsed.By.ToByteArray()[..2]);
        Assert.Equal(TimeSpan.FromSeconds(2), parsed.Took.ToTimeSpan());
        Assert.Equal(new DateTime(2024, 1, 1, 13, 0, 0, DateTimeKind.Utc), parsed.At.ToDateTime());
        Assert.Null(parsed.Inner);
    }

    [Theory]
    [InlineData("""{ "nope": 1 }""", "nope")]
    [InlineData("""{ "i32": "uno" }""", "i32")]
    [InlineData("""{ "i32": 3000000000 }""", "i32")]
    [InlineData("""{ "inner": { "values": [1, "x"] } }""", "inner.values[1]")]
    [InlineData("""{ "color": "AZUL" }""", "color")]
    [InlineData("""{ "at": "ayer" }""", "at")]
    [InlineData("""{ "b": "true" }""", "b")]
    [InlineData("""{ "byName": { "x": { "name": 5 } } }""", "byName[\"x\"].name")]
    public void Explains_which_field_is_wrong(string json, string field)
    {
        var error = Assert.Throws<GrpcEngineException>(() => Json.Encode(json, Everything.Descriptor));

        Assert.Contains(field, error.Message);
    }

    [Fact]
    public void Rejects_invalid_json()
    {
        var error = Assert.Throws<GrpcEngineException>(() => Json.Encode("{ \"s\": ", Everything.Descriptor));

        Assert.StartsWith(CoreTexts.Grpc_InvalidJson.Split('{')[0], error.Message);
    }

    [Fact]
    public void The_template_has_every_field_and_encodes_cleanly()
    {
        var template = ProtoJson.Template(Everything.Descriptor);
        var json = JsonDocument.Parse(template).RootElement;

        Assert.Equal("1970-01-01T00:00:00Z", json.GetProperty("at").GetString());
        Assert.Equal("COLOR_UNSPECIFIED", json.GetProperty("color").GetString());
        Assert.Equal("", json.GetProperty("inner").GetProperty("name").GetString());
        Assert.True(json.TryGetProperty("text", out _));
        Assert.False(json.TryGetProperty("nested", out _)); // del oneof, solo la primera opción
        Assert.Equal(0, json.GetProperty("wrapped").GetInt32());
        Assert.Equal(new Everything { At = new Timestamp(), Took = new Duration(), Wrapped = 0, Inner = new Inner(), Text = "", Maybe = 0,
            Meta = new Struct(), Extra = new Any(), List = new ListValue(), Mask = new FieldMask(), AnyValue = Value.ForNull() },
            Everything.Parser.ParseFrom(Json.Encode(template, Everything.Descriptor)));
    }
}
