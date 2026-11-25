using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raf.Client.Core.Api;
using Raf.Client.Services;
using Raf.Contracts;

namespace Raf.Client.ViewModels;

/// <summary>Una App para la columna y el filtro de la ventana de environments; <see cref="Id"/> null = todas (Plan 11).</summary>
public sealed record AppChoice(Guid? Id, string Name)
{
    public override string ToString() => Name;
}

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

    /// <summary>La App de la variable, o "Todas" (Plan 11, V1). Cambiarla la mueve de alcance (V7).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    private AppChoice _app;

    /// <summary>Si la fila pasa el filtro del buscador. Las filas nuevas se ven siempre, aunque no coincidan.</summary>
    [ObservableProperty] private bool _isShown = true;

    /// <summary>Coincide por clave o por valor, sin distinguir mayúsculas. Las secretas solo por clave, para no revelar nada de su valor.</summary>
    public bool Matches(string term) =>
        (Key ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) ||
        !IsSecret && (Value ?? "").Contains(term, StringComparison.OrdinalIgnoreCase);

    /// <summary>La fila recién agregada pide el foco en la clave cuando la vista la muestra (una sola vez).</summary>
    internal bool FocusKeyOnShow { get; set; }

    public VariableRowViewModel(EnvironmentsViewModel owner, Guid? environmentId, VariableDto? saved, AppChoice? app = null)
    {
        _owner = owner;
        EnvironmentId = environmentId;
        Saved = saved;
        _key = saved?.Key;
        _value = saved?.Value;
        _isSecret = saved?.IsSecret ?? false;
        _app = owner.AppChoiceFor(saved?.AppId ?? app?.Id);
    }

    public Guid? EnvironmentId { get; }
    public IReadOnlyList<AppChoice> AppChoices => _owner.AppChoices;
    public VariableDto? Saved { get; set; }
    public bool CanEdit => _owner.CanEdit;
    public bool IsReadOnly => !CanEdit;
    public bool IsHiddenFromMe => Saved is { IsSecret: true, Value: null };
    public string ValueHint => IsHiddenFromMe ? Texts.Env_HiddenForRole : "";
    public char MaskChar => IsSecret && !Reveal ? '•' : '\0';
    public bool CanReveal => IsSecret && !IsHiddenFromMe;

    public bool HasChanges =>
        Saved is null ? !string.IsNullOrWhiteSpace(Key)
        : Key != Saved.Key || (Value ?? "") != (Saved.Value ?? "") || IsSecret != Saved.IsSecret || App.Id != Saved.AppId;

    // Cualquier edición vuelve viejo el aviso de "guardado".
    partial void OnKeyChanged(string? value) => _owner.OnEdited();
    partial void OnValueChanged(string? value) => _owner.OnEdited();
    partial void OnIsSecretChanged(bool value) => _owner.OnEdited();
    partial void OnAppChanged(AppChoice value) => _owner.OnEdited();

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

public sealed partial class EnvironmentsViewModel(
    ClientServices services, Guid workspaceId, bool canEdit, IReadOnlyList<AppChoice>? apps = null) : ObservableObject
{
    private readonly List<VariableRowViewModel> _removed = [];

    private static AppChoice AllApps => new(null, Texts.EnvW_AllApps);

    /// <summary>Lo que se elige en la columna App: "Todas" y las Apps del workspace, en orden.</summary>
    public IReadOnlyList<AppChoice> AppChoices { get; } = [AllApps, .. apps ?? []];

    /// <summary>El filtro por App (Plan 11, V6): "Todas las Apps" (Id null) o una App.</summary>
    public IReadOnlyList<AppChoice> AppFilters { get; } = [new(null, Texts.EnvW_FilterAllApps), .. apps ?? []];

    [ObservableProperty] private AppChoice? _appFilter;

    /// <summary>Hay Apps para elegir: si no, la columna y el filtro no aportan nada.</summary>
    public bool HasApps => AppChoices.Count > 1;

    /// <summary>La opción de una App por id; "Todas" si es null o si la App ya no está.</summary>
    internal AppChoice AppChoiceFor(Guid? id) => AppChoices.FirstOrDefault(a => a.Id == id) ?? AppChoices[0];

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

    /// <summary>
    /// Carga los alcances y elige el environment activo de la ventana principal, o las globales si no hay. El filtro
    /// arranca en la App de la pestaña activa, o en "Todas las Apps".
    /// </summary>
    public async Task LoadAsync(Guid? activeEnvironment = null, Guid? activeApp = null)
    {
        AppFilter = AppFilters.FirstOrDefault(a => a.Id == activeApp) ?? AppFilters[0];
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
    partial void OnAppFilterChanged(AppChoice? value) => ApplyFilter();

    private bool IsFiltering => !string.IsNullOrWhiteSpace(Search) || AppFilter?.Id is not null;

    // Se filtra al cambiar la búsqueda, el alcance o la App, no al editar: así una fila no desaparece mientras se escribe.
    // Con una App elegida se ven sus variables y las de todas las Apps.
    private void ApplyFilter()
    {
        var term = Search?.Trim() ?? "";
        var app = AppFilter?.Id;
        foreach (var row in SelectedScope?.Rows ?? [])
            row.IsShown = (term.Length == 0 || row.Matches(term)) && (app is null || row.App.Id is null || row.App.Id == app);
        UpdateNoMatches();
    }

    private void UpdateNoMatches() =>
        NoMatches = IsFiltering && SelectedScope?.Rows.Any(r => r.IsShown) != true;

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
        // Nace con la App del filtro, para que se vea en la grilla filtrada.
        SelectedScope?.Rows.Add(new VariableRowViewModel(this, SelectedScope.Id, null, AppFilter) { FocusKeyOnShow = true });
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
        if (Repeated() is { Count: > 0 } repeated)
        {
            Error = string.Format(Texts.Env_Repeated, string.Join(", ", repeated));
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
                    workspaceId, saved.Id, new UpdateVariableRequest(key, row.Value ?? "", row.IsSecret, saved.Version, row.App.Id ?? Guid.Empty))
                : await services.Store.CreateVariableAsync(
                    workspaceId, new CreateVariableRequest(key, row.Value ?? "", row.EnvironmentId, row.IsSecret, AppId: row.App.Id)));
        }

        SavedMessage = total == 1 ? Texts.Env_SavedOne : string.Format(Texts.Env_SavedMany, total);
    });

    /// <summary>
    /// Las claves repetidas en la misma pestaña y para la misma App (Plan 11, V2), como "baseUrl (Orders)"; así no se
    /// guarda nada a medias.
    /// </summary>
    private List<string> Repeated() =>
        Scopes.SelectMany(s => s.Rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Key))
                .GroupBy(r => (Key: r.Key!.Trim(), r.App.Id))
                .Where(g => g.Count() > 1)
                .Select(g => g.Key.Id is null ? $"{g.Key.Key} ({s.Name})" : $"{g.Key.Key} ({s.Name} · {g.First().App.Name})"))
            .Distinct()
            .ToList();
}
