using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Configuration;
using optimizerDuck.Domain.Profiles;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Optimization;
using optimizerDuck.Services.Profiles;
using optimizerDuck.Services.System;

namespace optimizerDuck.Services.Cli;

/// <summary>
///     Runs one command line command without the window. Exit codes: 0 when everything worked
///     (or, for <c>--check-drift</c>, nothing drifted), 1 when an item failed or drift was found,
///     2 for a usage error or a file that cannot be read or written.
/// </summary>
public class CliRunner(
    ProfileService profileService,
    DriftService driftService,
    OptimizationService optimizationService,
    SystemRestoreService systemRestoreService,
    ConfigManager configManager,
    IOptionsMonitor<AppSettings> appOptions,
    ILogger<CliRunner> logger
)
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    /// <summary>Runs the command and returns the process exit code.</summary>
    public async Task<int> RunAsync(CommandLineOptions options, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        logger.LogInformation("Command line run: {Command}", options.Command);

        if (options.Error is not null)
        {
            output.WriteLine(Loc.Instance[options.Error, options.ErrorArg ?? string.Empty]);
            output.WriteLine();
            output.WriteLine(Loc.Instance["Cli.Usage"]);
            return ExitUsage;
        }

        if (options.Command == CliCommand.Help)
        {
            output.WriteLine(Loc.Instance["Cli.Usage"]);
            return ExitOk;
        }

        var changesSystem =
            options.Command
            is CliCommand.ApplyProfile
                or CliCommand.ApplyPreset
                or CliCommand.ReapplyDrift;
        if (changesSystem)
        {
            if (!appOptions.CurrentValue.App.LegalAccepted)
            {
                if (!options.AcceptTerms)
                {
                    output.WriteLine(Loc.Instance["Cli.Error.Terms"]);
                    return ExitUsage;
                }

                await configManager.SetAsync(x => x.App.LegalAccepted, true);
            }

            if (options.RestorePoint && !await CreateRestorePointAsync(output))
                return ExitFailed;
        }

        var report = new CliReport { Command = options.Command.ToString() };
        int exitCode;
        try
        {
            exitCode = options.Command switch
            {
                CliCommand.ApplyProfile => await ApplyAsync(
                    ProfileService.Load(options.Argument!),
                    output,
                    report
                ),
                CliCommand.ApplyPreset => await ApplyPresetAsync(options.Argument!, output, report),
                CliCommand.ExportProfile => await ExportAsync(options.Argument!, output),
                CliCommand.CheckDrift => await CheckDriftAsync(output, report),
                CliCommand.ReapplyDrift => await ReapplyDriftAsync(output, report),
                _ => ExitUsage,
            };
        }
        catch (Exception ex) when (ex is ProfileFormatException or IOException)
        {
            logger.LogError(ex, "Command line run failed");
            output.WriteLine(Loc.Instance["Profiles.Import.Failed.Message"]);
            output.WriteLine(ex.Message);
            exitCode = ExitUsage;
        }

        report.ExitCode = exitCode;
        if (options.ReportPath is not null)
        {
            try
            {
                File.WriteAllText(
                    options.ReportPath,
                    JsonConvert.SerializeObject(report, Formatting.Indented)
                );
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not write the report to {Path}", options.ReportPath);
                output.WriteLine(ex.Message);
                return ExitUsage;
            }
        }

        return exitCode;
    }

    private async Task<int> ApplyPresetAsync(string key, TextWriter output, CliReport report)
    {
        var preset = (await profileService.GetPresetsAsync()).FirstOrDefault(p =>
            string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)
        );
        if (preset is null)
        {
            output.WriteLine(
                Loc.Instance[
                    "Cli.Error.UnknownPreset",
                    key,
                    string.Join(", ", ProfileService.PresetKeys)
                ]
            );
            return ExitUsage;
        }

        return await ApplyAsync(preset.Profile, output, report);
    }

    private async Task<int> ApplyAsync(
        OptimizerProfile profile,
        TextWriter output,
        CliReport report
    )
    {
        var result = await profileService.ApplyAsync(
            profile,
            new SyncProgress(name =>
                output.WriteLine(Loc.Instance["Profiles.Apply.Progress", name])
            )
        );

        report.Applied = result.Applied;
        report.AlreadyApplied = result.AlreadyApplied;
        report.Unsupported = result.Unsupported;
        report.Unknown = result.Unknown;
        report.Failed = result.Failed;

        output.WriteLine();
        output.WriteLine(
            Loc.Instance[
                "Profiles.Report.Summary",
                result.Applied.Count,
                result.AlreadyApplied.Count,
                result.Unsupported.Count,
                result.Unknown.Count,
                result.Failed.Count
            ]
        );
        if (result.HasFailures)
        {
            output.WriteLine(Loc.Instance["Profiles.Report.Failures"]);
            foreach (var failure in result.Failed)
                output.WriteLine("  " + failure);
        }

        return result.HasFailures ? ExitFailed : ExitOk;
    }

    private async Task<int> ExportAsync(string path, TextWriter output)
    {
        var profile = await profileService.CaptureAsync(Path.GetFileNameWithoutExtension(path));
        ProfileService.Save(profile, path);
        output.WriteLine(
            Loc.Instance[
                "Profiles.Export.Success.Message",
                profile.Optimizations.Count,
                profile.Customize.Count
            ]
        );
        return ExitOk;
    }

    private async Task<int> CheckDriftAsync(TextWriter output, CliReport report)
    {
        var drifted = await driftService.CheckAsync();
        report.Drifted = drifted.Select(d => d.Optimization.Name).ToList();
        WriteDrift(output, report.Drifted);
        return drifted.Count == 0 ? ExitOk : ExitFailed;
    }

    private async Task<int> ReapplyDriftAsync(TextWriter output, CliReport report)
    {
        var drifted = await driftService.CheckAsync();
        WriteDrift(output, drifted.Select(d => d.Optimization.Name).ToList());

        foreach (var entry in drifted)
        {
            var name = entry.Optimization.Name;
            output.WriteLine(Loc.Instance["Profiles.Apply.Progress", name]);
            var result = await optimizationService.ApplyAsync(
                entry.Optimization,
                new SyncProgress<ProcessingProgress>(_ => { })
            );
            if (
                result.Status
                is OptimizationSuccessResult.Success
                    or OptimizationSuccessResult.NothingToDo
            )
                report.Applied.Add(name);
            else
                report.Failed.Add($"{name}: {result.Message}");
        }

        report.Drifted = (await driftService.CheckAsync())
            .Select(d => d.Optimization.Name)
            .ToList();
        return report.Failed.Count == 0 ? ExitOk : ExitFailed;
    }

    private static void WriteDrift(TextWriter output, IReadOnlyList<string> names)
    {
        output.WriteLine(
            names.Count == 0
                ? Loc.Instance["Drift.Status.None"]
                : Loc.Instance["Drift.Status.Found", names.Count]
        );
        foreach (var name in names)
            output.WriteLine("  " + name);
    }

    private async Task<bool> CreateRestorePointAsync(TextWriter output)
    {
        output.WriteLine(Loc.Instance["RestorePoint.Progress.Creating"]);
        var result = await Task.Run(() =>
        {
            var first = systemRestoreService.CreateRestorePoint(Shared.RestorePointName);
            if (
                first.Succeeded
                || !SystemRestoreService.IsProtectionDisabledStatus(first.NativeStatus)
            )
                return first;

            var drive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            return systemRestoreService.EnableProtection(drive).Succeeded
                ? systemRestoreService.CreateRestorePoint(Shared.RestorePointName)
                : first;
        });

        if (result.Succeeded)
            return true;

        logger.LogError(
            "Restore point creation failed: native status 0x{Status:X8}",
            result.NativeStatus
        );
        output.WriteLine(Loc.Instance["RestorePoint.Snackbar.Error.Message"]);
        return false;
    }

    /// <summary>Reports on the calling thread, so console lines keep their order.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    /// <summary>The machine-readable outcome written by <c>--report</c>.</summary>
    private sealed class CliReport
    {
        public string Command { get; set; } = string.Empty;
        public int ExitCode { get; set; }
        public string AppVersion { get; set; } = Shared.FileVersion;
        public List<string> Applied { get; set; } = [];
        public List<string> AlreadyApplied { get; set; } = [];
        public List<string> Unsupported { get; set; } = [];
        public List<string> Unknown { get; set; } = [];
        public List<string> Failed { get; set; } = [];
        public List<string> Drifted { get; set; } = [];
    }
}
