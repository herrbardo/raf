using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raf.Client.Core.Api;
using Raf.Client.Services;
using Raf.Contracts;

namespace Raf.Client.ViewModels;

public sealed partial class VariableRowViewModel : ObservableObject
{
    private readonly EnvironmentsViewModel _owner;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _key;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges), nameof(MaskChar), nameof(CanReveal))]
    private bool _isSecret;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaskChar))]
    private bool _reveal;

    /// <summary>Si la fila pasa el filtro del buscador. Las filas nuevas se ven siempre, aunque no coincidan.</summary>
    [ObservableProperty] private bool _isShown = true;

    /// <summary>Coincide por clave o por valor, sin distinguir mayúsculas. Las secretas solo por clave, para no revelar nada de su valor.</summary>
    public bool Matches(string term) =>
        (Key ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) ||
        !IsSecret && (Value ?? "").Contains(term, StringComparison.OrdinalIgnoreCase);

    /// <summary>La fila recién agregada pide el foco en la clave cuando la vista la muestra (una sola vez).</summary>
    internal bool FocusKeyOnShow { get; set; }

    public VariableRowViewModel(EnvironmentsViewModel owner, Guid? environmentId, VariableDto? saved)
    {
        _owner = owner;
        EnvironmentId = environmentId;
        Saved = saved;
        _key = saved?.Key;
        _value = saved?.Value;
        _isSecret = saved?.IsSecret ?? false;
    }

    public Guid? EnvironmentId { get; }
    public VariableDto? Saved { get; set; }
    public bool CanEdit => _owner.CanEdit;
    public bool IsReadOnly => !CanEdit;
    public bool IsHiddenFromMe => Saved is { IsSecret: true, Value: null };
    public string ValueHint => IsHiddenFromMe ? Texts.Env_HiddenForRole : "";
    public char MaskChar => IsSecret && !Reveal ? '•' : '\0';
    public bool CanReveal => IsSecret && !IsHiddenFromMe;

    public bool HasChanges =>
        Saved is null ? !string.IsNullOrWhiteSpace(Key)
        : Key != Saved.Key || (Value ?? "") != (Saved.Value ?? "") || IsSecret != Saved.IsSecret;

    // Cualquier edición vuelve viejo el aviso de "guardado".
    partial void OnKeyChanged(string? value) => _owner.OnEdited();
    partial void OnValueChanged(string? value) => _owner.OnEdited();
    partial void OnIsSecretChanged(bool value) => _owner.OnEdited();

    public void MarkSaved(VariableDto dto)
    {
        Saved = dto;
        OnPropertyChanged(nameof(HasChanges));
    }

    [RelayCommand]
    private void Remove() => _owner.RemoveRow(this);
}

/// <summary>Conjunto de variables: las globales (<see cref="Id"/> null) o las de un environment.</summary>
public sealed partial class VariableScopeViewModel : ObservableObject
{
    [ObservableProperty] private string _name;

    public VariableScopeViewModel(Guid? id, string name, EnvironmentDto? dto)
    {
        Id = id;
        _name = name;
        Dto = dto;
    }

    public Guid? Id { get; }
    public EnvironmentDto? Dto { get; set; }
    public bool IsEnvironment => Id is not null;
    public ObservableCollection<VariableRowViewModel> Rows { get; } = [];
}

