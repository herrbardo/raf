using CommunityToolkit.Mvvm.ComponentModel;
using Raf.Client.Controls;
using Raf.Client.Core.Execution;
using Raf.Contracts;
using Raf.Domain.Requests;

namespace Raf.Client.ViewModels;

/// <summary>Una opción del combo de vencimiento del token.</summary>
public sealed record TokenExpiryOption(TokenExpiry Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// La autenticación de una App, que usan sus requests con auth <see cref="AuthType.Inherit"/>, y su login
/// automático para obtener el token.
/// </summary>
public sealed partial class AppAuthViewModel : ObservableObject
{
    public static string[] Methods => EditorTabViewModel.Methods;

    public static TokenExpiryOption[] ExpiryOptions { get; } =
    [
        new(TokenExpiry.JwtExp, Texts.AppLogin_ExpiryJwt),
        new(TokenExpiry.ResponseField, Texts.AppLogin_ExpiryField),
        new(TokenExpiry.Fixed, Texts.AppLogin_ExpiryFixed),
        new(TokenExpiry.None, Texts.AppLogin_ExpiryNone),
    ];

    private readonly AppLogin? _original;

    [ObservableProperty] private bool _loginEnabled;
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

    /// <summary>Por qué no se puede guardar; se borra al volver a intentarlo.</summary>
    [ObservableProperty] private string? _error;

    public AppAuthViewModel(AppDto app, bool canEdit, VariableLookup? lookup = null)
    {
        Title = string.Format(Texts.AppAuth_Title, app.Name);
        CanEdit = canEdit;
        Lookup = lookup;
        Auth = new AuthEditorViewModel(canEdit, allowInherit: false);
        Auth.Load(app.Auth ?? AuthSpec.None);

        _original = app.Login;
        var login = app.Login ?? AppLogin.Default;
        _loginEnabled = login.Enabled;
        _method = login.Request.Method;
        _url = login.Request.Url;
        Query = new KeyValueListViewModel(canEdit);
        Query.Load(login.Request.QueryParams);
        Headers = new KeyValueListViewModel(canEdit);
        Headers.Load(login.Request.Headers);
        Body = new BodyEditorViewModel(canEdit);
        Body.Load(login.Request.Body);
        LoginAuth = new AuthEditorViewModel(canEdit, allowInherit: false);
        LoginAuth.Load(login.Request.Auth);
        _tokenSource = login.TokenSource;
        _tokenPath = login.TokenPath;
        _variable = login.Variable;
        _expiry = ExpiryOptions.First(o => o.Value == login.Expiry);
        _expiryPath = login.ExpiryPath;
        _expiryMinutes = login.ExpiryMinutes?.ToString();
    }

    public string Title { get; }
    public bool CanEdit { get; }
    public bool IsReadOnly => !CanEdit;

    /// <summary>Las variables vigentes, para resaltar y completar <c>{{variables}}</c> en los campos de la ventana.</summary>
    public VariableLookup? Lookup { get; }

    public AuthEditorViewModel Auth { get; }

    public KeyValueListViewModel Query { get; }
    public KeyValueListViewModel Headers { get; }
    public BodyEditorViewModel Body { get; }
    public AuthEditorViewModel LoginAuth { get; }

    public ExtractionSource[] Sources => ExtractionListViewModel.Sources;
    public string[] AvailableMethods => Methods.Contains(Method) ? Methods : [.. Methods, Method];
    public bool IsExpiryField => Expiry.Value == TokenExpiry.ResponseField;
    public bool IsExpiryFixed => Expiry.Value == TokenExpiry.Fixed;

    /// <summary>La auth a guardar; null si se cerró sin guardar.</summary>
    public AuthSpec? Result { get; private set; }

    /// <summary>El login a guardar; null si no cambia (por ejemplo, una App que nunca lo usó y lo deja apagado).</summary>
    public AppLogin? LoginResult { get; private set; }

    /// <summary>Al encender el login con la App sin auth, la deja en Bearer con la variable del token.</summary>
    partial void OnLoginEnabledChanged(bool value)
    {
        if (value && Auth.Type == AuthType.None)
            Auth.Load(new AuthSpec(AuthType.Bearer, Token: $"{{{{{(string.IsNullOrWhiteSpace(Variable) ? AppLogin.DefaultVariable : Variable.Trim())}}}}}"));
    }

    /// <summary>Valida y deja el resultado. Devuelve false, con el motivo en <see cref="Error"/>, si falta algo.</summary>
    public bool Accept()
    {
        var login = ToLogin();
        if (login.Problem() is var problem and not AppLoginProblem.None)
        {
            Error = AppLoginTexts.Describe(problem);
            return false;
        }

        Error = null;
        Result = Auth.ToSpec();
        LoginResult = _original is null && !login.Enabled ? null : login;
        return true;
    }

    private AppLogin ToLogin()
    {
        int? minutes = int.TryParse(ExpiryMinutes?.Trim(), out var m) ? m : null;
        var request = new RequestSpec(
            string.IsNullOrWhiteSpace(Method) ? "POST" : Method.Trim(), (Url ?? "").Trim(),
            Headers.ToItems(), Query.ToItems(), Body.ToSpec(), LoginAuth.ToSpec());
        return new AppLogin(
            LoginEnabled, request, TokenSource, (TokenPath ?? "").Trim(), (Variable ?? "").Trim(), Expiry.Value,
            Expiry.Value == TokenExpiry.ResponseField ? ExpiryPath?.Trim() : null,
            Expiry.Value == TokenExpiry.Fixed ? (minutes ?? 0) : null);
    }
}
