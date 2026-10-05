using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.Optimizations.Models.Tools;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.UI;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace optimizerDuck.UI.ViewModels.Pages;

/// <summary>A category of the installer, with its apps.</summary>
public sealed class AppCategory(string key, IReadOnlyList<InstallableApp> apps)
    : optimizerDuck.Common.Extensions.LocalizedObject
{
    public string Title => Loc.Instance[$"AppInstaller.Category.{key}"];

    public IReadOnlyList<InstallableApp> Apps { get; } = apps;
}

public partial class AppInstallerViewModel(
    AppInstallerService installerService,
    ToolRunPresenter presenter,
    ISnackbarService snackbarService,
    ILogger<AppInstallerViewModel> logger
) : ViewModel
{
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isWingetMissing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallSelectedCommand))]
    private int _selectedCount;

    public ObservableCollection<AppCategory> Categories { get; } = [];

    protected override async Task InitializeOnceAsync()
    {
        foreach (var group in AppInstallerService.Catalog().GroupBy(a => a.Category))
        {
            foreach (var app in group)
                app.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(InstallableApp.IsSelected))
                        SelectedCount = Categories.Sum(c => c.Apps.Count(a => a.IsSelected));
                };
            Categories.Add(new AppCategory(group.Key, group.ToList()));
        }

        IsLoading = true;
        try
        {
            IsWingetMissing = !await installerService.IsAvailableAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanInstall() => SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallSelected()
    {
        var apps = Categories.SelectMany(c => c.Apps).Where(a => a.IsSelected).ToList();
        var request = new OperationRequest(
            ToolSubjects.AppInstaller,
            Loc.Instance["AppInstaller.Header.Title"],
            logger,
            RevertPersistence.Disabled,
            async (progress, context) =>
            {
                for (var i = 0; i < apps.Count; i++)
                {
                    progress.Report(
                        new ProcessingProgress
                        {
                            Message = Loc.Instance["AppInstaller.Progress", apps[i].Name],
                            Value = i,
                            Total = apps.Count,
                        }
                    );
                    await installerService.InstallAsync(context, apps[i]);
                }

                return context.Changes.ToApplyResult();
            }
        );

        var result = await presenter.RunAsync(request);
        if (!await presenter.ReportAsync(request, result, Loc.Instance["AppInstaller.Error.Title"]))
            return;

        foreach (var app in apps)
            app.IsSelected = false;
        snackbarService.Show(
            Loc.Instance["AppInstaller.Success.Title"],
            Loc.Instance["AppInstaller.Success.Message", apps.Count],
            ControlAppearance.Success,
            new SymbolIcon { Symbol = SymbolRegular.CheckmarkCircle24, Filled = true },
            TimeSpan.FromSeconds(5)
        );
    }
}
