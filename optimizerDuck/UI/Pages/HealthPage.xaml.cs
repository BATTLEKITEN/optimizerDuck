using optimizerDuck.UI.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace optimizerDuck.UI.Pages;

public partial class HealthPage : INavigableView<HealthViewModel>
{
    public HealthPage(HealthViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }

    public HealthViewModel ViewModel { get; }
}
