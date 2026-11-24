using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raf.Domain.Requests;

namespace Raf.Client.ViewModels;

/// <summary>Un mensaje preparado, editable en la lista.</summary>
public sealed partial class PreparedMessageViewModel(PreparedMessagesViewModel owner, string name, string content) : ObservableObject
{
    [ObservableProperty] private string _name = name;
    [ObservableProperty] private string _content = content;

    public PreparedMessage ToMessage() => new(Name, Content);

    partial void OnNameChanged(string value) => owner.NotifyChanged();

    partial void OnContentChanged(string value) => owner.NotifyChanged();
}

/// <summary>
/// La lista de mensajes preparados de una conexión abierta (WebSocket o gRPC con streaming del cliente): se guardan
/// con el request y se mandan con un clic mientras la conexión está abierta. Quién manda y qué contenido tiene un
/// mensaje nuevo lo decide la pestaña.
/// </summary>
public sealed partial class PreparedMessagesViewModel(
    bool canEdit, Func<string, Task> send, Action changed, Func<string> newContent) : ObservableObject
{
    private bool _loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private PreparedMessageViewModel? _selected;

    /// <summary>Hay una conexión abierta que acepta mensajes: lo pone la pestaña.</summary>
    [ObservableProperty] private bool _canSend;

    public bool CanEdit { get; } = canEdit;
    public bool IsReadOnly => !CanEdit;
    public bool HasSelection => Selected is not null;

    public ObservableCollection<PreparedMessageViewModel> Items { get; } = [];

    public void Load(IReadOnlyList<PreparedMessage>? messages)
    {
        var index = Selected is { } current ? Items.IndexOf(current) : 0;
        _loading = true;
        Items.Clear();
        foreach (var m in messages ?? []) Items.Add(new PreparedMessageViewModel(this, m.Name, m.Content));
        _loading = false;
        Selected = Items.ElementAtOrDefault(Math.Max(0, index)) ?? Items.FirstOrDefault();
    }

    public IReadOnlyList<PreparedMessage> ToList() => Items.Select(m => m.ToMessage()).ToList();

    internal void NotifyChanged()
    {
        if (!_loading) changed();
    }

    [RelayCommand]
    private void Add()
    {
        var message = new PreparedMessageViewModel(this, string.Format(Texts.WsV_MessageName, Items.Count + 1), newContent());
        Items.Add(message);
        Selected = message;
        changed();
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is not { } message) return;
        var index = Items.IndexOf(message);
        Items.Remove(message);
        Selected = Items.ElementAtOrDefault(Math.Min(index, Items.Count - 1));
        changed();
    }

    [RelayCommand]
    private Task SendSelectedAsync() => Selected is { } message ? send(message.Content) : Task.CompletedTask;
}
