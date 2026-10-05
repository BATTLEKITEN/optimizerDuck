using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.Optimizations.Models.Tools;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.UI;

namespace optimizerDuck.UI.ViewModels.Pages;

public partial class OptionalFeaturesViewModel(
    OptionalFeaturesService featuresService,
    ToolRunPresenter presenter,
    ILogger<OptionalFeaturesViewModel> logger
) : ViewModel
{
    private List<OptionalFeature> _all = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _needsRestart;

    public ObservableCollection<OptionalFeature> Features { get; } = [];

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    protected override Task InitializeOnceAsync() => Refresh();

    [RelayCommand]
    private async Task Refresh()
    {
        IsLoading = true;
        try
        {
            _all = await featuresService.GetFeaturesAsync();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not list optional features");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ToggleFeature(OptionalFeature? feature)
    {
        if (feature is null || IsBusy)
            return;

        var enable = feature.IsEnabled;
        var request = new OperationRequest(
            ToolSubjects.OptionalFeatures,
            Loc.Instance["OptionalFeatures.Header.Title"],
            logger,
            RevertPersistence.Disabled,
            async (_, context) =>
            {
                await featuresService.SetEnabledAsync(context, feature.FeatureName, enable);
                return context.Changes.ToApplyResult();
            }
        );

        IsBusy = true;
        try
        {
            var result = await presenter.RunAsync(request);
            if (
                !await presenter.ReportAsync(
                    request,
                    result,
                    Loc.Instance["OptionalFeatures.Error.Title"]
                )
            )
            {
                feature.IsEnabled = !enable;
                return;
            }

            feature.State = enable ? "EnablePending" : "DisablePending";
            NeedsRestart = true;
            if (Application.Current is App app)
                app.HasPendingChanges = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        Features.Clear();
        foreach (
            var feature in _all.Where(f =>
                search.Length == 0
                || f.FeatureName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || f.Description.Contains(search, StringComparison.CurrentCultureIgnoreCase)
            )
        )
            Features.Add(feature);
    }
}
