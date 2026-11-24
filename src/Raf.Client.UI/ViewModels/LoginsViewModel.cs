using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raf.Client.Controls;
using Raf.Client.Core.Api;
using Raf.Client.Core.Workspaces;
using Raf.Client.Services;
using Raf.Contracts;

namespace Raf.Client.ViewModels;

/// <summary>Un login de la lista, con su editor; los cambios quedan en el editor hasta guardar.</summary>
public sealed partial class LoginItemViewModel : ObservableObject
{
    [ObservableProperty] private string _name;
    [ObservableProperty] private LoginDto _saved;

    public LoginItemViewModel(LoginDto saved, bool canEdit, IReadOnlyList<string> usedBy)
    {
        _saved = saved;
        _name = saved.Name;
        Editor = new AutoLoginEditorViewModel(saved.Config, canEdit);
        UsedBy = usedBy;
    }

    public AutoLoginEditorViewModel Editor { get; }

    /// <summary>Las Apps que usan este login.</summary>
    public IReadOnlyList<string> UsedBy { get; }

    public string UsedByText => UsedBy.Count == 0 ? Texts.LoginsW_Unused : string.Format(Texts.LoginsW_UsedBy, string.Join(", ", UsedBy));

    public bool HasChanges => Editor.DiffersFrom(Saved.Config);
}

/// <summary>Los logins compartidos del workspace (Plan 08, etapa 4): las Apps eligen uno para obtener el token.</summary>
public sealed partial class LoginsViewModel(ClientServices services, Guid workspaceId, bool canEdit, VariableLookup? lookup)
    : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand), nameof(DuplicateCommand), nameof(DeleteCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private LoginItemViewModel? _selected;

    [ObservableProperty] private string? _error;

    /// <summary>Resultado del último guardado; se borra con la próxima acción.</summary>
    [ObservableProperty] private string? _savedMessage;

    [ObservableProperty] private bool _isBusy;

    public bool CanEdit { get; } = canEdit;
    public IDialogs Dialogs => services.Dialogs;

    /// <summary>Las variables vigentes, para resaltar y completar <c>{{variables}}</c> en el editor.</summary>
    public VariableLookup? Lookup { get; } = lookup;

    public ObservableCollection<LoginItemViewModel> Items { get; } = [];
    public bool HasSelection => Selected is not null;

    /// <summary>Si quedó algún login sin guardar, o si hubo cambios guardados (para avisarle a la ventana principal).</summary>
    public bool HasChanges => Items.Any(i => i.HasChanges);

    public bool Changed { get; private set; }

    public async Task LoadAsync()
    {
        var logins = await services.Store.GetLoginsAsync(workspaceId);
        var apps = await services.Store.GetAppsAsync(workspaceId);
        Items.Clear();
        foreach (var l in logins) Items.Add(new LoginItemViewModel(l, CanEdit, UsersOf(apps, l.Id)));
        Selected = Items.FirstOrDefault();
    }

    private static List<string> UsersOf(IReadOnlyList<AppDto> apps, Guid loginId) =>
        apps.Where(a => a.LoginId == loginId).Select(a => a.Name).ToList();

    private async Task GuardAsync(Func<Task> action)
    {
        IsBusy = true;
        Error = null;
        SavedMessage = null;
        try
        {
            await action();
        }
        catch (ApiException e)
        {
            Error = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>El login nuevo nace apagado, así se puede guardar antes de completarlo.</summary>
    [RelayCommand]
    private Task AddAsync() => GuardAsync(async () =>
    {
        var name = await Dialogs.PromptAsync(Texts.LoginsW_NewTitle, Texts.Main_Name);
        if (name is null) return;
        var dto = await services.Store.CreateLoginAsync(workspaceId, new CreateLoginRequest(name, Raf.Domain.Requests.AppLogin.Default));
        var item = new LoginItemViewModel(dto, CanEdit, []);
        Items.Add(item);
        Selected = item;
        Changed = true;
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task RenameAsync() => GuardAsync(async () =>
    {
        var item = Selected!;
        var name = await Dialogs.PromptAsync(Texts.LoginsW_RenameTitle, Texts.Main_Name, item.Name);
        if (name is null) return;
        item.Saved = await services.Store.UpdateLoginAsync(
            workspaceId, item.Saved.Id, new UpdateLoginRequest(name, item.Saved.Config, item.Saved.Version));
        item.Name = item.Saved.Name;
        Changed = true;
    });

    /// <summary>Copia lo guardado del login, no lo que esté sin guardar en el editor. La copia no la usa ninguna App.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DuplicateAsync() => GuardAsync(async () =>
    {
        var source = Selected!.Saved;
        var name = DuplicateName.Next(source.Name, Items.Select(i => i.Name));
        var dto = await new WorkspaceDuplicator(services.Store, workspaceId).DuplicateLoginAsync(source, name);
        var item = new LoginItemViewModel(dto, CanEdit, []);
        Items.Insert(Items.IndexOf(Selected) + 1, item);
        Selected = item;
        Changed = true;
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DeleteAsync() => GuardAsync(async () =>
    {
        var item = Selected!;
        if (item.UsedBy.Count > 0)
        {
            Error = string.Format(Texts.LoginsW_InUse, item.Name, string.Join(", ", item.UsedBy));
            return;
        }
        if (!await Dialogs.ConfirmAsync(Texts.Main_Delete, string.Format(Texts.LoginsW_DeleteMessage, item.Name), Texts.Main_Delete))
            return;
        await services.Store.DeleteLoginAsync(workspaceId, item.Saved.Id);
        Items.Remove(item);
        Selected = Items.FirstOrDefault();
        Changed = true;
    });

    /// <summary>Guarda todos los logins editados. Si a uno le falta algo, lo selecciona y muestra el motivo.</summary>
    [RelayCommand]
    private Task SaveAsync() => GuardAsync(async () =>
    {
        var changed = Items.Where(i => i.HasChanges).ToList();
        if (changed.Count == 0)
        {
            SavedMessage = Texts.Env_NoChanges;
            return;
        }

        foreach (var item in changed)
        {
            if (item.Editor.Problem() is { } problem)
            {
                Selected = item;
                Error = string.Format(Texts.LoginsW_Incomplete, item.Name, problem);
                return;
            }
        }

        foreach (var item in changed)
            item.Saved = await services.Store.UpdateLoginAsync(
                workspaceId, item.Saved.Id, new UpdateLoginRequest(item.Name, item.Editor.ToLogin(), item.Saved.Version));

        Changed = true;
        SavedMessage = changed.Count == 1 ? Texts.Env_SavedOne : string.Format(Texts.Env_SavedMany, changed.Count);
    });
}
