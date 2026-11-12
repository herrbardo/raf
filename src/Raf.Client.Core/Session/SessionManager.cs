using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Raf.Client.Core.Api;
using Raf.Client.Core.Diagnostics;
using Raf.Client.Core.Local;
using Raf.Contracts;

namespace Raf.Client.Core.Session;

public static class ServerUrl
{
    /// <summary>
    /// Normaliza la URL ingresada, conservando el subpath y terminando en "/" para que las rutas relativas lo
    /// respeten. Sin esquema se asume https. Devuelve null si no es una URL http(s) válida.
    /// </summary>
    public static Uri? Normalize(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        var path = uri.AbsolutePath.TrimEnd('/') + "/";
        return new UriBuilder(uri) { Path = path, Query = "", Fragment = "" }.Uri;
    }
}

/// <summary>
/// Dueño de los tokens. El servidor trata la reutilización de un refresh token como robo y cierra todas las
/// sesiones, así que cada renovación debe usar el último token emitido: hay una sola en curso por proceso,
/// no se cancela aunque el llamador desista (la respuesta trae el token que reemplaza al actual) y un lock de
/// archivo la serializa entre instancias de la app.
/// </summary>
public sealed class SessionManager(
    HttpClient http, ISecureStore store, TimeProvider clock, string lockPath, EventLog? events = null)
{
    private const string StoreKey = "session";
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(30);

    private sealed record StoredSession(string Server, TokenResponse Tokens);

    private readonly object _sync = new();
    private StoredSession? _session;
    private Task<TokenResponse>? _refresh;

    /// <summary>La sesión dejó de ser válida en el servidor. Se dispara en el hilo que lo detectó.</summary>
    public event Action? SessionExpired;

    public Uri? Server { get; private set; }

    public UserDto? User => Volatile.Read(ref _session)?.Tokens.User;

    public void SetServer(Uri server) => Server = server;

    /// <summary>Recupera la sesión guardada para el servidor actual, si existe.</summary>
    public bool TryRestore()
    {
        var stored = ReadStored();
        if (stored is null || stored.Server != Server?.ToString()) return false;
        if (stored.Tokens.RefreshTokenExpiresAt <= clock.GetUtcNow())
        {
            events?.Info(EventCategory.Session, "La sesión guardada venció: hay que volver a iniciarla.");
            return false;
        }
        lock (_sync) _session = stored;
        events?.Info(EventCategory.Session, $"Se recuperó la sesión guardada en {Server} como {stored.Tokens.User.Email}.");
        return true;
    }

    public async Task<UserDto> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var server = Server ?? throw new InvalidOperationException("Falta indicar el servidor.");
        HttpResponseMessage res;
        try
        {
            res = await http.PostAsJsonAsync(new Uri(server, "auth/login"), new LoginRequest(email, password), ClientJson.Options, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            events?.Warning(EventCategory.Session, $"No se pudo conectar con {server} para iniciar sesión: {e.Message}", e.ToString());
            throw new ApiException(null, "No se pudo conectar con el servidor.", e);
        }

        using (res)
        {
            if (!res.IsSuccessStatusCode)
            {
                var error = await ApiException.FromResponseAsync(res);
                events?.Warning(EventCategory.Session, $"Inicio de sesión rechazado (HTTP {(int)res.StatusCode}): {error.Message}");
                throw error;
            }
            var tokens = (await res.Content.ReadFromJsonAsync<TokenResponse>(ClientJson.Options, ct))!;
            Adopt(tokens);
            events?.Info(EventCategory.Session, $"Sesión iniciada en {server} como {tokens.User.Email}.");
            return tokens.User;
        }
    }

    /// <summary>Reemplaza la sesión por tokens recién emitidos (login o cambio de contraseña).</summary>
    public void Adopt(TokenResponse tokens)
    {
        var session = new StoredSession(Server!.ToString(), tokens);
        lock (_sync) _session = session;
        Persist(session);
    }

    /// <summary>Devuelve un access token vigente, renovándolo si hace falta.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var current = Volatile.Read(ref _session) ?? throw new SessionExpiredException();
        if (current.Tokens.AccessTokenExpiresAt - ExpiryMargin > clock.GetUtcNow()) return current.Tokens.AccessToken;
        return (await RefreshSharedAsync(current.Tokens.AccessToken).WaitAsync(ct)).AccessToken;
    }

    /// <summary>El servidor rechazó <paramref name="rejectedToken"/>: obtiene uno nuevo, salvo que ya se haya renovado.</summary>
    public async Task<string> RefreshAfterRejectionAsync(string rejectedToken, CancellationToken ct = default) =>
        (await RefreshSharedAsync(rejectedToken).WaitAsync(ct)).AccessToken;

    public void MarkExpired()
    {
        events?.Warning(EventCategory.Session, "El servidor ya no acepta la sesión: hay que volver a iniciarla.");
        Clear();
        SessionExpired?.Invoke();
    }

    public async Task LogoutAsync()
    {
        Task? inFlight;
        lock (_sync) inFlight = _refresh;
        if (inFlight is not null)
            await inFlight.ContinueWith(_ => { }, TaskScheduler.Default); // su resultado no importa: solo debe terminar

        var refreshToken = Volatile.Read(ref _session)?.Tokens.RefreshToken;
        var server = Server;
        Clear();
        if (refreshToken is null || server is null) return;
        events?.Info(EventCategory.Session, "Sesión cerrada.");

        try
        {
            using var _ = await http.PostAsJsonAsync(new Uri(server, "auth/logout"), new LogoutRequest(refreshToken), ClientJson.Options);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // Sin conexión la sesión local ya quedó cerrada; el token vence solo.
        }
    }

    private Task<TokenResponse> RefreshSharedAsync(string staleAccessToken)
    {
        lock (_sync)
        {
            if (_session is null) return Task.FromException<TokenResponse>(new SessionExpiredException());
            if (_session.Tokens.AccessToken != staleAccessToken) return Task.FromResult(_session.Tokens);
            return _refresh ??= RunRefreshAsync(_session);
        }
    }

    private async Task<TokenResponse> RunRefreshAsync(StoredSession session)
    {
        await Task.Yield();
        try
        {
            using var fileLock = await AcquireFileLockAsync();

            // Otra instancia pudo haber renovado o cerrado la sesión mientras se esperaba el lock.
            var stored = ReadStored();
            if (stored is null || stored.Server != session.Server)
            {
                MarkExpired();
                throw new SessionExpiredException();
            }
            if (stored.Tokens.RefreshToken != session.Tokens.RefreshToken)
            {
                lock (_sync) _session = stored;
                return stored.Tokens;
            }

            HttpResponseMessage res;
            try
            {
                res = await http.PostAsJsonAsync(
                    new Uri(new Uri(session.Server), "auth/refresh"),
                    new RefreshRequest(session.Tokens.RefreshToken), ClientJson.Options, CancellationToken.None);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                events?.Warning(EventCategory.Session, $"No se pudo renovar la sesión, sin conexión con el servidor: {e.Message}", e.ToString());
                throw new ApiException(null, "No se pudo conectar con el servidor.", e);
            }

            using (res)
            {
                if (res.StatusCode == HttpStatusCode.Unauthorized)
                {
                    MarkExpired();
                    throw new SessionExpiredException();
                }
                if (!res.IsSuccessStatusCode)
                {
                    var error = await ApiException.FromResponseAsync(res);
                    events?.Warning(EventCategory.Session, $"No se pudo renovar la sesión (HTTP {(int)res.StatusCode}): {error.Message}");
                    throw error;
                }

                var tokens = (await res.Content.ReadFromJsonAsync<TokenResponse>(ClientJson.Options))!;
                var renewed = session with { Tokens = tokens };
                lock (_sync) _session = renewed;
                Persist(renewed);
                events?.Info(EventCategory.Session, "Se renovó el token de acceso.");
                return tokens;
            }
        }
        finally
        {
            lock (_sync) _refresh = null;
        }
    }

    private async Task<IDisposable> AcquireFileLockAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var deadline = clock.GetUtcNow() + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (clock.GetUtcNow() < deadline)
            {
                await Task.Delay(50);
            }
        }
    }

    private StoredSession? ReadStored()
    {
        var json = store.Read(StoreKey);
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<StoredSession>(json, ClientJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Persist(StoredSession session) =>
        store.Write(StoreKey, JsonSerializer.Serialize(session, ClientJson.Options));

    private void Clear()
    {
        lock (_sync) _session = null;
        store.Delete(StoreKey);
    }
}
