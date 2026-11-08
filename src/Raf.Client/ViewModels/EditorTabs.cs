using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raf.Client.Core.Api;
using Raf.Client.Core.Execution;
using Raf.Client.Core.History;
using Raf.Client.Core.Local;
using Raf.Client.Services;
using Raf.Contracts;
using Raf.Domain.Requests;

namespace Raf.Client.ViewModels;

/// <summary>Pestaña de edición y ejecución. Las subclases definen qué se edita y cómo se guarda.</summary>
public abstract partial class EditorTabViewModel : ObservableObject, IDisposable
{
    public static string[] Methods { get; } = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    private CancellationTokenSource? _run;
    private string _baseline = "";
    private bool _loading;

    [ObservableProperty] private bool _isDirty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(CancelCommand))]
    private bool _isRunning;

    [ObservableProperty] private string? _warning;
    [ObservableProperty] private string? _timeoutText;
    [ObservableProperty] private bool _acceptInvalidCertificates;

    protected EditorTabViewModel(MainViewModel owner, TreeNodeViewModel node, Guid requestId)
    {
        Owner = owner;
        Node = node;
        RequestId = requestId;
        Response = new ResponseViewModel(owner.Services.Dialogs);

        var options = Settings.Current.RequestOptions.GetValueOrDefault(requestId);
        _timeoutText = options?.TimeoutSeconds?.ToString();
        _acceptInvalidCertificates = options?.AcceptInvalidCertificates ?? false;
    }

    protected MainViewModel Owner { get; }

    private SettingsStore Settings => Owner.Services.Settings;

    public TreeNodeViewModel Node { get; }
    public Guid RequestId { get; }
    public ResponseViewModel Response { get; }
    public bool CanEdit => Owner.CanEdit;
    public bool IsReadOnly => !CanEdit;
    public string TimeoutHint => $"{Settings.Current.DefaultTimeoutSeconds} (predeterminado)";

    /// <summary>Lo que define si hay cambios sin guardar.</summary>
    protected abstract object Snapshot();

    protected abstract (RequestSpec Request, SampleOverrides? Sample) RunTarget();

    protected abstract Task SaveCoreAsync();

    protected abstract Task ReloadCoreAsync();

    protected void BeginLoad() => _loading = true;

    /// <summary>Lo cargado pasa a ser la referencia para detectar cambios.</summary>
    protected void EndLoad()
    {
        _loading = false;
        _baseline = Serialize();
        IsDirty = false;
        RefreshWarning();
    }

    protected void OnContentChanged()
    {
        if (_loading) return;
        IsDirty = Serialize() != _baseline;
        RefreshWarning();
    }

    private string Serialize() => JsonSerializer.Serialize(Snapshot(), ClientJson.Options);

    public void RefreshWarning()
    {
        var (request, sample) = RunTarget();
        var check = RequestRunner.Check(request, sample, Owner.Variables);
        var parts = new List<string>();
        if (check.Missing.Count > 0) parts.Add($"Sin valor: {string.Join(", ", check.Missing)}");
        if (check.UnreadableSecrets.Count > 0) parts.Add($"Secretos no disponibles para tu rol: {string.Join(", ", check.UnreadableSecrets)}");
        Warning = parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    partial void OnTimeoutTextChanged(string? value) => SaveLocalOptions();

    partial void OnAcceptInvalidCertificatesChanged(bool value) => SaveLocalOptions();

    private int? ParsedTimeout => int.TryParse(TimeoutText, out var s) && s > 0 ? s : null;

    private void SaveLocalOptions() => Settings.Update(s =>
    {
        if (ParsedTimeout is null && !AcceptInvalidCertificates) s.RequestOptions.Remove(RequestId);
        else s.RequestOptions[RequestId] = new RequestLocalOptions(ParsedTimeout, AcceptInvalidCertificates);
    });

    private bool CanSend => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var (request, sample) = RunTarget();
        var variables = Owner.Variables;
        var check = RequestRunner.Check(request, sample, variables);
        if (check.UnreadableSecrets.Count > 0 && !await Owner.Services.Dialogs.ConfirmAsync(
                "Secretos no disponibles",
                $"Este request usa variables secretas que tu rol no puede leer ({string.Join(", ", check.UnreadableSecrets)}). " +
                "Se enviará con esas variables sin reemplazar.",
                "Ejecutar igual"))
            return;

        var timeout = TimeSpan.FromSeconds(ParsedTimeout ?? Settings.Current.DefaultTimeoutSeconds);
        var input = new RunInput(request, sample, variables, new RunOptions(timeout, AcceptInvalidCertificates));

        using var cts = new CancellationTokenSource();
        _run = cts;
        IsRunning = true;
        Response.IsRunning = true;
        try
        {
            var result = await Owner.Services.Runner.RunAsync(input, cts.Token);
            Response.Show(result);
            await Owner.AddHistoryAsync(new RunHistoryEntry(
                DateTimeOffset.Now, RequestId, this is SampleTabViewModel ? Node.Id : null, Node.Name,
                RequestRunner.Effective(request, sample).Method,
                RequestRunner.DescribeUrl(request, sample, variables),
                result.Outcome, result.StatusCode, (long)result.Elapsed.TotalMilliseconds, result.Body?.Length, result.Error));
        }
        finally
        {
            _run = null;
            IsRunning = false;
            Response.IsRunning = false;
        }
    }

    private bool CanCancel => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _run?.Cancel();

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (!CanEdit) return;
        try
        {
            await SaveCoreAsync();
            Owner.ReportStatus($"Guardado: {Node.Name}");
        }
        catch (VersionConflictException)
        {
            var reload = await Owner.Services.Dialogs.ConfirmAsync(
                "Cambió en el servidor",
                $"Otra persona modificó \"{Node.Name}\". Podés recargar la versión del servidor, y tus cambios sin guardar se pierden.",
                "Recargar");
            if (reload) await GuardAsync(ReloadCoreAsync);
        }
        catch (ApiException e)
        {
            Owner.ReportStatus(e.Message);
        }
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ApiException e)
        {
            Owner.ReportStatus(e.Message);
        }
    }

    [RelayCommand]
    private Task CloseAsync() => Owner.CloseTabAsync(this);

    /// <summary>El request padre cambió; solo afecta a los samples.</summary>
    public virtual void OnParentChanged()
    {
    }

    public void Dispose()
    {
        _run?.Cancel();
        Response.Dispose();
    }
}

