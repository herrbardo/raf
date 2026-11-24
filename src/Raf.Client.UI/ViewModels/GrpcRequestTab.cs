using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raf.Client.Core.Execution;
using Raf.Client.Core.Grpc;
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
    [ObservableProperty] private GrpcMethodChoice? _selectedMethod;
    [ObservableProperty] private bool _isLoadingMethods;

    /// <summary>Por qué no hay métodos para elegir, o null si se cargaron.</summary>
    [ObservableProperty] private string? _methodsError;

    public GrpcRequestTabViewModel(MainViewModel owner, TreeNodeViewModel node) : base(owner, node, node.Id)
    {
        Headers = new KeyValueListViewModel(CanEdit);
        Auth = new AuthEditorViewModel(CanEdit);
        Extractions = new ExtractionListViewModel(CanEdit);
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

    public ObservableCollection<GrpcMethodChoice> GrpcMethods { get; } = [];

    /// <summary>El método guardado, aunque todavía no se hayan cargado los del servidor.</summary>
    public string MethodText => _grpc.IsComplete ? $"{_grpc.Service} / {_grpc.Method}" : Texts.Grpc_NoMethod;

    /// <summary>Los streaming llegan en la etapa 3 del plan: por ahora se avisa en lugar de fallar al enviar.</summary>
    public bool IsStreaming => SelectedMethod is { Info.Kind: not GrpcMethodKind.Unary };

    private void LoadFrom(RequestSpec spec)
    {
        BeginLoad();
        _grpc = spec.Grpc ?? GrpcSpec.Empty;
        Url = spec.Url;
        Message = spec.Body.Content ?? "{}";
        Headers.Load(spec.Headers);
        Auth.Load(spec.Auth);
        Extractions.Load(spec.Extractions);
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
        Grpc = _grpc,
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
}
