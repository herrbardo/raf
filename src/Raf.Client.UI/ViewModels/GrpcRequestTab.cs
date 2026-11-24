using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raf.Client.Core.Execution;
using Raf.Client.Core.Grpc;
using Raf.Client.Core.History;
using Raf.Contracts;
using Raf.Domain.Requests;

namespace Raf.Client.ViewModels;

/// <summary>Un método del selector, con su tipo de llamada.</summary>
public sealed record GrpcMethodChoice(GrpcMethodInfo Info)
{
    public string Title => $"{Info.Service} / {Info.Method}";

    public string KindText => Info.Kind switch
    {
        GrpcMethodKind.Unary => Texts.Grpc_KindUnary,
        GrpcMethodKind.ServerStreaming => Texts.Grpc_KindServerStreaming,
        GrpcMethodKind.ClientStreaming => Texts.Grpc_KindClientStreaming,
        _ => Texts.Grpc_KindDuplex,
    };
}

/// <summary>
/// Pestaña de un request gRPC (Plan 10): la dirección, el método elegido entre los que informa el server reflection,
/// el mensaje en JSON, la metadata, la auth y las extracciones. Se envía y se guarda como cualquier request.
/// </summary>
public sealed partial class GrpcRequestTabViewModel : EditorTabViewModel
{
    private GrpcSpec _grpc;

    [ObservableProperty] private string? _url;
    [ObservableProperty] private string _message = "{}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStreaming), nameof(IsClientStreaming), nameof(MessageTabHeader))]
    private GrpcMethodChoice? _selectedMethod;

    [ObservableProperty] private bool _isLoadingMethods;

    /// <summary>Hay una llamada con streaming abierta (o abriéndose).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStreamIdle), nameof(StreamStateText))]
    private bool _isStreamOpen;

    /// <summary>Todavía se pueden mandar mensajes en la llamada abierta.</summary>
    [ObservableProperty] private bool _canSendToStream;

    private IGrpcStream? _stream;
    private VariableContext _streamVariables = VariableContext.Empty;
    private long _streamStarted;
    private string? _lastReceived;

    /// <summary>Por qué no hay métodos para elegir, o null si se cargaron.</summary>
    [ObservableProperty] private string? _methodsError;

    public GrpcRequestTabViewModel(MainViewModel owner, TreeNodeViewModel node) : base(owner, node, node.Id)
    {
        Headers = new KeyValueListViewModel(CanEdit);
        Auth = new AuthEditorViewModel(CanEdit);
        Extractions = new ExtractionListViewModel(CanEdit);
        Prepared = new PreparedMessagesViewModel(CanEdit, SendToStreamAsync, OnContentChanged,
            () => SelectedMethod?.Info.InputTemplate ?? "{}");
        foreach (var part in new EditorPart[] { Headers, Auth, Extractions }) part.Changed += OnContentChanged;
        _grpc = node.Request.Spec.Grpc ?? GrpcSpec.Empty;
        LoadFrom(node.Request.Spec);
        if (Engine is not null && !string.IsNullOrWhiteSpace(Url)) _ = LoadMethodsAsync(refresh: false);
    }

    private IGrpcEngine? Engine => Owner.Services.Runner.Grpc;

    /// <summary>En el cliente web no hay motor gRPC: el request se edita pero no se envía.</summary>
    public bool IsAvailable => Engine is not null;

    public KeyValueListViewModel Headers { get; }
    public AuthEditorViewModel Auth { get; }
    public ExtractionListViewModel Extractions { get; }

    /// <summary>Los mensajes preparados de un método con streaming del cliente.</summary>
    public PreparedMessagesViewModel Prepared { get; }

    /// <summary>La conversación de una llamada con streaming; las unarias usan la respuesta común.</summary>
    public ConversationViewModel Conversation { get; } = new();

    public ObservableCollection<GrpcMethodChoice> GrpcMethods { get; } = [];

    /// <summary>El método guardado, aunque todavía no se hayan cargado los del servidor.</summary>
    public string MethodText => _grpc.IsComplete ? $"{_grpc.Service} / {_grpc.Method}" : Texts.Grpc_NoMethod;

    /// <summary>El método elegido tiene streaming: se abre una conversación en lugar de esperar una respuesta.</summary>
    public bool IsStreaming => SelectedMethod is { Info.Kind: not GrpcMethodKind.Unary };

    /// <summary>El cliente manda varios mensajes (client streaming o bidireccional): se usan los mensajes preparados.</summary>
    public bool IsClientStreaming => SelectedMethod is { Info.Kind: GrpcMethodKind.ClientStreaming or GrpcMethodKind.DuplexStreaming };

