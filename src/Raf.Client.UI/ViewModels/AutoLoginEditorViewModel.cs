using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Raf.Client.Core.Execution;
using Raf.Domain.Requests;

namespace Raf.Client.ViewModels;

/// <summary>Una opción del combo de vencimiento del token.</summary>
public sealed record TokenExpiryOption(TokenExpiry Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Editor de la configuración de un login automático (Plan 08): el request de login, dónde está el token, la
/// variable de sesión donde se guarda y cómo se calcula el vencimiento.
/// </summary>
public sealed partial class AutoLoginEditorViewModel : ObservableObject
{
    public static TokenExpiryOption[] ExpiryOptions { get; } =
    [
        new(TokenExpiry.JwtExp, Texts.AppLogin_ExpiryJwt),
        new(TokenExpiry.ResponseField, Texts.AppLogin_ExpiryField),
        new(TokenExpiry.Fixed, Texts.AppLogin_ExpiryFixed),
        new(TokenExpiry.None, Texts.AppLogin_ExpiryNone),
    ];

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _method;
    [ObservableProperty] private string? _url;
    [ObservableProperty] private ExtractionSource _tokenSource;
    [ObservableProperty] private string? _tokenPath;
    [ObservableProperty] private string? _variable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExpiryField), nameof(IsExpiryFixed))]
    private TokenExpiryOption _expiry;

    [ObservableProperty] private string? _expiryPath;
    [ObservableProperty] private string? _expiryMinutes;

    public AutoLoginEditorViewModel(AppLogin login, bool canEdit)
    {
        CanEdit = canEdit;
        _enabled = login.Enabled;
        _method = login.Request.Method;
        _url = login.Request.Url;
        Query = new KeyValueListViewModel(canEdit);
        Query.Load(login.Request.QueryParams);
        Headers = new KeyValueListViewModel(canEdit);
        Headers.Load(login.Request.Headers);
        Body = new BodyEditorViewModel(canEdit);
        Body.Load(login.Request.Body);
        Auth = new AuthEditorViewModel(canEdit, allowInherit: false);
        Auth.Load(login.Request.Auth);
        _tokenSource = login.TokenSource;
        _tokenPath = login.TokenPath;
        _variable = login.Variable;
        _expiry = ExpiryOptions.First(o => o.Value == login.Expiry);
        _expiryPath = login.ExpiryPath;
        _expiryMinutes = login.ExpiryMinutes?.ToString();
    }

    public bool CanEdit { get; }
    public bool IsReadOnly => !CanEdit;

    public KeyValueListViewModel Query { get; }
    public KeyValueListViewModel Headers { get; }
    public BodyEditorViewModel Body { get; }
    public AuthEditorViewModel Auth { get; }

    public ExtractionSource[] Sources => ExtractionListViewModel.Sources;
    public string[] AvailableMethods => EditorTabViewModel.Methods.Contains(Method) ? EditorTabViewModel.Methods : [.. EditorTabViewModel.Methods, Method];
    public bool IsExpiryField => Expiry.Value == TokenExpiry.ResponseField;
    public bool IsExpiryFixed => Expiry.Value == TokenExpiry.Fixed;

    public AppLogin ToLogin()
    {
        int? minutes = int.TryParse(ExpiryMinutes?.Trim(), out var m) ? m : null;
        var request = new RequestSpec(
            string.IsNullOrWhiteSpace(Method) ? "POST" : Method.Trim(), (Url ?? "").Trim(),
            Headers.ToItems(), Query.ToItems(), Body.ToSpec(), Auth.ToSpec());
        return new AppLogin(
            Enabled, request, TokenSource, (TokenPath ?? "").Trim(), (Variable ?? "").Trim(), Expiry.Value,
            Expiry.Value == TokenExpiry.ResponseField ? ExpiryPath?.Trim() : null,
            Expiry.Value == TokenExpiry.Fixed ? (minutes ?? 0) : null);
    }

    /// <summary>Si lo editado difiere de <paramref name="saved"/>.</summary>
    public bool DiffersFrom(AppLogin saved) => Serialize(ToLogin()) != Serialize(saved);

    /// <summary>Qué le falta para guardarse, en el idioma de la interfaz; null si está completo.</summary>
    public string? Problem() => ToLogin().Problem() is var problem and not AppLoginProblem.None ? AppLoginTexts.Describe(problem) : null;

    private static string Serialize(AppLogin login) => JsonSerializer.Serialize(login, Core.Api.ClientJson.Options);
}