public sealed partial class EnvironmentsViewModel(ClientServices services, Guid workspaceId, bool canEdit) : ObservableObject
{
    private readonly List<VariableRowViewModel> _removed = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameEnvironmentCommand), nameof(DeleteEnvironmentCommand))]
    private VariableScopeViewModel? _selectedScope;

    [ObservableProperty] private string? _error;

    /// <summary>Resultado del último guardado; se borra con la próxima edición.</summary>
    [ObservableProperty] private string? _savedMessage;
    [ObservableProperty] private bool _isBusy;

    public bool CanEdit { get; } = canEdit;
    public IDialogs Dialogs => services.Dialogs;
    public ObservableCollection<VariableScopeViewModel> Scopes { get; } = [];

    public bool HasChanges => _removed.Count > 0 || Scopes.Any(s => s.Rows.Any(r => r.HasChanges));

    /// <summary>Carga los alcances y elige el environment activo de la ventana principal, o las globales si no hay.</summary>
    public async Task LoadAsync(Guid? activeEnvironment = null)
    {
        var environments = await services.Store.GetEnvironmentsAsync(workspaceId);
        var variables = await services.Store.GetVariablesAsync(workspaceId);

        Scopes.Clear();
        _removed.Clear();
        var global = new VariableScopeViewModel(null, Texts.Env_Globals, null);
        Scopes.Add(global);
        foreach (var e in environments) Scopes.Add(new VariableScopeViewModel(e.Id, e.Name, e));

        foreach (var scope in Scopes)
            foreach (var v in variables.Where(v => v.EnvironmentId == scope.Id))
                scope.Rows.Add(new VariableRowViewModel(this, scope.Id, v));

        SelectedScope = Scopes.FirstOrDefault(s => s.Id is { } id && id == activeEnvironment) ?? global;
    }

    internal void OnEdited() => SavedMessage = null;

    /// <summary>Texto del buscador: filtra las filas del alcance elegido por clave o por valor.</summary>
    [ObservableProperty] private string? _search;

    /// <summary>Hay búsqueda y ninguna fila del alcance elegido coincide.</summary>
    [ObservableProperty] private bool _noMatches;

    partial void OnSearchChanged(string? value) => ApplyFilter();
    partial void OnSelectedScopeChanged(VariableScopeViewModel? value) => ApplyFilter();

    // Se filtra al cambiar la búsqueda o el alcance, no al editar: así una fila no desaparece mientras se escribe.
    private void ApplyFilter()
    {
        var term = Search?.Trim() ?? "";
        foreach (var row in SelectedScope?.Rows ?? [])
            row.IsShown = term.Length == 0 || row.Matches(term);
        UpdateNoMatches();
    }

    private void UpdateNoMatches() =>
        NoMatches = !string.IsNullOrWhiteSpace(Search) && SelectedScope?.Rows.Any(r => r.IsShown) != true;

    internal void RemoveRow(VariableRowViewModel row)
    {
        OnEdited();
        Scopes.First(s => s.Id == row.EnvironmentId).Rows.Remove(row);
        if (row.Saved is not null) _removed.Add(row);
        UpdateNoMatches();
    }

    [RelayCommand]
    private void AddVariable()
    {
        OnEdited();
        SelectedScope?.Rows.Add(new VariableRowViewModel(this, SelectedScope.Id, null) { FocusKeyOnShow = true });
        UpdateNoMatches();
    }

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

    [RelayCommand]
    private Task AddEnvironmentAsync() => GuardAsync(async () =>
    {
        var name = await Dialogs.PromptAsync(Texts.Env_NewTitle, Texts.Main_Name);
        if (name is null) return;
        var dto = await services.Store.CreateEnvironmentAsync(workspaceId, name);
        var scope = new VariableScopeViewModel(dto.Id, dto.Name, dto);
        Scopes.Add(scope);
        SelectedScope = scope;
    });

    private bool IsEnvironmentSelected => SelectedScope?.IsEnvironment == true;

    [RelayCommand(CanExecute = nameof(IsEnvironmentSelected))]
    private Task RenameEnvironmentAsync() => GuardAsync(async () =>
    {
        var scope = SelectedScope!;
        var name = await Dialogs.PromptAsync(Texts.Env_RenameTitle, Texts.Main_Name, scope.Name);
        if (name is null) return;
        scope.Dto = await services.Store.UpdateEnvironmentAsync(
            workspaceId, scope.Id!.Value, new UpdateEnvironmentRequest(name, scope.Dto!.Version));
        scope.Name = scope.Dto.Name;
    });

    [RelayCommand(CanExecute = nameof(IsEnvironmentSelected))]
    private Task DeleteEnvironmentAsync() => GuardAsync(async () =>
    {
        var scope = SelectedScope!;
        if (!await Dialogs.ConfirmAsync(Texts.Main_Delete, string.Format(Texts.Env_DeleteMessage, scope.Name), Texts.Main_Delete))
            return;
        await services.Store.DeleteEnvironmentAsync(workspaceId, scope.Id!.Value);
        _removed.RemoveAll(r => r.EnvironmentId == scope.Id);
        Scopes.Remove(scope);
        SelectedScope = Scopes[0];
    });

    /// <summary>Primero las bajas, para poder reutilizar una clave recién quitada.</summary>
    [RelayCommand]
    private Task SaveAsync() => GuardAsync(async () =>
    {
        var changed = Scopes.SelectMany(s => s.Rows).Where(r => r.HasChanges).ToList();
        var total = _removed.Count + changed.Count;
        if (total == 0)
        {
            SavedMessage = Texts.Env_NoChanges;
            return;
        }

        foreach (var row in _removed.ToList())
        {
            await services.Store.DeleteVariableAsync(workspaceId, row.Saved!.Id);
            _removed.Remove(row);
        }

        foreach (var row in changed)
        {
            var key = row.Key?.Trim() ?? "";
            row.MarkSaved(row.Saved is { } saved
                ? await services.Store.UpdateVariableAsync(
                    workspaceId, saved.Id, new UpdateVariableRequest(key, row.Value ?? "", row.IsSecret, saved.Version))
                : await services.Store.CreateVariableAsync(
                    workspaceId, new CreateVariableRequest(key, row.Value ?? "", row.EnvironmentId, row.IsSecret)));
        }

        SavedMessage = total == 1 ? Texts.Env_SavedOne : string.Format(Texts.Env_SavedMany, total);
    });
}
