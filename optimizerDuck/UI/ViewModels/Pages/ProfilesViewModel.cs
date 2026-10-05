using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using optimizerDuck.Common.Extensions;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Profiles;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Optimization;
using optimizerDuck.Services.Profiles;
using optimizerDuck.UI.Dialogs;
using optimizerDuck.UI.ViewModels.Dialogs;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;
using TextBlock = System.Windows.Controls.TextBlock;

namespace optimizerDuck.UI.ViewModels.Pages;

/// <summary>A built-in preset as the profiles page shows it.</summary>
public sealed class PresetItem(ProfilePreset preset) : LocalizedObject
{
    public ProfilePreset Preset { get; } = preset;

    public string Title => Loc.Instance[$"Profiles.Preset.{Preset.Key}.Title"];

    public string Description => Loc.Instance[$"Profiles.Preset.{Preset.Key}.Description"];

    public string CountText =>
        Loc.Instance["Profiles.Preset.Count", Preset.Profile.Optimizations.Count];
}

public partial class ProfilesViewModel(
    ProfileService profileService,
    DriftService driftService,
    OptimizationService optimizationService,
    IContentDialogService contentDialogService,
    ISnackbarService snackbarService,
    ILogger<ProfilesViewModel> logger
) : ViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyPresetCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckDriftCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReapplyDriftCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _driftStatus = string.Empty;

    public ObservableCollection<PresetItem> Presets { get; } = [];

    public ObservableCollection<string> DriftedItems { get; } = [];

    public bool HasDrift => DriftedItems.Count > 0;

    private bool CanRun() => !IsBusy;

    protected override async Task InitializeOnceAsync()
    {
        driftService.Checked += (_, _) => _ = UiThread.InvokeAsync(ShowDrift);
        foreach (var preset in await profileService.GetPresetsAsync())
            Presets.Add(new PresetItem(preset));
        ShowDrift();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task PreviewPreset(PresetItem? item)
    {
        if (item is null)
            return;

        var names = (await profileService.ResolveOptimizationsAsync(item.Preset.Profile))
            .Select(o => "• " + o.Name)
            .ToList();
        await contentDialogService.ShowSimpleDialogAsync(
            new SimpleContentDialogCreateOptions
            {
                Title = Loc.Instance["Profiles.Preview.Title", item.Title],
                Content = ScrollableText(
                    names.Count == 0
                        ? Loc.Instance["Profiles.Preview.Empty"]
                        : string.Join(Environment.NewLine, names)
                ),
                CloseButtonText = Loc.Instance["Button.Ok"],
            },
            CancellationToken.None
        );
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ApplyPreset(PresetItem? item)
    {
        if (item is not null)
            await ApplyProfileAsync(item.Preset.Profile, item.Title);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Export()
    {
        var dialog = new SaveFileDialog
        {
            Filter = FileFilter(),
            DefaultExt = OptimizerProfile.FileExtension,
            FileName = $"optimizerDuck-{Environment.MachineName}{OptimizerProfile.FileExtension}",
            AddExtension = true,
        };
        if (dialog.ShowDialog() != true)
            return;

        IsBusy = true;
        try
        {
            var profile = await profileService.CaptureAsync(
                Path.GetFileNameWithoutExtension(dialog.FileName)
            );
            await Task.Run(() => ProfileService.Save(profile, dialog.FileName));
            snackbarService.Show(
                Loc.Instance["Profiles.Export.Success.Title"],
                Loc.Instance[
                    "Profiles.Export.Success.Message",
                    profile.Optimizations.Count,
                    profile.Customize.Count
                ],
                ControlAppearance.Success,
                new SymbolIcon { Symbol = SymbolRegular.CheckmarkCircle24, Filled = true },
                TimeSpan.FromSeconds(5)
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to export the profile to {Path}", dialog.FileName);
            ShowError(Loc.Instance["Profiles.Export.Failed.Title"], ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Import()
    {
        var dialog = new OpenFileDialog { Filter = FileFilter(), CheckFileExists = true };
        if (dialog.ShowDialog() != true)
            return;

        OptimizerProfile profile;
        try
        {
            profile = await Task.Run(() => ProfileService.Load(dialog.FileName));
        }
        catch (Exception ex) when (ex is ProfileFormatException or IOException)
        {
            logger.LogWarning(ex, "Could not read the profile {Path}", dialog.FileName);
            ShowError(
                Loc.Instance["Profiles.Import.Failed.Title"],
                Loc.Instance["Profiles.Import.Failed.Message"]
            );
            return;
        }

        var name = string.IsNullOrWhiteSpace(profile.Name)
            ? Path.GetFileNameWithoutExtension(dialog.FileName)
            : profile.Name;
        await ApplyProfileAsync(profile, name);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CheckDrift()
    {
        IsBusy = true;
        DriftStatus = Loc.Instance["Drift.Status.Checking"];
        try
        {
            await driftService.CheckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The drift check failed");
            ShowError(Loc.Instance["Drift.Header"], ex.Message);
        }
        finally
        {
            IsBusy = false;
            ShowDrift();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ReapplyDrift()
    {
        var entries = driftService.LastResult;
        if (entries.Count == 0 || !await EnsureRestorePointAsync())
            return;

        IsBusy = true;
        var processing = new ProcessingViewModel();
        var dialog = new ContentDialog
        {
            Title = Loc.Instance["Drift.Reapply.Title"],
            Content = new ProcessingDialog { DataContext = processing },
            IsFooterVisible = false,
        };
        _ = contentDialogService.ShowAsync(dialog, CancellationToken.None);
        try
        {
            foreach (var entry in entries)
                await optimizationService.ApplyAsync(
                    entry.Optimization,
                    processing.ProgressReporter
                );
            await OptimizationService.UpdateOptimizationStateAsync(
                entries.Select(e => e.Optimization)
            );
            if (Application.Current is App app)
                app.HasPendingChanges = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reapplying drifted optimizations failed");
            ShowError(Loc.Instance["Drift.Reapply.Title"], ex.Message);
        }
        finally
        {
            dialog.Hide();
            IsBusy = false;
        }

        await CheckDrift();
    }

    private async Task ApplyProfileAsync(OptimizerProfile profile, string displayName)
    {
        var optimizations = await profileService.ResolveOptimizationsAsync(profile);
        var confirm = await contentDialogService.ShowSimpleDialogAsync(
            new SimpleContentDialogCreateOptions
            {
                Title = Loc.Instance["Profiles.Apply.Confirm.Title", displayName],
                Content = Loc.Instance[
                    "Profiles.Apply.Confirm.Message",
                    optimizations.Count,
                    profile.Customize.Count
                ],
                PrimaryButtonText = Loc.Instance["Profiles.Button.Apply"],
                CloseButtonText = Loc.Instance["Button.Cancel"],
            },
            CancellationToken.None
        );
        if (confirm != ContentDialogResult.Primary || !await EnsureRestorePointAsync())
            return;

        IsBusy = true;
        var processing = new ProcessingViewModel();
        var dialog = new ContentDialog
        {
            Title = Loc.Instance["Profiles.Apply.Title", displayName],
            Content = new ProcessingDialog { DataContext = processing },
            IsFooterVisible = false,
        };
        _ = contentDialogService.ShowAsync(dialog, CancellationToken.None);

        ProfileApplyReport report;
        try
        {
            report = await profileService.ApplyAsync(
                profile,
                new Progress<string>(name =>
                    processing.ProgressReporter.Report(
                        new ProcessingProgress
                        {
                            Message = Loc.Instance["Profiles.Apply.Progress", name],
                            IsIndeterminate = true,
                        }
                    )
                )
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Applying the profile {Name} failed", displayName);
            ShowError(Loc.Instance["Profiles.Apply.Title", displayName], ex.Message);
            return;
        }
        finally
        {
            dialog.Hide();
            IsBusy = false;
        }

        if (report.Applied.Count > 0 && Application.Current is App app)
            app.HasPendingChanges = true;

        var summary = Loc.Instance[
            "Profiles.Report.Summary",
            report.Applied.Count,
            report.AlreadyApplied.Count,
            report.Unsupported.Count,
            report.Unknown.Count,
            report.Failed.Count
        ];
        if (report.HasFailures)
            summary +=
                Environment.NewLine
                + Environment.NewLine
                + Loc.Instance["Profiles.Report.Failures"]
                + Environment.NewLine
                + string.Join(Environment.NewLine, report.Failed.Select(f => "• " + f));

        await contentDialogService.ShowSimpleDialogAsync(
            new SimpleContentDialogCreateOptions
            {
                Title = Loc.Instance["Profiles.Report.Title"],
                Content = ScrollableText(summary),
                CloseButtonText = Loc.Instance["Button.Ok"],
            },
            CancellationToken.None
        );
    }

    /// <summary>
    ///     Creates a restore point once per session before the first change, like a single
    ///     apply does. A failed restore point lets the user decide whether to go on.
    /// </summary>
    private async Task<bool> EnsureRestorePointAsync()
    {
        if (optimizationService.WasRequestedRestorePoint)
            return true;

        var result = await optimizationService.CreateRestorePointAsync();
        optimizationService.WasRequestedRestorePoint = true;
        if (result == RestorePointResult.Success)
            return true;

        var choice = await contentDialogService.ShowSimpleDialogAsync(
            new SimpleContentDialogCreateOptions
            {
                Title = Loc.Instance["RestorePoint.Snackbar.Error.Title"],
                Content = Loc.Instance["RestorePoint.Snackbar.Error.Message"],
                PrimaryButtonText = Loc.Instance["Button.Skip"],
                CloseButtonText = Loc.Instance["Button.Cancel"],
            },
            CancellationToken.None
        );
        if (choice == ContentDialogResult.Primary)
            return true;

        // The user wants a restore point, so the next attempt asks again.
        optimizationService.WasRequestedRestorePoint = false;
        return false;
    }

    private void ShowDrift()
    {
        DriftedItems.Clear();
        foreach (var entry in driftService.LastResult)
            DriftedItems.Add(entry.Optimization.Name);
        OnPropertyChanged(nameof(HasDrift));

        if (IsBusy)
            return;
        DriftStatus =
            DriftedItems.Count > 0
                ? Loc.Instance["Drift.Status.Found", DriftedItems.Count]
                : Loc.Instance["Drift.Status.None"];
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

    private static string FileFilter()
    {
        return $"{Loc.Instance["Profiles.FileFilter"]} (*{OptimizerProfile.FileExtension})|*{OptimizerProfile.FileExtension}|JSON (*.json)|*.json";
    }

    private static ScrollViewer ScrollableText(string text)
    {
        return new ScrollViewer
        {
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
        };
    }
}
