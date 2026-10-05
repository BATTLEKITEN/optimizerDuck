using System.Diagnostics;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.UI;
using optimizerDuck.Resources.Languages;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Optimization;
using optimizerDuck.Services.System;
using optimizerDuck.UI.Dialogs;
using optimizerDuck.UI.Pages;
using optimizerDuck.UI.ViewModels.Dialogs;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace optimizerDuck.UI.ViewModels.Pages;

public partial class DashboardViewModel : ViewModel
{
    private readonly IContentDialogService _contentDialogService;
    private readonly ILogger<DashboardViewModel> _logger;
    private readonly ISnackbarService _snackbarService;
    private readonly SystemInfoService _systemInfoService;
    private readonly UpdaterService _updaterService;
    private readonly DriftService _driftService;
    private readonly StreamService _streamService;
    private readonly INavigationService _navigationService;

    [ObservableProperty]
    private bool _isDriftInfoOpen;

    [ObservableProperty]
    private string _driftMessage = string.Empty;

    private readonly DispatcherTimer _updateTimer;
    private bool _ticking;

    [ObservableProperty]
    private ApplicationTheme _currentApplicationTheme = ApplicationTheme.Unknown;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasSystemInfoLoadFailed;
    private bool _isUpdateInfoOpen;

    [ObservableProperty]
    private string? _latestVersion;

    [ObservableProperty]
    private StorageInfo _runtimeStorage = StorageInfo.Unknown;

    [ObservableProperty]
    private MemoryInfo _runtimeMemory = MemoryInfo.Unknown;

    [ObservableProperty]
    private SystemInfo _systemInfo = SystemInfo.Unknown;

    [ObservableProperty]
    private string _windowsTitle = Loc.Instance["Common.Unknown"];

    [ObservableProperty]
    private string _installDateText = Loc.Instance["Common.Unknown"];

    [ObservableProperty]
    private string _lastBootText = Loc.Instance["Common.Unknown"];

    [ObservableProperty]
    private string _uptimeText = Loc.Instance["Common.Unknown"];

    [ObservableProperty]
    private string _powerPlanText = Loc.Instance["Common.Unknown"];
    private bool _updateNotified;

    public DashboardViewModel(
        SystemInfoService systemInfoService,
        ISnackbarService snackbarService,
        ILogger<DashboardViewModel> logger,
        UpdaterService updaterService,
        IContentDialogService contentDialogService,
        DriftService driftService,
        INavigationService navigationService,
        StreamService streamService
    )
    {
        _streamService = streamService;
        _driftService = driftService;
        _navigationService = navigationService;
        _driftService.Checked += (_, _) => _ = UiThread.InvokeAsync(ShowDrift);
        _systemInfoService = systemInfoService;
        _snackbarService = snackbarService;
        _logger = logger;
        _updaterService = updaterService;
        _contentDialogService = contentDialogService;

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _updateTimer.Tick += OnUpdateTick;

        CurrentApplicationTheme = ApplicationThemeManager.GetAppTheme();
    }

    public bool IsUpdateInfoOpen
    {
        get => _isUpdateInfoOpen;
        set
        {
            if (_isUpdateInfoOpen == value)
                return;

            _isUpdateInfoOpen = value;
            OnPropertyChanged();

            if (!_isUpdateInfoOpen && _updateNotified)
                OpenLatestRelease();
        }
    }

    private void ShowDrift()
    {
        var count = _driftService.LastResult.Count;
        DriftMessage = Loc.Instance["Dashboard.Drift.Message", count];
        IsDriftInfoOpen = count > 0;
    }

