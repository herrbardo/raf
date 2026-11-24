using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raf.Client.Core.Execution;

namespace Raf.Client.ViewModels;

public enum ConversationEntryKind { Sent, Received, Info, Error }

/// <summary>Una línea de la conversación: un mensaje enviado o recibido, o un aviso de la conexión.</summary>
public sealed class ConversationEntry(ConversationEntryKind kind, DateTimeOffset at, string text, long? size = null, string? detail = null)
{
    public ConversationEntryKind Kind { get; } = kind;
    public string Text { get; } = text;

    /// <summary>Lo que se muestra al elegirla: el mensaje completo, o el detalle de un aviso (headers, por ejemplo).</summary>
    public string Detail { get; } = detail ?? text;

    public string TimeText { get; } = at.ToLocalTime().ToString("HH:mm:ss.fff");

    /// <summary>Flecha de la dirección; el color lo pone la vista según el tipo.</summary>
    public string Glyph => Kind switch
    {
        ConversationEntryKind.Sent => "↑",
        ConversationEntryKind.Received => "↓",
        ConversationEntryKind.Error => "!",
        _ => "•",
    };

    /// <summary>Una línea, para la lista.</summary>
    public string Preview { get; } = text.Length > 300 ? text[..300].ReplaceLineEndings(" ") + "…" : text.ReplaceLineEndings(" ");

    public string? SizeText { get; } = size is { } s ? RunFormat.Size(s) : null;

    public bool IsSent => Kind == ConversationEntryKind.Sent;
    public bool IsReceived => Kind == ConversationEntryKind.Received;
    public bool IsInfo => Kind == ConversationEntryKind.Info;
    public bool IsError => Kind == ConversationEntryKind.Error;
}

/// <summary>
/// La conversación de una conexión abierta (WebSocket; más adelante, el streaming de gRPC): la lista de lo enviado y
/// recibido, el detalle del mensaje elegido formateado y el texto para mandar. No sabe del protocolo: la pestaña
/// agrega las entradas y hace el envío.
/// </summary>
public sealed partial class ConversationViewModel : ObservableObject
{
    /// <summary>Tope de entradas: las más viejas se descartan para que una conexión larga no agote la memoria.</summary>
    public const int MaxEntries = 5000;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ConversationEntry? _selectedEntry;

    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private string? _detailSyntax;

    /// <summary>El mensaje que se está escribiendo para mandar.</summary>
    [ObservableProperty] private string _composer = "";

    /// <summary>Si la lista sigue al último mensaje; se apaga al elegir uno anterior.</summary>
    [ObservableProperty] private bool _followLatest = true;

    public ObservableCollection<ConversationEntry> Entries { get; } = [];

    public bool HasSelection => SelectedEntry is not null;

    public int SentCount { get; private set; }
    public int ReceivedCount { get; private set; }

    public void Add(ConversationEntry entry)
    {
        if (Entries.Count >= MaxEntries) Entries.RemoveAt(0);
        Entries.Add(entry);
        if (entry.IsSent) SentCount++;
        if (entry.IsReceived) ReceivedCount++;
        if (FollowLatest || SelectedEntry is null) SelectEntry(entry);
    }

    private void SelectEntry(ConversationEntry entry)
    {
        var follow = FollowLatest;
        SelectedEntry = entry;
        FollowLatest = follow;
    }

    partial void OnSelectedEntryChanged(ConversationEntry? value)
    {
        FollowLatest = value is null || ReferenceEquals(value, Entries.LastOrDefault());
        if (value is null)
        {
            DetailText = "";
            DetailSyntax = null;
            return;
        }

        // Un mensaje en JSON o XML se muestra formateado, como el body de una respuesta.
        var formatted = value.Kind is ConversationEntryKind.Sent or ConversationEntryKind.Received
            ? ResponseFormatter.Format(value.Detail, null)
            : null;
        DetailText = formatted?.Text ?? value.Detail;
        DetailSyntax = formatted?.Format switch
        {
            ResponseFormat.Json => "Json",
            ResponseFormat.Xml => "XML",
            _ => null,
        };
    }

    [RelayCommand]
    private void Clear()
    {
        Entries.Clear();
        SentCount = ReceivedCount = 0;
        SelectedEntry = null;
        FollowLatest = true;
    }
}
