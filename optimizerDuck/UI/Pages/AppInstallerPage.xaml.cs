using optimizerDuck.UI.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace optimizerDuck.UI.Pages;

public partial class AppInstallerPage : INavigableView<AppInstallerViewModel>
{
    public AppInstallerPage(AppInstallerViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }

    public AppInstallerViewModel ViewModel { get; }
}
