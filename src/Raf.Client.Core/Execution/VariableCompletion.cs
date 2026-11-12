namespace Raf.Client.Core.Execution;

/// <summary>Una variable para ofrecer al autocompletar. La descripción nunca incluye valores secretos.</summary>
public sealed record VariableSuggestion(string Key, string Description);

/// <summary>Lógica del autocompletar de <c>{{variables}}</c>, sin dependencias de la UI.</summary>
public static class VariableCompletion
{
    private const int ValuePreviewChars = 60;

    /// <summary>Los caracteres que admite un nombre de variable (igual que el resolver).</summary>
    public static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-';

    /// <summary>
    /// Si el cursor está dentro de un <c>{{</c> sin cerrar, devuelve dónde empieza y termina el nombre que se
    /// está escribiendo (justo después de <c>{{</c> y hasta el último carácter de nombre). Si no, null.
    /// </summary>
    public static (int Start, int End)? FindQuery(string line, int caret)
    {
        if (caret < 0 || caret > line.Length) return null;
        var start = caret;
        while (start > 0 && IsNameChar(line[start - 1])) start--;
        if (start < 2 || line[start - 1] != '{' || line[start - 2] != '{') return null;

        var end = caret;
        while (end < line.Length && IsNameChar(line[end])) end++;
        return (start, end);
    }

    /// <summary>Las variables disponibles, por nombre, con de dónde salen y un adelanto del valor si no es secreta.</summary>
    public static IReadOnlyList<VariableSuggestion> Suggestions(VariableContext variables) =>
        variables.Effective
            .OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
            .Select(v =>
            {
                var scope = v.EnvironmentId is null ? "Global" : "Environment activo";
                var value = v switch
                {
                    { IsSecret: true, Value: null } => "secreta, tu rol no puede leerla",
                    { IsSecret: true } => "secreta",
                    { Value: null or "" } => "sin valor",
                    _ => Preview(v.Value),
                };
                return new VariableSuggestion(v.Key, $"{scope} · {value}");
            })
            .ToList();

    /// <summary>Lo que reemplaza al nombre escrito: el nombre elegido y el cierre, si no estaba ya.</summary>
    public static string Insertion(string key, string textAfter) =>
        textAfter.TrimStart().StartsWith("}}", StringComparison.Ordinal) ? key : key + "}}";

    private static string Preview(string value)
    {
        var flat = value.ReplaceLineEndings(" ");
        return flat.Length <= ValuePreviewChars ? flat : flat[..ValuePreviewChars] + "…";
    }
}
