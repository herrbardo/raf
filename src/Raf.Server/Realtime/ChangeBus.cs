using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Npgsql;
using Raf.Server.Data;

namespace Raf.Server.Realtime;

public enum BusEvent
{
    /// <summary>Un workspace tiene cambios nuevos.</summary>
    WorkspaceChanged,

    /// <summary>Al usuario le conviene sincronizar (por ejemplo, lo agregaron a un workspace).</summary>
    UserRefresh,

    /// <summary>El usuario fue deshabilitado: se cortan sus conexiones.</summary>
    UserDisabled,
}

public sealed record BusMessage(BusEvent Event, Guid Id, long Seq = 0);

/// <summary>
/// Reparte los avisos entre todas las tareas del servidor con <c>LISTEN/NOTIFY</c> de Postgres. Cada tarea los
/// entrega a sus propias conexiones; con una sola tarea, el camino es el mismo.
/// </summary>
public interface IChangeBus
{
    /// <summary>No propaga errores: un aviso perdido solo demora la sincronización hasta el próximo pull.</summary>
    Task PublishAsync(BusMessage message, CancellationToken ct = default);
}

public sealed class PostgresChangeBus(NpgsqlDataSource dataSource, ILogger<PostgresChangeBus> log) : IChangeBus
{
    public const string Channel = "raf_changes";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(BusMessage message, CancellationToken ct = default)
    {
        try
        {
            await using var cmd = dataSource.CreateCommand("SELECT pg_notify(@channel, @payload)");
            cmd.Parameters.AddWithValue("channel", Channel);
            cmd.Parameters.AddWithValue("payload", JsonSerializer.Serialize(message, Json));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            log.LogWarning(e, "No se pudo publicar el aviso {Event} de {Id}", message.Event, message.Id);
        }
    }
}

/// <summary>Los cambios confirmados en un workspace salen como aviso a sus suscriptores.</summary>
public sealed class BusChangeNotifier(IChangeBus bus) : IChangeNotifier
{
    public async ValueTask NotifyAsync(Guid workspaceId, long changeSeq) =>
        await bus.PublishAsync(new BusMessage(BusEvent.WorkspaceChanged, workspaceId, changeSeq));
}

/// <summary>Escucha el canal de Postgres y entrega cada aviso a las conexiones de esta tarea. Se reconecta solo.</summary>
public sealed class ChangeBusListener(
    NpgsqlDataSource dataSource,
    IHubContext<ChangesHub, IChangesClient> hub,
    ConnectionRegistry registry,
    ILogger<ChangeBusListener> log) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(ct);
                connection.Notification += (_, e) => _ = DispatchAsync(e.Payload);
                await using (var listen = new NpgsqlCommand($"LISTEN {PostgresChangeBus.Channel}", connection))
                    await listen.ExecuteNonQueryAsync(ct);

                while (!ct.IsCancellationRequested) await connection.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // Mientras no haya conexión, los clientes siguen sincronizando por polling.
                log.LogWarning(e, "Se cortó la escucha de avisos; se reintenta en {Delay}", RetryDelay);
                try
                {
                    await Task.Delay(RetryDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    internal async Task DispatchAsync(string payload)
    {
        try
        {
            var message = JsonSerializer.Deserialize<BusMessage>(payload, PostgresChangeBus.Json);
            switch (message?.Event)
            {
                case BusEvent.WorkspaceChanged:
                    await hub.Clients.Group(ChangesHub.WorkspaceGroup(message.Id)).Changed(message.Id, message.Seq);
                    break;
                case BusEvent.UserRefresh:
                    await hub.Clients.Group(ChangesHub.UserGroup(message.Id)).Refresh();
                    break;
                case BusEvent.UserDisabled:
                    registry.AbortUser(message.Id);
                    break;
            }
        }
        catch (Exception e)
        {
            log.LogWarning(e, "No se pudo entregar el aviso {Payload}", payload);
        }
    }
}

public static class RealtimeSetup
{
    public static IServiceCollection AddRealtime(this IServiceCollection services, IConfiguration config)
    {
        services.AddSignalR();
        services.AddSingleton(_ => NpgsqlDataSource.Create(Infrastructure.DatabaseConfig.GetConnectionString(config)));
        services.AddSingleton<ConnectionRegistry>();
        services.AddSingleton<IChangeBus, PostgresChangeBus>();
        services.AddSingleton<BusChangeNotifier>();
        services.AddSingleton<IChangeNotifier>(sp => sp.GetRequiredService<BusChangeNotifier>());
        services.AddHostedService<ChangeBusListener>();
        return services;
    }
}
