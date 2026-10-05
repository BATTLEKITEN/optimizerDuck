using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.System;
using optimizerDuck.Services.System.Primitives;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace optimizerDuck.UI.ViewModels.Pages;

/// <summary>A health check as the page shows it.</summary>
public sealed record HealthItem(HealthCheck Check)
{
    public SymbolRegular Icon =>
        Check.Status switch
        {
            HealthStatus.Good => SymbolRegular.CheckmarkCircle24,
            HealthStatus.Warning => SymbolRegular.Warning24,
            HealthStatus.Bad => SymbolRegular.ErrorCircle24,
            HealthStatus.Info => SymbolRegular.Info24,
            _ => SymbolRegular.QuestionCircle24,
        };

    public string StatusText => Loc.Instance[$"Health.Status.{Check.Status}"];
}

public partial class HealthViewModel(
    HealthCheckService healthCheckService,
    ShellService shellService,
    ISnackbarService snackbarService,
    ILogger<HealthViewModel> logger
) : ViewModel
{
    private IReadOnlyList<HealthCheck> _lastChecks = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(BatteryReportCommand))]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasBattery;

    public ObservableCollection<HealthItem> Items { get; } = [];

    private bool CanRun() => !IsLoading;

    protected override Task InitializeOnceAsync() => Refresh();

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Refresh()
    {
        IsLoading = true;
        try
        {
            _lastChecks = await healthCheckService.RunAsync();
            Items.Clear();
            foreach (var check in _lastChecks)
                Items.Add(new HealthItem(check));
            HasBattery = _lastChecks.Any(c => c.Title == Loc.Instance["Health.Check.Battery"]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The health report failed");
            ShowError(Loc.Instance["Health.Header.Title"], ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Export()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "HTML (*.html)|*.html",
            FileName = $"optimizerDuck-health-{DateTime.Now:yyyy-MM-dd}.html",
            AddExtension = true,
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var html = HealthCheckService.ToHtml(_lastChecks, DateTime.Now);
            await File.WriteAllTextAsync(dialog.FileName, html);
            ShellLauncher.Reveal(dialog.FileName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not export the health report");
            ShowError(Loc.Instance["Health.Export.Failed.Title"], ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task BatteryReport()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            $"optimizerDuck-battery-{DateTime.Now:yyyy-MM-dd}.html"
        );
        IsLoading = true;
        try
        {
            var result = await shellService.QueryCMDAsync(
                $"powercfg /batteryreport /output \"{path}\"",
                logger
            );
            if (result.ExitCode != 0 || !File.Exists(path))
            {
                ShowError(Loc.Instance["Health.Export.Failed.Title"], result.Stderr);
                return;
            }

            ShellLauncher.Reveal(path);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not create the battery report");
            ShowError(Loc.Instance["Health.Export.Failed.Title"], ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ShowError(string title, string message)
    {
        snackbarService.Show(
            title,
            message,
            ControlAppearance.Danger,
            new SymbolIcon { Symbol = SymbolRegular.ErrorCircle24, Filled = true },
            TimeSpan.FromSeconds(6)
        );
    }
}
