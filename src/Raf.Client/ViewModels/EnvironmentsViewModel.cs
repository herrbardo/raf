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
    public string ValueHint => IsHiddenFromMe ? "Oculto para tu rol" : "";
    public char MaskChar => IsSecret && !Reveal ? '•' : '\0';
    public bool CanReveal => IsSecret && !IsHiddenFromMe;

    public bool HasChanges =>
        Saved is null ? !string.IsNullOrWhiteSpace(Key)
        : Key != Saved.Key || (Value ?? "") != (Saved.Value ?? "") || IsSecret != Saved.IsSecret;

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
    [ObservableProperty] private bool _isBusy;

    public bool CanEdit { get; } = canEdit;
    public IDialogs Dialogs => services.Dialogs;
    public ObservableCollection<VariableScopeViewModel> Scopes { get; } = [];

    public bool HasChanges => _removed.Count > 0 || Scopes.Any(s => s.Rows.Any(r => r.HasChanges));

    public async Task LoadAsync()
    {
        var environments = await services.Store.GetEnvironmentsAsync(workspaceId);
        var variables = await services.Store.GetVariablesAsync(workspaceId);

        Scopes.Clear();
        _removed.Clear();
        var global = new VariableScopeViewModel(null, "Globales", null);
        Scopes.Add(global);
        foreach (var e in environments) Scopes.Add(new VariableScopeViewModel(e.Id, e.Name, e));

        foreach (var scope in Scopes)
            foreach (var v in variables.Where(v => v.EnvironmentId == scope.Id))
                scope.Rows.Add(new VariableRowViewModel(this, scope.Id, v));

        SelectedScope = global;
    }

    internal void RemoveRow(VariableRowViewModel row)
    {
        Scopes.First(s => s.Id == row.EnvironmentId).Rows.Remove(row);
        if (row.Saved is not null) _removed.Add(row);
    }

    [RelayCommand]
    private void AddVariable() => SelectedScope?.Rows.Add(new VariableRowViewModel(this, SelectedScope.Id, null));

    private async Task GuardAsync(Func<Task> action)
    {
        IsBusy = true;
        Error = null;
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
        var name = await Dialogs.PromptAsync("Nuevo environment", "Nombre");
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
        var name = await Dialogs.PromptAsync("Renombrar environment", "Nombre", scope.Name);
        if (name is null) return;
        scope.Dto = await services.Store.UpdateEnvironmentAsync(
            workspaceId, scope.Id!.Value, new UpdateEnvironmentRequest(name, scope.Dto!.Version));
        scope.Name = scope.Dto.Name;
    });

    [RelayCommand(CanExecute = nameof(IsEnvironmentSelected))]
    private Task DeleteEnvironmentAsync() => GuardAsync(async () =>
    {
        var scope = SelectedScope!;
        if (!await Dialogs.ConfirmAsync("Eliminar", $"¿Eliminar el environment \"{scope.Name}\" y sus variables?", "Eliminar"))
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
        foreach (var row in _removed.ToList())
        {
            await services.Store.DeleteVariableAsync(workspaceId, row.Saved!.Id);
            _removed.Remove(row);
        }

        foreach (var row in Scopes.SelectMany(s => s.Rows).Where(r => r.HasChanges).ToList())
        {
            var key = row.Key?.Trim() ?? "";
            row.MarkSaved(row.Saved is { } saved
                ? await services.Store.UpdateVariableAsync(
                    workspaceId, saved.Id, new UpdateVariableRequest(key, row.Value ?? "", row.IsSecret, saved.Version))
                : await services.Store.CreateVariableAsync(
                    workspaceId, new CreateVariableRequest(key, row.Value ?? "", row.EnvironmentId, row.IsSecret)));
        }
    });
}
