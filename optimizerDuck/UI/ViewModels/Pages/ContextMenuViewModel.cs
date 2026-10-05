using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.Optimizations.Models.Tools;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.System.Primitives;
using optimizerDuck.Services.UI;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace optimizerDuck.UI.ViewModels.Pages;

public partial class ContextMenuViewModel(
    ContextMenuService contextMenuService,
    ToolRunPresenter presenter,
    ShellService shellService,
    ISnackbarService snackbarService,
    ILogger<ContextMenuViewModel> logger
) : ViewModel
{
    private List<ContextMenuEntry> _all = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _showWindowsEntries;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _needsExplorerRestart;

    public ObservableCollection<ContextMenuEntry> Entries { get; } = [];

    public bool HasResults => Entries.Count > 0;

    partial void OnShowWindowsEntriesChanged(bool value) => ApplyFilter();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    protected override Task InitializeOnceAsync() => Refresh();

    [RelayCommand]
    private async Task Refresh()
    {
        IsLoading = true;
        try
        {
            _all = await Task.Run(contextMenuService.GetEntries);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not list context menu entries");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ToggleEntry(ContextMenuEntry? entry)
    {
        if (entry is null)
            return;

        // The switch has already moved, so its new position is what the user asked for.
        var enable = entry.IsEnabled;
        var request = new OperationRequest(
            ToolSubjects.ContextMenu,
            Loc.Instance["ContextMenu.Header.Title"],
            logger,
            RevertPersistence.Disabled,
            (_, context) =>
                Task.Run(() =>
                {
                    ContextMenuService.SetEnabled(context, entry.Clsid, enable);
                    return context.Changes.ToApplyResult();
                })
        );

        var result = await presenter.RunAsync(request);
        if (!await presenter.ReportAsync(request, result, Loc.Instance["ContextMenu.Error.Title"]))
        {
            entry.IsEnabled = !enable;
            return;
        }

        NeedsExplorerRestart = true;
    }

    [RelayCommand]
    private async Task RestartExplorer()
    {
        try
        {
            await shellService.QueryCMDAsync(
                "taskkill /f /im explorer.exe && start explorer.exe",
                logger
            );
            NeedsExplorerRestart = false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not restart File Explorer");
            snackbarService.Show(
                Loc.Instance["ContextMenu.Error.Title"],
                ex.Message,
                ControlAppearance.Danger,
                new SymbolIcon { Symbol = SymbolRegular.ErrorCircle24, Filled = true },
                TimeSpan.FromSeconds(5)
            );
        }
    }

    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        Entries.Clear();
        foreach (
            var entry in _all.Where(e =>
                (ShowWindowsEntries || !e.IsSystem)
                && (
                    search.Length == 0
                    || e.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                    || e.DllPath.Contains(search, StringComparison.OrdinalIgnoreCase)
                )
            )
        )
            Entries.Add(entry);
        OnPropertyChanged(nameof(HasResults));
    }
}
