using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Raf.Domain.Entities;
using Raf.Server.Auth;
using Raf.Server.Data;

namespace Raf.Server.Infrastructure;

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? UserId =>
        accessor.HttpContext?.User.FindFirst(RafClaims.Subject)?.Value is { } sub ? Guid.Parse(sub) : null;
}

/// <summary>Traduce errores de base de datos esperables a respuestas 409 / 400.</summary>
public sealed class DbExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception ex, CancellationToken ct)
    {
        var (status, title) = ex switch
        {
            DbUpdateConcurrencyException =>
                (StatusCodes.Status409Conflict, "El elemento fue modificado por otro usuario. Recargalo y volvé a intentar."),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } =>
                (StatusCodes.Status409Conflict, "Ya existe un elemento con esos datos."),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } } =>
                (StatusCodes.Status400BadRequest, "Referencia a un elemento inexistente."),
            _ => (0, null),
        };
        if (title is null) return false;

        http.Response.StatusCode = status;
        return await problems.TryWriteAsync(new()
        {
            HttpContext = http,
            Exception = ex,
            ProblemDetails = { Status = status, Title = title },
        });
    }
}

public static class Problems
{
    public static IResult BadRequest(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Solicitud inválida", detail: detail);

    public static IResult NotFound(string what = "Elemento") =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: $"{what} no encontrado");

    public static IResult Conflict(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflicto", detail: detail);

    /// <summary>La versión enviada por el cliente no coincide con la del servidor.</summary>
    public static IResult VersionConflict(SyncEntity current) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflicto de versión",
            detail: "El elemento fue modificado por otro usuario. Recargalo y volvé a intentar.",
            extensions: new Dictionary<string, object?> { ["currentVersion"] = current.Version });

    public static IResult? ValidateName(string? name, string field = "name") =>
        string.IsNullOrWhiteSpace(name) ? BadRequest($"'{field}' es obligatorio.")
        : name.Length > 200 ? BadRequest($"'{field}' admite como máximo 200 caracteres.")
        : null;
}
