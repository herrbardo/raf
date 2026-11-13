using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Raf.Client.Core.Diagnostics;

namespace Raf.Client.Core.Sync;

/// <summary>
/// Avisos en tiempo real por SignalR (solo WebSocket, sin negociación: no requiere sesiones pegajosas en el ALB).
/// El aviso no trae datos, solo dispara un pull. Mientras no hay conexión, consulta cada <c>fallbackInterval</c>;
/// al reconectar dispara un pull para recuperar lo que se perdió.
/// </summary>
public sealed class RealtimeChangeSubscriber : IChangeSubscriber
{
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private readonly HubConnection _connection;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _fallbackInterval;
    private readonly EventLog? _events;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private Guid[] _watched = [];
    private ITimer? _poll;
    private Task? _startLoop;

    public RealtimeChangeSubscriber(
        Uri server, Func<Task<string?>> accessToken, TimeProvider clock, TimeSpan fallbackInterval,
        Action<HttpConnectionOptions>? configure = null, EventLog? events = null)
    {
        _clock = clock;
        _fallbackInterval = fallbackInterval;
        _events = events;
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server, "hubs/changes"), o =>
            {
                o.Transports = HttpTransportType.WebSockets;
                o.SkipNegotiation = true;
                o.AccessTokenProvider = accessToken;
                configure?.Invoke(o);
            })
            .WithAutomaticReconnect(new ForeverRetry())
            .Build();

        _connection.On<Guid, long>("Changed", (_, _) => Raise());
        _connection.On("Refresh", Raise);
        _connection.Reconnecting += e =>
        {
            _events?.Warning(EventCategory.Connection,
                string.Format(CoreTexts.Realtime_Lost, Cause(e), Seconds(fallbackInterval)),
                e?.ToString());
            SetPolling(true);
            return Task.CompletedTask;
        };
        _connection.Reconnected += async _ =>
        {
            _events?.Info(EventCategory.Connection, CoreTexts.Realtime_Recovered);
            SetPolling(false);
            await SendWatchAsync();
            Raise(); // lo que pasó durante el corte se trae con un pull
        };
        _connection.Closed += e =>
        {
            // Si el servidor cierra la conexión (por ejemplo, el usuario fue deshabilitado), se reintenta de cero.
            if (!_stop.IsCancellationRequested)
                _events?.Warning(EventCategory.Connection, string.Format(CoreTexts.Realtime_Closed, Cause(e)), e?.ToString());
            SetPolling(true);
            if (!_stop.IsCancellationRequested) StartLoop();
            return Task.CompletedTask;
        };
    }

    public event Action? ChangeAvailable;

    /// <summary>Hay conexión en tiempo real.</summary>
    public bool IsConnected => _connection.State == HubConnectionState.Connected;

    public void Start()
    {
        SetPolling(true);
        StartLoop();
    }

    public void Watch(IReadOnlyCollection<Guid> workspaceIds)
    {
        var ids = workspaceIds.Order().ToArray();
        lock (_sync)
        {
            if (ids.SequenceEqual(_watched)) return;
            _watched = ids;
        }
        _ = SendWatchAsync();
    }

    private void Raise() => ChangeAvailable?.Invoke();

    private static string Cause(Exception? e) => e is null ? "" : $": {e.Message}";

    private static string Seconds(TimeSpan t) => string.Format(CoreTexts.Realtime_Seconds, (int)t.TotalSeconds);

    private void StartLoop()
    {
        lock (_sync)
        {
            if (_startLoop is { IsCompleted: false }) return;
            _startLoop = Task.Run(ConnectAsync);
        }
    }

    private async Task ConnectAsync()
    {
        var attempt = 0;
        while (!_stop.IsCancellationRequested && _connection.State == HubConnectionState.Disconnected)
        {
            try
            {
                await _connection.StartAsync(_stop.Token);
                _events?.Info(EventCategory.Connection,
                    attempt == 0 ? CoreTexts.Realtime_Connected : string.Format(CoreTexts.Realtime_ConnectedAfter, attempt + 1));
                SetPolling(false);
                await SendWatchAsync();
                Raise();
                return;
            }
            catch (Exception e) when (!_stop.IsCancellationRequested)
            {
                // Sin servidor, sin red o sesión vencida: se sigue por polling y se reintenta. Solo se anota el primer fallo.
                if (attempt == 0)
                    _events?.Warning(EventCategory.Connection,
                        string.Format(CoreTexts.Realtime_OpenFailed, e.Message, Seconds(_fallbackInterval)),
                        e.ToString());
                var delay = RetryDelays[Math.Min(++attempt, RetryDelays.Length - 1)];
                try
                {
                    await Task.Delay(delay, _clock, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SendWatchAsync()
    {
        Guid[] ids;
        lock (_sync) ids = _watched;
        if (_connection.State != HubConnectionState.Connected) return; // se envía al conectar
        try
        {
            await _connection.InvokeAsync<Guid[]>("Watch", ids, _stop.Token);
        }
        catch (Exception)
        {
            // Si se cortó, se vuelve a enviar al reconectar.
        }
    }

    private void SetPolling(bool on)
    {
        lock (_sync)
        {
            if (on && !_stop.IsCancellationRequested)
                _poll ??= _clock.CreateTimer(_ => Raise(), null, _fallbackInterval, _fallbackInterval);
            else
            {
                _poll?.Dispose();
                _poll = null;
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        SetPolling(false);
        _ = _connection.DisposeAsync().AsTask();
    }

    private sealed class ForeverRetry : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context) =>
            RetryDelays[Math.Min(context.PreviousRetryCount, RetryDelays.Length - 1)];
    }
}
