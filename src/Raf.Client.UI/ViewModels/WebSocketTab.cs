using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raf.Client.Core.Execution;
using Raf.Client.Core.History;
using Raf.Client.Core.WebSockets;
using Raf.Client.Services;
using Raf.Contracts;
using Raf.Domain.Requests;

namespace Raf.Client.ViewModels;

/// <summary>
/// Pestaña de un WebSocket (Ticket-036): la URL, query, headers y auth del handshake, los mensajes preparados y,
/// mientras está conectado, la conversación. Solo la app de escritorio conecta.
/// </summary>
public sealed partial class WebSocketTabViewModel : EditorTabViewModel
{
    private WebSocketSession? _session;
    private CancellationTokenSource? _connecting;
    private VariableContext _sessionVariables = VariableContext.Empty;
    private long _connectedAt;

    [ObservableProperty] private string? _url;
    [ObservableProperty] private string? _subprotocols;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisconnected), nameof(StateText))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisconnected), nameof(StateText))]
    private bool _isConnecting;

    public WebSocketTabViewModel(MainViewModel owner, TreeNodeViewModel node) : base(owner, node, node.Id)
    {
        Headers = new KeyValueListViewModel(CanEdit);
        Query = new KeyValueListViewModel(CanEdit);
        Auth = new AuthEditorViewModel(CanEdit);
        Prepared = new PreparedMessagesViewModel(CanEdit, SendTextAsync, OnContentChanged, () => "");
        foreach (var part in new EditorPart[] { Headers, Query, Auth }) part.Changed += OnContentChanged;
        LoadFrom(node.Request.Spec);
    }

    public KeyValueListViewModel Headers { get; }
    public KeyValueListViewModel Query { get; }
    public AuthEditorViewModel Auth { get; }
    public PreparedMessagesViewModel Prepared { get; }
    public ConversationViewModel Conversation { get; } = new();

    /// <summary>En el cliente web no se conecta: el navegador no manda headers propios en el handshake.</summary>
    public bool IsAvailable => ClientHost.Current.SupportsWebSockets;

    public bool IsDisconnected => !IsConnected && !IsConnecting;

    public string StateText => IsConnecting ? Texts.WsV_Connecting : IsConnected ? Texts.WsV_Connected : Texts.WsV_Disconnected;

    private void LoadFrom(RequestSpec spec)
    {
        BeginLoad();
        Url = spec.Url;
        Headers.Load(spec.Headers);
        Query.Load(spec.QueryParams);
        Auth.Load(spec.Auth);
        Subprotocols = string.Join(", ", spec.WebSocket?.Subprotocols ?? []);
        Prepared.Load(spec.WebSocket?.Messages);
        EndLoad();
    }

    private IReadOnlyList<string> ParsedSubprotocols =>
        (Subprotocols ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private RequestSpec ToSpec() => RequestSpec.EmptyWebSocket with
    {
        Url = Url ?? "",
        Headers = Headers.ToItems(),
        QueryParams = Query.ToItems(),
        Auth = Auth.ToSpec(),
        WebSocket = new WebSocketSpec(Prepared.ToList(), ParsedSubprotocols.Count == 0 ? null : ParsedSubprotocols),
    };

    protected override object Snapshot() => ToSpec();

    protected override (RequestSpec, SampleOverrides?) RunTarget() => (ToSpec(), null);

    protected override async Task SaveCoreAsync()
    {
        var r = Node.Request;
        Node.Dto = await Owner.Services.Store.UpdateRequestAsync(
            Owner.WorkspaceId, r.Id, new UpdateRequestRequest(r.Name, r.FolderId, r.Order, ToSpec(), r.Version));
        LoadFrom(Node.Request.Spec);
        Owner.NotifyRequestChanged(Node);
    }

    protected override async Task ReloadCoreAsync()
    {
        Node.Dto = await Owner.Services.Store.GetRequestAsync(Owner.WorkspaceId, Node.Id);
        LoadFrom(Node.Request.Spec);
        Owner.NotifyRequestChanged(Node);
    }

    partial void OnUrlChanged(string? value) => OnContentChanged();

    partial void OnSubprotocolsChanged(string? value) => OnContentChanged();

    partial void OnIsConnectedChanged(bool value) => Prepared.CanSend = value;

    // ---------- Conexión ----------

    /// <summary>"Enviar" y Ctrl+Enter: conectan si no hay conexión; si la hay, mandan lo escrito.</summary>
    protected override async Task<bool> TrySendCustomAsync()
    {
        if (IsConnected) await SendComposerAsync();
        else await ConnectAsync();
        return true;
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (!IsAvailable || IsConnected || IsConnecting) return;
        var (request, _) = RunTarget();
        if (!await ConfirmUnreadableSecretsAsync(request, null)) return;

        using var cts = new CancellationTokenSource();
        _connecting = cts;
        IsConnecting = true;
        var started = Stopwatch.GetTimestamp();
        var url = request.Url;
        try
        {
            // El login automático de la App, si hace falta, antes del handshake.
            var input = new RunInput(request, null, Variables, new RunOptions(Timeout, AcceptInvalidCertificates),
                AppAuth, Owner.AppLoginOf(AppId));
            var login = await Owner.Services.Runner.PrepareLoginAsync(input, cts.Token);
            foreach (var e in login.Events) Conversation.Add(EventEntry(e));
            if (login.Token is { } token) Owner.SaveAppToken(token);
            if (login.Error is { } loginError)
            {
                Conversation.Add(new ConversationEntry(ConversationEntryKind.Error, DateTimeOffset.Now, loginError));
                return;
            }

            var variables = login.Variables;
            var resolver = variables.CreateResolver();
            var spec = resolver.Resolve(RequestRunner.Effective(request, null, AppAuth));
            if (resolver.Missing.Count > 0)
                Conversation.Add(new ConversationEntry(ConversationEntryKind.Info, DateTimeOffset.Now,
                    string.Format(Texts.Tab_NoValue, string.Join(", ", resolver.Missing.Order(StringComparer.Ordinal)))));
            var (uri, headers) = RequestRunner.WebSocketHandshake(spec);
            url = variables.Scrub(uri.AbsoluteUri);
            Conversation.Add(new ConversationEntry(ConversationEntryKind.Info, DateTimeOffset.Now,
                string.Format(Texts.WsV_ConnectingTo, url), detail: variables.Scrub(DescribeHeaders(url, headers))));

            var session = await WebSocketSession.ConnectAsync(
                new WebSocketConnectOptions(uri, headers, ParsedSubprotocols, AcceptInvalidCertificates, Timeout), cts.Token);
            _session = session;
            _sessionVariables = variables;
            _connectedAt = Stopwatch.GetTimestamp();
            session.Received += frame => Dispatcher.UIThread.Post(() => OnReceived(session, frame));
            session.Closed += closed => Dispatcher.UIThread.Post(() => OnClosed(session, closed));
            IsConnected = true;
            Conversation.Add(new ConversationEntry(ConversationEntryKind.Info, DateTimeOffset.Now,
                session.Subprotocol is { } protocol ? string.Format(Texts.WsV_ConnectedWith, protocol) : Texts.WsV_ConnectedNow,
                detail: DescribeHeaders($"{session.Status}", session.ResponseHeaders)));
            session.Start();
            await AddHistoryAsync(url, RunOutcome.Completed, session.Status, started, null);
        }
        catch (OperationCanceledException)
        {
            Conversation.Add(new ConversationEntry(ConversationEntryKind.Info, DateTimeOffset.Now, Texts.WsV_ConnectCancelled));
        }
        catch (RequestBuildException e)
        {
            Conversation.Add(new ConversationEntry(ConversationEntryKind.Error, DateTimeOffset.Now, Variables.Scrub(e.Message)));
        }
        catch (WebSocketConnectException e)
        {
            var error = Variables.Scrub(e.Message);
            Conversation.Add(new ConversationEntry(ConversationEntryKind.Error, DateTimeOffset.Now, error,
                detail: e.Headers.Count == 0 ? error : error + Environment.NewLine + Environment.NewLine + DescribeHeaders($"{e.Status}", e.Headers)));
            await AddHistoryAsync(url, RunOutcome.Failed, e.Status, started, error);
        }
        finally
        {
            _connecting = null;
            IsConnecting = false;
        }
    }

    private static ConversationEntry EventEntry(RunEvent e) => new(
        e.Level == RunEventLevel.Error ? ConversationEntryKind.Error : ConversationEntryKind.Info, DateTimeOffset.Now, e.Title,
        detail: e.Detail is null ? e.Title : e.Title + Environment.NewLine + Environment.NewLine + e.Detail);

    private static string DescribeHeaders(string first, IReadOnlyList<KeyValuePair<string, string>> headers) =>
        string.Join(Environment.NewLine, headers.Select(h => $"{h.Key}: {h.Value}").Prepend(first));

    private Task AddHistoryAsync(string url, RunOutcome outcome, int? status, long started, string? error) =>
        Owner.AddHistoryAsync(new RunHistoryEntry(
            DateTimeOffset.Now, RequestId, null, Node.Name, WebSocketSpec.MethodLabel, url, outcome, status,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, null, error));

    private void OnReceived(WebSocketSession session, WebSocketFrame frame)
    {
        if (session != _session) return;
        var text = frame.Truncated ? frame.Text + Environment.NewLine + string.Format(Texts.WsV_Truncated, RunFormat.Size(frame.Size)) : frame.Text;
        Conversation.Add(new ConversationEntry(ConversationEntryKind.Received, frame.At,
            frame.IsBinary ? string.Format(Texts.WsV_Binary, text) : text, frame.Size));
    }

    private void OnClosed(WebSocketSession session, WebSocketClosed closed)
    {
        if (session != _session) return;
        _session = null;
        IsConnected = false;
        var duration = RunFormat.Time(Stopwatch.GetElapsedTime(_connectedAt));
        var summary = string.Format(Texts.WsV_Summary, Conversation.SentCount, Conversation.ReceivedCount, duration);
        if (closed.Error is { } error)
            Conversation.Add(new ConversationEntry(ConversationEntryKind.Error, DateTimeOffset.Now,
                string.Format(Texts.WsV_Lost, _sessionVariables.Scrub(error)), detail: summary));
        else
            Conversation.Add(new ConversationEntry(ConversationEntryKind.Info, DateTimeOffset.Now,
                closed.ByServer ? string.Format(Texts.WsV_ClosedByServer, CloseText(closed)) : Texts.WsV_ClosedByYou, detail: summary));
        _ = session.DisposeAsync().AsTask();
    }

    private static string CloseText(WebSocketClosed closed) =>
        string.IsNullOrEmpty(closed.Description) ? $"{closed.Status}" : $"{closed.Status} {closed.Description}";

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        _connecting?.Cancel();
        if (_session is { } session) await session.CloseAsync();
    }

    /// <summary>Manda un texto, con las variables resueltas en el momento. Lo usan el editor y los mensajes preparados.</summary>
    public async Task SendTextAsync(string text)
    {
        if (_session is not { IsOpen: true } session) return;
        var resolver = Variables.CreateResolver();
        var resolved = resolver.Resolve(text);
        if (resolver.Missing.Count > 0)
            Conversation.Add(new ConversationEntry(ConversationEntryKind.Info, DateTimeOffset.Now,
                string.Format(Texts.Tab_NoValue, string.Join(", ", resolver.Missing.Order(StringComparer.Ordinal)))));
        try
        {
            var frame = await session.SendTextAsync(resolved);
            // En la conversación se muestra sin los valores secretos, como en la consola.
            Conversation.Add(new ConversationEntry(ConversationEntryKind.Sent, frame.At, Variables.Scrub(frame.Text), frame.Size));
        }
        catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or IOException or ObjectDisposedException)
        {
            Conversation.Add(new ConversationEntry(ConversationEntryKind.Error, DateTimeOffset.Now, Variables.Scrub(e.Message)));
        }
    }

    [RelayCommand]
    private async Task SendComposerAsync()
    {
        if (string.IsNullOrEmpty(Conversation.Composer)) return;
        await SendTextAsync(Conversation.Composer);
    }

    public override bool IsLive => IsConnected || IsConnecting;

    /// <summary>Al cerrar la pestaña se cierra la conexión.</summary>
    public override void OnClosing()
    {
        _connecting?.Cancel();
        if (_session is { } session)
        {
            _session = null;
            _ = session.DisposeAsync().AsTask();
        }
    }
}
