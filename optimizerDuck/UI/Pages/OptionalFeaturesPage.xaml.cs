using optimizerDuck.UI.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace optimizerDuck.UI.Pages;

public partial class OptionalFeaturesPage : INavigableView<OptionalFeaturesViewModel>
{
    public OptionalFeaturesPage(OptionalFeaturesViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }

    public OptionalFeaturesViewModel ViewModel { get; }
}