    /// <summary>
    ///     Downloads the newer release, verifies it against its published SHA-256 (and the
    ///     publisher's signature when this build is signed), then swaps it in and restarts.
    /// </summary>
    [RelayCommand]
    private async Task UpdateNow()
    {
        var confirm = await _contentDialogService.ShowSimpleDialogAsync(
            new SimpleContentDialogCreateOptions
            {
                Title = Loc.Instance["Update.Confirm.Title", LatestVersion ?? string.Empty],
                Content = Loc.Instance["Update.Confirm.Message"],
                PrimaryButtonText = Loc.Instance["Update.Button.Install"],
                CloseButtonText = Loc.Instance["Button.Cancel"],
            },
            CancellationToken.None
        );
        if (confirm != ContentDialogResult.Primary)
            return;

        var processing = new ProcessingViewModel();
        processing.ProgressReporter.Report(
            new ProcessingProgress
            {
                Message = Loc.Instance["Update.Downloading"],
                IsIndeterminate = true,
            }
        );
        var dialog = new ContentDialog
        {
            Title = Loc.Instance["Update.Confirm.Title", LatestVersion ?? string.Empty],
            Content = new ProcessingDialog { DataContext = processing },
            IsFooterVisible = false,
        };
        _ = _contentDialogService.ShowAsync(dialog, CancellationToken.None);

        string? verified;
        try
        {
            verified = await _updaterService.DownloadVerifiedUpdateAsync(_streamService);
        }
        finally
        {
            dialog.Hide();
        }

        if (verified is null)
        {
            _snackbarService.Show(
                Loc.Instance["Update.Failed.Title"],
                Loc.Instance["Update.Failed.Message"],
                ControlAppearance.Caution,
                new SymbolIcon { Symbol = SymbolRegular.Warning24, Filled = true },
                TimeSpan.FromSeconds(6)
            );
            OpenLatestRelease();
            return;
        }

        try
        {
            UpdaterService.InstallAndStart(verified);
            if (System.Windows.Application.Current is App app)
                app.ShutdownWithoutPrompt();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Installing the update failed");
            _snackbarService.Show(
                Loc.Instance["Update.Failed.Title"],
                ex.Message,
                ControlAppearance.Danger,
                new SymbolIcon { Symbol = SymbolRegular.ErrorCircle24, Filled = true },
                TimeSpan.FromSeconds(6)
            );
        }
    }

    [RelayCommand]
    private void OpenProfiles()
    {
        IsDriftInfoOpen = false;
        _navigationService.Navigate(typeof(ProfilesPage));
    }

    protected override async Task InitializeOnceAsync()
    {
        ShowDrift();
        await LoadSystemInfoAsync();
        _systemInfoService.LogSummary();
        var version = await _updaterService.CheckForUpdatesAsync();
        if (version is not null)
        {
            _updateNotified = true;
            IsUpdateInfoOpen = true;
            LatestVersion = version;
        }
        ApplicationThemeManager.Changed += OnThemeChanged;
    }

    /// <inheritdoc />
    public override async Task OnNavigatedToAsync()
    {
        await base.OnNavigatedToAsync();

        if (IsLoading)
            return;

        await LoadSystemInfoAsync();
        _updateTimer.Start();
    }

    /// <inheritdoc />
    public override Task OnNavigatedFromAsync()
    {
        _updateTimer.Stop();
        return base.OnNavigatedFromAsync();
    }

    protected override void OnLanguageChanged(CultureInfo newCulture)
    {
        ApplyDisplayTexts(SystemInfo);
    }

    #region Property Changed

    private void OnThemeChanged(ApplicationTheme currentApplicationTheme, Color systemAccent)
    {
        if (CurrentApplicationTheme != currentApplicationTheme)
            CurrentApplicationTheme = currentApplicationTheme;
    }

    #endregion Property Changed