    public string MessageTabHeader => IsClientStreaming ? Texts.GrpcV_Messages : Texts.GrpcV_Message;

    public bool IsStreamIdle => !IsStreamOpen;

    public string StreamStateText => IsStreamOpen ? Texts.WsV_Connected : Texts.WsV_Disconnected;

    private void LoadFrom(RequestSpec spec)
    {
        BeginLoad();
        _grpc = spec.Grpc ?? GrpcSpec.Empty;
        Url = spec.Url;
        Message = spec.Body.Content ?? "{}";
        Headers.Load(spec.Headers);
        Auth.Load(spec.Auth);
        Extractions.Load(spec.Extractions);
        Prepared.Load(_grpc.Messages);
        SelectedMethod = GrpcMethods.FirstOrDefault(m => m.Info.Service == _grpc.Service && m.Info.Method == _grpc.Method);
        OnPropertyChanged(nameof(MethodText));
        EndLoad();
    }

    private RequestSpec ToSpec() => RequestSpec.EmptyGrpc with
    {
        Url = Url ?? "",
        Headers = Headers.ToItems(),
        Body = new BodySpec(BodyType.Json, Message),
        Auth = Auth.ToSpec(),
        Extractions = Extractions.ToItems(),
        Grpc = _grpc with { Messages = Prepared.Items.Count == 0 ? null : Prepared.ToList() },
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

    [RelayCommand]
    private Task ReloadMethodsAsync() => LoadMethodsAsync(refresh: true);

    /// <summary>
    /// Pide los métodos con la dirección, la metadata y la auth del request (algunos servidores piden autenticarse
    /// también para el reflection), con las variables resueltas.
    /// </summary>
    private async Task LoadMethodsAsync(bool refresh)
    {
        if (Engine is not { } engine) return;
        IsLoadingMethods = true;
        MethodsError = null;
        try
        {
            var spec = Owner.Variables.CreateResolver().Resolve(RequestRunner.Effective(ToSpec(), null, AppAuth));
            var target = new GrpcTarget(RequestRunner.GrpcAddress(spec.Url), AcceptInvalidCertificates,
                RequestRunner.GrpcMetadata(spec), TimeSpan.FromSeconds(30));
            var methods = await engine.GetMethodsAsync(target, refresh);

            var keep = _grpc;
            GrpcMethods.Clear();
            foreach (var method in methods) GrpcMethods.Add(new GrpcMethodChoice(method));
            _grpc = keep;
            SelectedMethod = GrpcMethods.FirstOrDefault(m => m.Info.Service == keep.Service && m.Info.Method == keep.Method);
            if (GrpcMethods.Count == 0) MethodsError = Texts.Grpc_NoMethods;
        }
        catch (Exception e) when (e is GrpcEngineException or RequestBuildException or HttpRequestException)
        {
            MethodsError = Owner.Variables.Scrub(e.Message);
        }
        finally
        {
            IsLoadingMethods = false;
        }
    }

    partial void OnSelectedMethodChanged(GrpcMethodChoice? value)
    {
        OnPropertyChanged(nameof(IsStreaming));
        if (value is null) return;
        var chosen = _grpc with { Service = value.Info.Service, Method = value.Info.Method };
        if (chosen == _grpc) return;
        _grpc = chosen;
        OnPropertyChanged(nameof(MethodText));

        // Con el mensaje vacío, la plantilla del método ahorra escribir los nombres de los campos.
        if (string.IsNullOrWhiteSpace(Message) || Message.Trim() == "{}") Message = value.Info.InputTemplate;
        OnContentChanged();
    }

    /// <summary>Reemplaza el mensaje por uno con todos los campos del método elegido.</summary>
    [RelayCommand]
    private void UseTemplate()
    {
        if (SelectedMethod is { } method) Message = method.Info.InputTemplate;
    }

    partial void OnUrlChanged(string? value) => OnContentChanged();

    partial void OnMessageChanged(string value) => OnContentChanged();

    partial void OnCanSendToStreamChanged(bool value) => Prepared.CanSend = value;

    // ---------- Streaming (Plan 10, etapa 3) ----------

    /// <summary>
    /// "Enviar" y Ctrl+Enter con un método de streaming: abren la llamada o, si está abierta y acepta mensajes, mandan
    /// lo escrito. Una unaria usa el envío común. Si los métodos no se cargaron todavía, se piden para saber el tipo.
    /// </summary>
    protected override async Task<bool> TrySendCustomAsync()
    {
        if (Engine is null) return false;
        if (SelectedMethod is null && _grpc.IsComplete && !string.IsNullOrWhiteSpace(Url)) await LoadMethodsAsync(refresh: false);
        if (!IsStreaming) return false;
        if (!IsStreamOpen) await OpenStreamAsync();
        else if (CanSendToStream) await SendComposerAsync();
        return true;
    }

    [RelayCommand]
    private async Task OpenStreamAsync()
    {
        if (Engine is not { } engine || IsStreamOpen || SelectedMethod is not { } method) return;
        var (request, _) = RunTarget();
        if (!await ConfirmUnreadableSecretsAsync(request, null)) return;

        IsStreamOpen = true;
        _lastReceived = null;
        var started = Stopwatch.GetTimestamp();
        string? address = null;
        try
        {
            // El login automático de la App, si hace falta, antes de abrir la llamada.
            var input = new RunInput(request, null, Owner.Variables, new RunOptions(Timeout, AcceptInvalidCertificates),
                AppAuth, Owner.AppLoginOf(AppId));
            var login = await Owner.Services.Runner.PrepareLoginAsync(input);
            foreach (var e in login.Events)
                Conversation.Add(new ConversationEntry(e.Level == RunEventLevel.Error ? ConversationEntryKind.Error : ConversationEntryKind.Info,
                    DateTimeOffset.Now, e.Title, detail: e.Detail is null ? e.Title : e.Title + Environment.NewLine + Environment.NewLine + e.Detail));
            if (login.Token is { } token) Owner.SaveAppToken(token);
            if (login.Error is not null)
            {
                IsStreamOpen = false;
                return;
            }

            var variables = login.Variables;
            var resolver = variables.CreateResolver();
            var spec = resolver.Resolve(RequestRunner.Effective(request, null, AppAuth));
            if (resolver.Missing.Count > 0)
                Conversation.Add(new ConversationEntry(ConversationEntryKind.Info, DateTimeOffset.Now,
                    string.Format(Texts.Tab_NoValue, string.Join(", ", resolver.Missing.Order(StringComparer.Ordinal)))));
            var target = new GrpcTarget(RequestRunner.GrpcAddress(spec.Url), AcceptInvalidCertificates, RequestRunner.GrpcMetadata(spec), Timeout);
            address = target.Address.GetLeftPart(UriPartial.Authority);
            var first = method.Info.Kind == GrpcMethodKind.ServerStreaming ? spec.Body.Content ?? "{}" : "";
            Conversation.Add(new ConversationEntry(ConversationEntryKind.Info, DateTimeOffset.Now,
                string.Format(CoreTexts.Trace_GrpcCall, _grpc.Path, address),
                detail: variables.Scrub(string.Join(Environment.NewLine, target.Metadata.Select(m => $"{m.Key}: {m.Value}")))));

            var stream = await engine.StartStreamAsync(new GrpcCall(target, method.Info.Service, method.Info.Method, first));
            _stream = stream;
            _streamVariables = variables;
            _streamStarted = started;
            stream.Received += m => Dispatcher.UIThread.Post(() => OnStreamReceived(stream, m));
            stream.Completed += r => Dispatcher.UIThread.Post(() => OnStreamCompleted(stream, r));
            if (first.Length > 0)
                Conversation.Add(new ConversationEntry(ConversationEntryKind.Sent, DateTimeOffset.Now, variables.Scrub(first)));
            CanSendToStream = stream.CanSend;
            stream.Start();
        }
        catch (Exception e) when (e is GrpcEngineException or RequestBuildException or HttpRequestException)
        {
            var error = Owner.Variables.Scrub(e.Message);
            Conversation.Add(new ConversationEntry(ConversationEntryKind.Error, DateTimeOffset.Now, error));
            IsStreamOpen = false;
            await AddHistoryAsync(address ?? request.Url, RunOutcome.Failed, started, error);
        }
    }

    private void OnStreamReceived(IGrpcStream stream, GrpcStreamMessage message)
    {
        if (stream != _stream) return;
        if (message.Error is { } error)
        {
            Conversation.Add(new ConversationEntry(ConversationEntryKind.Error, message.At, error, message.Size));
            return;
        }
        _lastReceived = message.Json;
        Conversation.Add(new ConversationEntry(ConversationEntryKind.Received, message.At, message.Json ?? "", message.Size));
    }

    private void OnStreamCompleted(IGrpcStream stream, GrpcCallResult result)
    {
        if (stream != _stream) return;
        _stream = null;
        IsStreamOpen = false;
        CanSendToStream = false;

        var lines = new List<string>();
        if (!string.IsNullOrEmpty(result.StatusDetail)) lines.Add(result.StatusDetail);
        lines.Add(string.Format(Texts.WsV_Summary, Conversation.SentCount, Conversation.ReceivedCount,
            RunFormat.Time(Stopwatch.GetElapsedTime(_streamStarted))));
        if (result.Headers.Count > 0) lines.AddRange(result.Headers.Select(h => $"{h.Key}: {h.Value}").Prepend("").Prepend("Metadata:"));
        if (result.Trailers.Count > 0) lines.AddRange(result.Trailers.Select(h => $"{h.Key}: {h.Value}").Prepend(CoreTexts.Trace_GrpcTrailers).Prepend(""));
        Conversation.Add(new ConversationEntry(result.IsOk ? ConversationEntryKind.Info : ConversationEntryKind.Error, DateTimeOffset.Now,
            string.Format(CoreTexts.Trace_GrpcStatus, result.StatusName, result.StatusCode),
            detail: _streamVariables.Scrub(string.Join(Environment.NewLine, lines))));

        // G3 del plan: las extracciones leen el último mensaje recibido y la metadata y los trailers, si terminó bien.
        if (result.IsOk) Extract(result);

        var address = RequestRunner.GrpcAddress(Url ?? "").GetLeftPart(UriPartial.Authority);
        _ = AddHistoryAsync(address, RunOutcome.Completed, _streamStarted,
            result.IsOk ? null : $"{result.StatusName}: {result.StatusDetail}".TrimEnd(' ', ':'));
        _ = stream.DisposeAsync().AsTask();
    }

    private void Extract(GrpcCallResult result)
    {
        var headers = result.Headers.Concat(result.Trailers).ToList();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var extraction in (Extractions.ToItems() ?? []).Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.Variable)))
        {
            var found = ResponseExtractor.Evaluate(extraction, _lastReceived, headers);
            if (found.Value is { } value)
            {
                values[extraction.Variable.Trim()] = value;
                Conversation.Add(new ConversationEntry(ConversationEntryKind.Info, DateTimeOffset.Now,
                    string.Format(Texts.GrpcV_Extracted, extraction.Variable.Trim())));
            }
            else
            {
                Conversation.Add(new ConversationEntry(ConversationEntryKind.Info, DateTimeOffset.Now,
                    string.Format(Texts.GrpcV_NotExtracted, extraction.Variable.Trim(), found.Problem)));
            }
        }
        if (values.Count > 0) Owner.SaveSessionVariables(values);
    }

    private Task AddHistoryAsync(string url, RunOutcome outcome, long started, string? error) =>
        Owner.AddHistoryAsync(new RunHistoryEntry(
            DateTimeOffset.Now, RequestId, null, Node.Name, GrpcSpec.MethodLabel, $"{url}{_grpc.Path}", outcome, null,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, null, error));

    /// <summary>Manda un mensaje por la llamada abierta, con las variables resueltas en el momento.</summary>
    private async Task SendToStreamAsync(string text)
    {
        if (_stream is not { CanSend: true } stream) return;
        var resolver = Owner.Variables.CreateResolver();
        var resolved = resolver.Resolve(text);
        if (resolver.Missing.Count > 0)
            Conversation.Add(new ConversationEntry(ConversationEntryKind.Info, DateTimeOffset.Now,
                string.Format(Texts.Tab_NoValue, string.Join(", ", resolver.Missing.Order(StringComparer.Ordinal)))));
        try
        {
            var size = await stream.SendAsync(resolved);
            Conversation.Add(new ConversationEntry(ConversationEntryKind.Sent, DateTimeOffset.Now, Owner.Variables.Scrub(resolved), size));
        }
        catch (Exception e) when (e is GrpcEngineException or InvalidOperationException)
        {
            Conversation.Add(new ConversationEntry(ConversationEntryKind.Error, DateTimeOffset.Now, Owner.Variables.Scrub(e.Message)));
        }
    }

    [RelayCommand]
    private Task SendComposerAsync() =>
        string.IsNullOrWhiteSpace(Conversation.Composer) ? Task.CompletedTask : SendToStreamAsync(Conversation.Composer);

    /// <summary>Avisa al servidor que no se mandan más mensajes; la respuesta sigue llegando.</summary>
    [RelayCommand]
    private async Task CompleteSendingAsync()
    {
        if (_stream is not { } stream) return;
        CanSendToStream = false;
        await stream.CompleteSendingAsync();
        Conversation.Add(new ConversationEntry(ConversationEntryKind.Info, DateTimeOffset.Now, Texts.GrpcV_SendingDone));
    }

    [RelayCommand]
    private void CancelStream() => _stream?.Cancel();

    public override bool IsLive => IsStreamOpen;

    public override void OnClosing()
    {
        if (_stream is { } stream)
        {
            _stream = null;
            _ = stream.DisposeAsync().AsTask();
        }
    }
}
