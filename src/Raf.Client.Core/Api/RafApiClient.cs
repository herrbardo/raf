using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Raf.Client.Core.Session;
using Raf.Contracts;

namespace Raf.Client.Core.Api;

/// <summary>Llamadas autenticadas a la API. Reintenta una vez si el servidor rechaza el access token.</summary>
public sealed class RafApiClient(HttpClient http, SessionManager session)
{
    public async Task<T> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var res = await SendAsync(HttpMethod.Get, path, null, ct);
        return (await res.Content.ReadFromJsonAsync<T>(ClientJson.Options, ct))!;
    }

    public async Task<T> PostAsync<T>(string path, object body, CancellationToken ct = default)
    {
        using var res = await SendAsync(HttpMethod.Post, path, body, ct);
        return (await res.Content.ReadFromJsonAsync<T>(ClientJson.Options, ct))!;
    }

    public async Task<T> PutAsync<T>(string path, object body, CancellationToken ct = default)
    {
        using var res = await SendAsync(HttpMethod.Put, path, body, ct);
        return (await res.Content.ReadFromJsonAsync<T>(ClientJson.Options, ct))!;
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Delete, path, null, ct);
    }

    /// <summary>El servidor revoca las demás sesiones y emite tokens nuevos, que reemplazan a los actuales.</summary>
    public async Task ChangePasswordAsync(string current, string next, CancellationToken ct = default) =>
        session.Adopt(await PostAsync<TokenResponse>("auth/change-password", new ChangePasswordRequest(current, next), ct));

    /// <summary>Comprueba que la URL corresponda a un servidor RAF accesible.</summary>
    public async Task CheckServerAsync(Uri server, CancellationToken ct = default)
    {
        try
        {
            using var res = await http.GetAsync(new Uri(server, "health"), ct);
            if (!res.IsSuccessStatusCode)
                throw new ApiException(res.StatusCode, $"No parece un servidor RAF: respondió {(int)res.StatusCode}.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new ApiException(null, "No se pudo conectar con esa URL.", e);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var token = await session.GetAccessTokenAsync(ct);
        var res = await SendOnceAsync(method, path, body, token, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            res.Dispose();
            token = await session.RefreshAfterRejectionAsync(token, ct);
            res = await SendOnceAsync(method, path, body, token, ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                res.Dispose();
                session.MarkExpired();
                throw new SessionExpiredException();
            }
        }

        if (res.IsSuccessStatusCode) return res;
        using (res) throw await ApiException.FromResponseAsync(res);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method, string path, object? body, string token, CancellationToken ct)
    {
        var server = session.Server ?? throw new InvalidOperationException("Falta indicar el servidor.");
        using var req = new HttpRequestMessage(method, new Uri(server, path));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: ClientJson.Options);

        try
        {
            return await http.SendAsync(req, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new ApiException(null, "No se pudo conectar con el servidor.", e);
        }
    }
}
