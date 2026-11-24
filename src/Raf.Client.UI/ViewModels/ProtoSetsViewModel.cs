using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raf.Client.Core.Api;
using Raf.Client.Core.Grpc;
using Raf.Client.Services;
using Raf.Contracts;
using Raf.Domain.Requests;

namespace Raf.Client.ViewModels;

/// <summary>Un archivo del conjunto, editable: la ruta (la que usan los imports) y el contenido.</summary>
public sealed partial class ProtoFileViewModel(ProtoSetItemViewModel owner, string path, string content) : ObservableObject
{
    [ObservableProperty] private string _path = path;
    [ObservableProperty] private string _content = content;

    public ProtoFile ToFile() => new(Path.Trim().Replace('\\', '/'), Content);

    partial void OnPathChanged(string value) => owner.OnFilesChanged();

    partial void OnContentChanged(string value) => owner.OnFilesChanged();
}

/// <summary>Un conjunto de la lista, con sus archivos; los cambios quedan acá hasta guardar.</summary>
public sealed partial class ProtoSetItemViewModel : ObservableObject
{
    [ObservableProperty] private ProtoSetDto _saved;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile))]
    private ProtoFileViewModel? _selectedFile;

    /// <summary>Cómo compiló la última vez; null si cambió desde entonces o todavía no se compiló.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckText), nameof(CheckOk), nameof(CheckFailed))]
    private GrpcProtoCheck? _check;

    public ProtoSetItemViewModel(ProtoSetDto saved, Action changed)
    {
        _saved = saved;
        Changed = changed;
        _name = saved.Name;
        foreach (var f in saved.Files) Files.Add(new ProtoFileViewModel(this, f.Path, f.Content));
        SelectedFile = Files.FirstOrDefault();
    }

    private Action Changed { get; }

    [ObservableProperty] private string _name;

    public ObservableCollection<ProtoFileViewModel> Files { get; } = [];

    public bool HasFile => SelectedFile is not null;

    public IReadOnlyList<ProtoFile> ToFiles() => Files.Select(f => f.ToFile()).ToList();

    public bool HasChanges => !ToFiles().SequenceEqual(Saved.Files);

    public bool CheckOk => Check is { IsOk: true };
    public bool CheckFailed => Check is { IsOk: false };

    public string? CheckText => Check switch
    {
        null => null,
        { IsOk: true, Methods.Count: var n } => string.Format(Texts.ProtoW_Compiled, n),
        { Errors: var errors } => string.Join(Environment.NewLine, errors),
    };

    internal void OnFilesChanged()
    {
        Check = null;
        Changed();
    }

    /// <summary>Agrega archivos; uno con una ruta que ya está reemplaza al anterior.</summary>
    public void AddFiles(IEnumerable<PickedTextFile> picked)
    {
        foreach (var file in picked)
        {
            var path = file.Path.Replace('\\', '/');
            if (Files.FirstOrDefault(f => f.Path == path) is { } existing) existing.Content = file.Content;
            else Files.Add(new ProtoFileViewModel(this, path, file.Content));
        }
        SelectedFile ??= Files.FirstOrDefault();
        OnFilesChanged();
    }

    public void RemoveSelected()
    {
        if (SelectedFile is not { } file) return;
        var index = Files.IndexOf(file);
        Files.Remove(file);
        SelectedFile = Files.ElementAtOrDefault(Math.Min(index, Files.Count - 1));
        OnFilesChanged();
    }
}

