using CommunityToolkit.Mvvm.ComponentModel;
using Raf.Client.Controls;
using Raf.Contracts;
using Raf.Domain.Requests;

namespace Raf.Client.ViewModels;

/// <summary>Una opción del combo de logins: uno del workspace, o "Ninguno" (<see cref="Login"/> null).</summary>
public sealed record LoginChoice(LoginDto? Login, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// La autenticación de una App, que usan sus requests con auth <see cref="AuthType.Inherit"/>, y el login
/// compartido del workspace que obtiene el token.
/// </summary>
public sealed partial class AppAuthViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoginSummary), nameof(LoginIsOff))]
    private LoginChoice _selectedLogin;

    public AppAuthViewModel(AppDto app, IReadOnlyList<LoginDto> logins, bool canEdit, VariableLookup? lookup = null)
    {
        Title = string.Format(Texts.AppAuth_Title, app.Name);
        CanEdit = canEdit;
        Lookup = lookup;
        Auth = new AuthEditorViewModel(canEdit, allowInherit: false);
        Auth.Load(app.Auth ?? AuthSpec.None);

        LoginChoices = [new LoginChoice(null, Texts.AppLogin_None), .. logins.Select(l => new LoginChoice(l, l.Name))];

        // Un login que ya no está en la lista (por ejemplo, borrado por otro usuario) equivale a ninguno.
        _selectedLogin = LoginChoices.FirstOrDefault(c => c.Login?.Id == app.LoginId) ?? LoginChoices[0];
        _originalLoginId = _selectedLogin.Login?.Id;
    }

    private readonly Guid? _originalLoginId;

    public string Title { get; }
    public bool CanEdit { get; }

    /// <summary>Las variables vigentes, para resaltar y completar <c>{{variables}}</c> en los campos de la ventana.</summary>
    public VariableLookup? Lookup { get; }

    public AuthEditorViewModel Auth { get; }

    public IReadOnlyList<LoginChoice> LoginChoices { get; }

    /// <summary>Qué hace el login elegido, para no tener que abrir la ventana de logins.</summary>
    public string? LoginSummary => SelectedLogin.Login is { } l
        ? string.Format(Texts.AppLogin_Summary, l.Config.Request.Method, l.Config.Request.Url, l.Config.Variable)
        : null;

    public bool LoginIsOff => SelectedLogin.Login is { Config.Enabled: false };

    /// <summary>La auth a guardar; null si se cerró sin guardar.</summary>
    public AuthSpec? Result { get; private set; }

    /// <summary>El login elegido: su id, <see cref="Guid.Empty"/> para ninguno, o null si no cambió.</summary>
    public Guid? LoginIdResult { get; private set; }

    /// <summary>Al elegir un login con la App sin auth, la deja en Bearer con la variable del token.</summary>
    partial void OnSelectedLoginChanged(LoginChoice value)
    {
        if (value.Login is { } login && Auth.Type == AuthType.None)
        {
            var variable = string.IsNullOrWhiteSpace(login.Config.Variable) ? AppLogin.DefaultVariable : login.Config.Variable.Trim();
            Auth.Load(new AuthSpec(AuthType.Bearer, Token: $"{{{{{variable}}}}}"));
        }
    }

    public void Accept()
    {
        Result = Auth.ToSpec();
        var chosen = SelectedLogin.Login?.Id;
        LoginIdResult = chosen == _originalLoginId ? null : chosen ?? Guid.Empty;
    }
}
