using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using Raf.Client.ViewModels;

namespace Raf.Client.Views;

public partial class ConversationView : UserControl
{
    private ConversationViewModel? _vm;

    public ConversationView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.Entries.CollectionChanged -= OnEntriesChanged;
            _vm = DataContext as ConversationViewModel;
            if (_vm is not null) _vm.Entries.CollectionChanged += OnEntriesChanged;
        };
    }

    // Puramente visual: mientras se sigue al último mensaje, la lista baja sola.
    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm is not { FollowLatest: true } || e.Action != NotifyCollectionChangedAction.Add) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm?.Entries.LastOrDefault() is { } last) EntriesList.ScrollIntoView(last);
        }, DispatcherPriority.Background);
    }
}
