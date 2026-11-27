using Avalonia.Controls;
using Raf.Client.Controls;

namespace Raf.Client.Views;

public partial class BodyEditorView : UserControl
{
    public BodyEditorView()
    {
        InitializeComponent();
        CommandKey.AddMetaAliases(KeyBindings);
    }
}