    #region Commands

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadSystemInfoAsync();
    }

    [RelayCommand]
    private void SelectDisk(StorageVolume diskVolume)
    {
        if (diskVolume is null || string.IsNullOrWhiteSpace(diskVolume.DriveLetter))
            return;

        try
        {
            var drivePath = $"{diskVolume.DriveLetter}\\";

            ShellLauncher.OpenFolder(drivePath);
        }
        catch (Exception ex)
        {
            _snackbarService.Show(
                Loc.Instance["Snackbar.OpenFailed.Title"],
                Loc.Instance["Snackbar.OpenFailed.Message"],
                ControlAppearance.Danger,
                new SymbolIcon { Symbol = SymbolRegular.ErrorCircle24, Filled = true },
                TimeSpan.FromSeconds(5)
            );
            _logger.LogError(
                ex,
                "Failed to open disk volume {DriveLetter}",
                diskVolume.DriveLetter
            );
        }
    }

    [RelayCommand]
    private void OpenAction(string action)
    {
        try
        {
            switch (action)
            {
                case "Discord":
                    ShellLauncher.OpenUrl(Shared.DiscordInviteURL);
                    break;

                case "GitHub":
                    ShellLauncher.OpenUrl(Shared.GitHubRepoURL);
                    break;

                case "Support":
                case "Contribute":
                    ShellLauncher.OpenUrl(Shared.ContributeURL);
                    break;
            }
        }
        catch (Exception ex)
        {
            _snackbarService.Show(
                Loc.Instance["Snackbar.OpenFailed.Title"],
                Loc.Instance["Snackbar.OpenFailed.Message"],
                ControlAppearance.Danger,
                new SymbolIcon { Symbol = SymbolRegular.ErrorCircle24, Filled = true },
                TimeSpan.FromSeconds(5)
            );
            _logger.LogError(ex, "Failed to open {Action} link", action);
        }
    }

    [RelayCommand]
    private void Run(string action)
    {
        try
        {
            switch (action)
            {
                case "Settings":
                    Process.Start(
                        new ProcessStartInfo { FileName = "ms-settings:", UseShellExecute = true }
                    );
                    break;

                case "TaskManager":
                    Process.Start(
                        new ProcessStartInfo { FileName = "taskmgr", UseShellExecute = true }
                    );
                    break;

                case "ControlPanel":
                    Process.Start(
                        new ProcessStartInfo { FileName = "control", UseShellExecute = true }
                    );
                    break;
            }
        }
        catch (Exception ex)
        {
            _snackbarService.Show(
                Loc.Instance["Snackbar.OpenFailed.Title"],
                Loc.Instance["Snackbar.OpenFailed.Message"],
                ControlAppearance.Danger,
                new SymbolIcon { Symbol = SymbolRegular.ErrorCircle24, Filled = true },
                TimeSpan.FromSeconds(5)
            );
            _logger.LogError(ex, "Failed to run action {Action}", action);
        }
    }

    #endregion Commands

    #region Helpers

    private async Task LoadSystemInfoAsync()
    {
        IsLoading = true;
        HasSystemInfoLoadFailed = false;

        try
        {
            var snapshot = await _systemInfoService.RefreshAsync();
            SystemInfo = snapshot;
            RuntimeMemory = snapshot.Memory;
            RuntimeStorage = snapshot.Storage;
            ApplyDisplayTexts(snapshot);
        }
        catch (Exception ex)
        {
            HasSystemInfoLoadFailed = true;
            _logger.LogError(ex, "Failed to load system information");
            _snackbarService.Show(
                Loc.Instance["Snackbar.OpenFailed.Title"],
                Loc.Instance["Snackbar.OpenFailed.Message"],
                ControlAppearance.Danger,
                new SymbolIcon { Symbol = SymbolRegular.ErrorCircle24, Filled = true },
                TimeSpan.FromSeconds(5)
            );
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    ///     Runs on the two second tick, on the UI thread and through the cheap cached service
    ///     paths, so a slow scan can never overlap ticks.
    /// </summary>
    private void OnUpdateTick(object? sender, EventArgs e)
    {
        if (_ticking)
            return;
        _ticking = true;
        try
        {
            RuntimeMemory = _systemInfoService.GetLiveMemory();
            RuntimeStorage = _systemInfoService.GetLiveStorage();
            UptimeText =
                Loc.Instance["Dashboard.SystemInfo.Uptime.Label"]
                + ": "
                + FormatUptime(TimeSpan.FromMilliseconds((double)Environment.TickCount64));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update runtime info");
        }
        finally
        {
            _ticking = false;
        }
    }

    private void ApplyDisplayTexts(SystemInfo snapshot)
    {
        var unknown = Loc.Instance["Common.Unknown"];
        WindowsTitle = snapshot.Windows switch
        {
            { IsWindows11: true } => "Windows 11",
            { IsWindows10: true } => "Windows 10",
            { BuildNumber: { } build } => $"Windows (build {build})",
            _ => unknown,
        };
        InstallDateText = snapshot.Windows.InstallDate?.ToString("yyyy-MM-dd") ?? unknown;
        LastBootText = snapshot.Windows.LastBootTime?.ToString("yyyy-MM-dd HH:mm") ?? unknown;
        PowerPlanText = FormatPowerPlan(snapshot.Power);
        UptimeText =
            Loc.Instance["Dashboard.SystemInfo.Uptime.Label"]
            + ": "
            + FormatUptime(TimeSpan.FromMilliseconds((double)Environment.TickCount64));
    }

    private static string FormatPowerPlan(PowerInfo power)
    {
        // Live name from Windows; GUID fallback when the name is unreadable.
        var label = power.SchemeName ?? power.SchemeId?.ToString();
        if (string.IsNullOrWhiteSpace(label))
            return Loc.Instance["Common.Unknown"];
        return Loc.Instance["Dashboard.SystemInfo.PowerPlan", label];
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime < TimeSpan.Zero)
            return Loc.Instance["Common.Unknown"];
        return uptime.TotalDays >= 1 ? $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m"
            : uptime.TotalHours >= 1 ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m"
            : $"{uptime.Minutes}m {uptime.Seconds}s";
    }

    private void OpenLatestRelease()
    {
        try
        {
            ShellLauncher.OpenUrl(UpdaterService.LatestReleaseUrl);
        }
        catch (Exception ex)
        {
            _snackbarService.Show(
                Loc.Instance["Snackbar.OpenLinkFailed.Title"],
                Loc.Instance["Snackbar.OpenLinkFailed.Message"],
                ControlAppearance.Danger,
                new SymbolIcon { Symbol = SymbolRegular.ErrorCircle24, Filled = true },
                TimeSpan.FromSeconds(5)
            );
            _logger.LogError(ex, "Failed to open latest release page");
        }
    }

    #endregion Helpers
}