public sealed partial class RequestTabViewModel : EditorTabViewModel
{
    [ObservableProperty] private string _method = "GET";
    [ObservableProperty] private string? _url;

    public RequestTabViewModel(MainViewModel owner, TreeNodeViewModel node) : base(owner, node, node.Id)
    {
        Headers = new KeyValueListViewModel(CanEdit);
        Query = new KeyValueListViewModel(CanEdit);
        Body = new BodyEditorViewModel(CanEdit);
        Auth = new AuthEditorViewModel(CanEdit);
        foreach (var part in new EditorPart[] { Headers, Query, Body, Auth }) part.Changed += OnContentChanged;
        LoadFrom(node.Request.Spec);
    }

    public KeyValueListViewModel Headers { get; }
    public KeyValueListViewModel Query { get; }
    public BodyEditorViewModel Body { get; }
    public AuthEditorViewModel Auth { get; }

    private void LoadFrom(RequestSpec spec)
    {
        BeginLoad();
        Method = spec.Method;
        Url = spec.Url;
        Headers.Load(spec.Headers);
        Query.Load(spec.QueryParams);
        Body.Load(spec.Body);
        Auth.Load(spec.Auth);
        EndLoad();
    }

    public string[] AvailableMethods => Methods.Contains(Method) ? Methods : [.. Methods, Method];

