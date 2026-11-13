using CommunityToolkit.Mvvm.ComponentModel;
using Raf.Contracts;
using Raf.Domain.Requests;

namespace Raf.Client.ViewModels;

/// <summary>La autenticación de una App, que usan sus requests con auth <see cref="AuthType.Inherit"/>.</summary>
public sealed class AppAuthViewModel : ObservableObject
{
    public AppAuthViewModel(AppDto app, bool canEdit)
    {
        Title = string.Format(Texts.AppAuth_Title, app.Name);
        CanEdit = canEdit;
        Auth = new AuthEditorViewModel(canEdit, allowInherit: false);
        Auth.Load(app.Auth ?? AuthSpec.None);
    }

    public string Title { get; }
    public bool CanEdit { get; }
    public AuthEditorViewModel Auth { get; }

    /// <summary>La auth a guardar; null si se cerró sin guardar.</summary>
    public AuthSpec? Result { get; private set; }

    public void Accept() => Result = Auth.ToSpec();
}
