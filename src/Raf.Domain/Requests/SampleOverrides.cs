namespace Raf.Domain.Requests;

/// <summary>
/// Override de un header o query param heredado. Si <see cref="Remove"/> es true se elimina la clave;
/// si no, se agrega o se reemplaza el valor/estado por clave.
/// </summary>
public sealed record KeyValueOverride(string Key, string? Value = null, bool? Enabled = null, bool Remove = false);

/// <summary>Solo los campos que un Sample cambia respecto de su Request. null = heredar.</summary>
public sealed record SampleOverrides(
    string? Method = null,
    string? Url = null,
    IReadOnlyList<KeyValueOverride>? Headers = null,
    IReadOnlyList<KeyValueOverride>? QueryParams = null,
    BodySpec? Body = null,
    AuthSpec? Auth = null)
{
    public static SampleOverrides Empty { get; } = new();
}
