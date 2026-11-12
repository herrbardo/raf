using Avalonia.Controls;
using Raf.Client.ViewModels;

namespace Raf.Client.Views;

public partial class ResponseView : UserControl
{
    public ResponseView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ResponseViewModel vm)
                vm.CopyToClipboard = text => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
        };
    }
}