/// <summary>
/// Los conjuntos de archivos .proto del workspace (Plan 10, etapa 2): los requests gRPC los eligen cuando el servidor
/// no tiene server reflection. Se cargan archivos o carpetas enteras (conservan las rutas de los imports), se editan
/// y se compilan para ver los errores antes de usarlos.
/// </summary>
public sealed partial class ProtoSetsViewModel(ClientServices services, Guid workspaceId, bool canEdit) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand), nameof(DeleteCommand), nameof(AddFilesCommand), nameof(AddFolderCommand),
        nameof(RemoveFileCommand), nameof(CompileCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ProtoSetItemViewModel? _selected;

    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _savedMessage;
    [ObservableProperty] private bool _isBusy;

    public bool CanEdit { get; } = canEdit;
    public bool IsReadOnly => !CanEdit;
    public IDialogs Dialogs => services.Dialogs;

    /// <summary>Compilar necesita el motor gRPC, que solo tiene la app de escritorio.</summary>
    public bool CanCompile => services.Runner.Grpc is not null;

    public ObservableCollection<ProtoSetItemViewModel> Items { get; } = [];
    public bool HasSelection => Selected is not null;
    public bool HasChanges => Items.Any(i => i.HasChanges);
    public bool Changed { get; private set; }

    public async Task LoadAsync()
    {
        Items.Clear();
        foreach (var set in await services.Store.GetProtoSetsAsync(workspaceId)) Items.Add(new ProtoSetItemViewModel(set, OnEdited));
        Selected = Items.FirstOrDefault();
    }

    private void OnEdited() => SavedMessage = null;

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
    private Task AddAsync() => GuardAsync(async () =>
    {
        var name = await Dialogs.PromptAsync(Texts.ProtoW_NewTitle, Texts.Main_Name);
        if (name is null) return;
        var dto = await services.Store.CreateProtoSetAsync(workspaceId, new CreateProtoSetRequest(name, []));
        var item = new ProtoSetItemViewModel(dto, OnEdited);
        Items.Add(item);
        Selected = item;
        Changed = true;
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task RenameAsync() => GuardAsync(async () =>
    {
        var item = Selected!;
        var name = await Dialogs.PromptAsync(Texts.ProtoW_RenameTitle, Texts.Main_Name, item.Name);
        if (name is null) return;
        item.Saved = await services.Store.UpdateProtoSetAsync(
            workspaceId, item.Saved.Id, new UpdateProtoSetRequest(name, item.Saved.Files, item.Saved.Version));
        item.Name = item.Saved.Name;
        Changed = true;
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DeleteAsync() => GuardAsync(async () =>
    {
        var item = Selected!;
        if (!await Dialogs.ConfirmAsync(Texts.Main_Delete, string.Format(Texts.ProtoW_DeleteMessage, item.Name), Texts.Main_Delete)) return;
        await services.Store.DeleteProtoSetAsync(workspaceId, item.Saved.Id);
        Items.Remove(item);
        Selected = Items.FirstOrDefault();
        Changed = true;
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task AddFilesAsync() => GuardAsync(async () =>
    {
        var files = await Dialogs.OpenTextFilesAsync(Texts.ProtoW_AddFiles, ".proto");
        if (files.Count == 0) return;
        Selected!.AddFiles(files);
        Compile();
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task AddFolderAsync() => GuardAsync(async () =>
    {
        var files = await Dialogs.OpenTextFolderAsync(Texts.ProtoW_AddFolder, ".proto");
        if (files.Count == 0)
        {
            Error = Texts.ProtoW_NoProtoInFolder;
            return;
        }
        Selected!.AddFiles(files);
        Compile();
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveFile() => Selected?.RemoveSelected();

    /// <summary>Compila el conjunto elegido tal como está en la ventana, sin guardarlo.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Compile()
    {
        if (Selected is not { } item || services.Runner.Grpc is not { } engine) return;
        item.Check = engine.CheckProtos(item.ToFiles());
    }

    /// <summary>Guarda todos los conjuntos editados. Se guardan aunque no compilen: el error se ve al usarlos.</summary>
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
            if (ProtoFile.Problem(item.ToFiles()) is { } problem)
            {
                Selected = item;
                Error = string.Format(Texts.ProtoW_Invalid, item.Name, problem);
                return;
            }
        }

        foreach (var item in changed)
            item.Saved = await services.Store.UpdateProtoSetAsync(
                workspaceId, item.Saved.Id, new UpdateProtoSetRequest(item.Name, item.ToFiles(), item.Saved.Version));

        Changed = true;
        SavedMessage = changed.Count == 1 ? Texts.Env_SavedOne : string.Format(Texts.Env_SavedMany, changed.Count);
    });

    partial void OnSelectedChanged(ProtoSetItemViewModel? value)
    {
        if (value is { Check: null }) Compile();
    }
}