    private RequestSpec ToSpec() =>
        new(Method, Url ?? "", Headers.ToItems(), Query.ToItems(), Body.ToSpec(), Auth.ToSpec());

    protected override object Snapshot() => ToSpec();

    protected override (RequestSpec, SampleOverrides?) RunTarget() => (ToSpec(), null);

    protected override async Task SaveCoreAsync()
    {
        var r = Node.Request;
        var spec = ToSpec();
        Node.Dto = await Owner.Services.Store.UpdateRequestAsync(
            Owner.WorkspaceId, r.Id, new UpdateRequestRequest(r.Name, r.FolderId, r.Order, spec, r.Version));
        LoadFrom(Node.Request.Spec);
        Owner.NotifyRequestChanged(Node);
    }

    protected override async Task ReloadCoreAsync()
    {
        Node.Dto = await Owner.Services.Store.GetRequestAsync(Owner.WorkspaceId, Node.Id);
        LoadFrom(Node.Request.Spec);
        Owner.NotifyRequestChanged(Node);
    }

    partial void OnMethodChanged(string value) => OnContentChanged();

    partial void OnUrlChanged(string? value) => OnContentChanged();
}

public sealed partial class SampleTabViewModel : EditorTabViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MethodInherited))]
    private bool _methodOverridden;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UrlInherited))]
    private bool _urlOverridden;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BodyInherited))]
    private bool _bodyOverridden;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AuthInherited))]
    private bool _authOverridden;

    [ObservableProperty] private string _method = "GET";
    [ObservableProperty] private string? _url;

    public SampleTabViewModel(MainViewModel owner, TreeNodeViewModel node)
        : base(owner, node, node.Parent!.Id)
    {
        Headers = new SampleKeyValueListViewModel(CanEdit, StringComparer.OrdinalIgnoreCase);
        Query = new SampleKeyValueListViewModel(CanEdit, StringComparer.Ordinal);
        Body = new BodyEditorViewModel(CanEdit);
        Auth = new AuthEditorViewModel(CanEdit);
        foreach (var part in new EditorPart[] { Headers, Query, Body, Auth }) part.Changed += OnContentChanged;
        LoadFrom(node.Sample.Overrides);
    }

    public SampleKeyValueListViewModel Headers { get; }
    public SampleKeyValueListViewModel Query { get; }
    public BodyEditorViewModel Body { get; }
    public AuthEditorViewModel Auth { get; }

    private RequestSpec Parent => Node.Parent!.Request.Spec;

    public bool MethodInherited => !MethodOverridden;
    public bool UrlInherited => !UrlOverridden;
    public bool BodyInherited => !BodyOverridden;
    public bool AuthInherited => !AuthOverridden;

    public string InheritedMethod => Parent.Method;
    public string InheritedUrl => Parent.Url;
    public string InheritedBody => Parent.Body.Type switch
    {
        BodyType.None => "Sin body",
        BodyType.FormUrlEncoded => "FormUrlEncoded: " + string.Join("&", (Parent.Body.Form ?? []).Select(f => $"{f.Key}={f.Value}")),
        var t => $"{t}: {Parent.Body.Content}",
    };

    public string InheritedAuth => Parent.Auth.Type switch
    {
        AuthType.None => "Sin autenticación",
        AuthType.ApiKey => $"ApiKey ({Parent.Auth.ApiKeyName}, en {Parent.Auth.ApiKeyIn})",
        var t => t.ToString(),
    };

    public string[] AvailableMethods => Methods.Contains(Method) ? Methods : [.. Methods, Method];

    private void LoadFrom(SampleOverrides o)
    {
        BeginLoad();
        MethodOverridden = o.Method is not null;
        Method = o.Method ?? Parent.Method;
        UrlOverridden = o.Url is not null;
        Url = o.Url ?? Parent.Url;
        Headers.Load(Parent.Headers, o.Headers);
        Query.Load(Parent.QueryParams, o.QueryParams);
        BodyOverridden = o.Body is not null;
        Body.Load(o.Body ?? Parent.Body);
        AuthOverridden = o.Auth is not null;
        Auth.Load(o.Auth ?? Parent.Auth);
        EndLoad();
        NotifyInherited();
    }

    private void NotifyInherited()
    {
        OnPropertyChanged(nameof(InheritedMethod));
        OnPropertyChanged(nameof(InheritedUrl));
        OnPropertyChanged(nameof(InheritedBody));
        OnPropertyChanged(nameof(InheritedAuth));
    }

    private SampleOverrides ToOverrides() => new(
        MethodOverridden ? Method : null,
        UrlOverridden ? Url ?? "" : null,
        Headers.ToOverrides(),
        Query.ToOverrides(),
        BodyOverridden ? Body.ToSpec() : null,
        AuthOverridden ? Auth.ToSpec() : null);

    protected override object Snapshot() => ToOverrides();

    protected override (RequestSpec, SampleOverrides?) RunTarget() => (Parent, ToOverrides());

    protected override async Task SaveCoreAsync()
    {
        var s = Node.Sample;
        Node.Dto = await Owner.Services.Store.UpdateSampleAsync(
            Owner.WorkspaceId, s.Id, new UpdateSampleRequest(s.Name, s.Order, ToOverrides(), s.Version));
        LoadFrom(Node.Sample.Overrides);
    }

    protected override async Task ReloadCoreAsync()
    {
        Node.Dto = await Owner.Services.Store.GetSampleAsync(Owner.WorkspaceId, Node.Id);
        LoadFrom(Node.Sample.Overrides);
    }

    /// <summary>Lo heredado cambió: se refresca sin perder lo que el sample tenga sin guardar.</summary>
    public override void OnParentChanged()
    {
        var pending = ToOverrides();
        var wasDirty = IsDirty;
        var saved = Node.Sample.Overrides;

        LoadFrom(saved);
        if (!wasDirty) return;

        MethodOverridden = pending.Method is not null;
        Method = pending.Method ?? Parent.Method;
        UrlOverridden = pending.Url is not null;
        Url = pending.Url ?? Parent.Url;
        Headers.Load(Parent.Headers, pending.Headers);
        Query.Load(Parent.QueryParams, pending.QueryParams);
        BodyOverridden = pending.Body is not null;
        Body.Load(pending.Body ?? Parent.Body);
        AuthOverridden = pending.Auth is not null;
        Auth.Load(pending.Auth ?? Parent.Auth);
    }

    [RelayCommand]
    private void OverrideMethod()
    {
        Method = Parent.Method;
        MethodOverridden = true;
    }

    [RelayCommand]
    private void InheritMethod() => MethodOverridden = false;

    [RelayCommand]
    private void OverrideUrl()
    {
        Url = Parent.Url;
        UrlOverridden = true;
    }

    [RelayCommand]
    private void InheritUrl() => UrlOverridden = false;

    [RelayCommand]
    private void OverrideBody()
    {
        Body.Load(Parent.Body);
        BodyOverridden = true;
    }

    [RelayCommand]
    private void InheritBody() => BodyOverridden = false;

    [RelayCommand]
    private void OverrideAuth()
    {
        Auth.Load(Parent.Auth);
        AuthOverridden = true;
    }

    [RelayCommand]
    private void InheritAuth() => AuthOverridden = false;

    partial void OnMethodChanged(string value) => OnContentChanged();

    partial void OnUrlChanged(string? value) => OnContentChanged();

    partial void OnMethodOverriddenChanged(bool value) => OnContentChanged();

    partial void OnUrlOverriddenChanged(bool value) => OnContentChanged();

    partial void OnBodyOverriddenChanged(bool value) => OnContentChanged();

    partial void OnAuthOverriddenChanged(bool value) => OnContentChanged();
}
