using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raf.Client.Core.Api;
using Raf.Client.Core.Sync;
using Raf.Client.Services;
using Raf.Contracts;

namespace Raf.Client.ViewModels;

public sealed record ConflictRow(SyncConflict Conflict)
{
    public string Title => $"{KindText(Conflict.Kind)}: {Conflict.Name}";
    public string Subtitle => Conflict.WorkspaceName;

    private static string KindText(ChangeKind kind) => kind switch
    {
        ChangeKind.Workspace => "Workspace",
        ChangeKind.Environment => "Environment",
        ChangeKind.Variable => "Variable",
        ChangeKind.App => "App",
        ChangeKind.Folder => "Carpeta",
        ChangeKind.Request => "Request",
        ChangeKind.Sample => "Sample",
        _ => kind.ToString(),
    };
}

/// <summary>Muestra la versión local y la remota de cada conflicto; se elige una de las dos o se edita.</summary>
public sealed partial class ConflictsViewModel(SyncSession sync, IDialogs dialogs) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(Message), nameof(LocalText), nameof(RemoteText), nameof(HasRemote),
        nameof(CanUseLocal), nameof(CanUseRemote), nameof(CanEdit), nameof(CanRecreate), nameof(CanDiscard))]
    private ConflictRow? _selected;

    [ObservableProperty] private string? _editedText;
    [ObservableProperty] private string? _error;

    public ObservableCollection<ConflictRow> Conflicts { get; } = [];

    public bool HasSelection => Selected is not null;
    public string? Message => Selected?.Conflict.Message;
    public string? LocalText => Selected?.Conflict.LocalJson;
    public string? RemoteText => Selected?.Conflict.RemoteJson;
    public bool HasRemote => RemoteText is not null;
    public bool CanUseLocal => Selected?.Conflict.CanUseLocal == true;
    public bool CanUseRemote => Selected?.Conflict.Reason == ConflictReason.Version;
    public bool CanEdit => Selected?.Conflict.CanEdit == true;
    public bool CanRecreate => Selected?.Conflict.CanRecreate == true;

    /// <summary>En un conflicto de versión, descartar es lo mismo que usar la del servidor.</summary>
    public bool CanDiscard => HasSelection && !CanUseRemote;

    partial void OnSelectedChanged(ConflictRow? value)
    {
        EditedText = value?.Conflict.LocalJson;
        Error = null;
    }

    public async Task LoadAsync()
    {
        var conflicts = await sync.Store.GetConflictsAsync();
        Conflicts.Clear();
        foreach (var c in conflicts) Conflicts.Add(new ConflictRow(c));
        Selected = Conflicts.FirstOrDefault();
    }

    private async Task ResolveAsync(Func<long, Task> action)
    {
        if (Selected is null) return;
        try
        {
            await action(Selected.Conflict.Id);
            await LoadAsync();
        }
        catch (ApiException e)
        {
            Error = e.Message;
        }
    }

    [RelayCommand]
    private Task UseLocalAsync() => ResolveAsync(id => sync.Store.UseLocalAsync(id));

    [RelayCommand]
    private Task UseRemoteAsync() => ResolveAsync(id => sync.Store.DiscardAsync(id));

    [RelayCommand]
    private Task SaveEditAsync() => ResolveAsync(id => sync.Store.EditAsync(id, EditedText ?? ""));

    [RelayCommand]
    private Task RecreateAsync() => ResolveAsync(id => sync.Store.RecreateAsync(id));

    [RelayCommand]
    private async Task DiscardAsync()
    {
        if (await dialogs.ConfirmAsync("Descartar", "Tu cambio se pierde. ¿Continuar?", "Descartar"))
            await ResolveAsync(id => sync.Store.DiscardAsync(id));
    }
}
