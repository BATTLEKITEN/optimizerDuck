using optimizerDuck.UI.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace optimizerDuck.UI.Pages;

public partial class ContextMenuPage : INavigableView<ContextMenuViewModel>
{
    public ContextMenuPage(ContextMenuViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }

    public ContextMenuViewModel ViewModel { get; }
}
