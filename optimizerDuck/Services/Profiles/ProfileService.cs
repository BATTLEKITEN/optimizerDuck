using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Domain.Customize.Models;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Categories;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.Profiles;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Conditions;
using optimizerDuck.Services.Customize;
using optimizerDuck.Services.Optimization;
using optimizerDuck.Services.Revert;
using optimizerDuck.Services.System;

namespace optimizerDuck.Services.Profiles;

/// <summary>
///     Captures, saves, loads and applies <see cref="OptimizerProfile" />s, and builds the
///     built-in presets. Every optimization a profile applies goes through
///     <see cref="OptimizationService.ApplyAsync" />, so it is recorded and revertible like a
///     click on its card.
/// </summary>
public class ProfileService(
    OptimizationRegistry optimizationRegistry,
    CustomizeRegistry customizeRegistry,
    OptimizationService optimizationService,
    SystemInfoService systemInfoService,
    ILogger<ProfileService> logger
)
{
    /// <summary>Profiles are small; anything larger is not a profile.</summary>
    internal const long MaxFileBytes = 1024 * 1024;

    /// <summary>The keys of the built-in presets, in display order.</summary>
    public static readonly string[] PresetKeys = ["Recommended", "Gaming", "Privacy", "Laptop"];

    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        Formatting = Formatting.Indented,
        MissingMemberHandling = MissingMemberHandling.Ignore,
        DateParseHandling = DateParseHandling.None,
        MaxDepth = 16,
    };

    /// <summary>The key a customize setting is stored under in a profile.</summary>
    public static string CustomizeKey(ICustomizeSetting setting)
    {
        return setting is BaseCustomizeSetting b
            ? $"{b.OwnerKey}.{b.FeatureKey}"
            : setting.FeatureKey;
    }

    /// <summary>Builds a profile from what is applied on this machine right now.</summary>
    /// <param name="name">The name to store in the profile.</param>
    public async Task<OptimizerProfile> CaptureAsync(string name)
    {
        await optimizationRegistry.EnsurePreloadedAsync().ConfigureAwait(false);
        await customizeRegistry.EnsurePreloadedAsync().ConfigureAwait(false);

        var profile = NewProfile(name);
        foreach (var optimization in AllOptimizations())
            if (await RevertManager.IsAppliedAsync(optimization.Id).ConfigureAwait(false))
                profile.Optimizations.Add(optimization.Id);

        foreach (var setting in customizeRegistry.Categories.SelectMany(c => c.Features))
        {
            try
            {
                var value = await Task.Run(() => ReadCustomizeValue(setting)).ConfigureAwait(false);
                if (value is not null)
                    profile.Customize[CustomizeKey(setting)] = value;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Could not read {Setting} for the profile",
                    setting.FeatureKey
                );
            }
        }

        return profile;
    }

    /// <summary>The built-in presets, resolved against the optimizations of this build.</summary>
    public async Task<IReadOnlyList<ProfilePreset>> GetPresetsAsync()
    {
        await optimizationRegistry.EnsurePreloadedAsync().ConfigureAwait(false);

        var all = AllOptimizations().OfType<BaseOptimization>().ToList();
        return
        [
            Preset("Recommended", all.Where(o => o.Risk == OptimizationRisk.Safe)),
            Preset(
                "Gaming",
                all.Where(o =>
                    o.Risk != OptimizationRisk.Risky
                    && (
                        o.Tags.HasFlag(OptimizationTags.Performance)
                        || o.Tags.HasFlag(OptimizationTags.Latency)
                    )
                )
            ),
            Preset(
                "Privacy",
                all.Where(o =>
                    o.Risk != OptimizationRisk.Risky
                    && (
                        o.OwnerType == typeof(SecurityAndPrivacy)
                        || o.OwnerType == typeof(AI)
                        || o.Tags.HasFlag(OptimizationTags.Privacy)
                    )
                )
            ),
            Preset(
                "Laptop",
                // Battery life first: nothing that keeps the CPU, GPU or USB awake.
                all.Where(o =>
                    o.Risk == OptimizationRisk.Safe
                    && !o.Tags.HasFlag(OptimizationTags.Power)
                    && !o.Tags.HasFlag(OptimizationTags.Latency)
                    && o.OwnerType != typeof(Gpu)
                )
            ),
        ];

        static ProfilePreset Preset(string key, IEnumerable<BaseOptimization> items)
        {
            var profile = NewProfile(key);
            profile.Optimizations.AddRange(items.Select(o => o.Id));
            return new ProfilePreset(key, profile);
        }
    }

    /// <summary>Writes a profile as indented JSON.</summary>
    public static void Save(OptimizerProfile profile, string path)
    {
        ArgumentNullException.ThrowIfNull(profile);
        File.WriteAllText(path, Serialize(profile), new UTF8Encoding(false));
    }

    /// <summary>Serializes a profile as indented JSON.</summary>
    public static string Serialize(OptimizerProfile profile)
    {
        return JsonConvert.SerializeObject(profile, SerializerSettings);
    }

    /// <summary>Reads and validates a profile file.</summary>
    /// <exception cref="ProfileFormatException">The file is not a profile this build reads.</exception>
    public static OptimizerProfile Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new ProfileFormatException($"Profile not found: {path}");
        if (info.Length > MaxFileBytes)
            throw new ProfileFormatException("The file is too large to be a profile.");

        return Parse(File.ReadAllText(path));
    }

    /// <summary>Parses and validates profile JSON.</summary>
    /// <exception cref="ProfileFormatException">The text is not a profile this build reads.</exception>
    public static OptimizerProfile Parse(string json)
    {
        OptimizerProfile? profile;
        try
        {
            profile = JsonConvert.DeserializeObject<OptimizerProfile>(json, SerializerSettings);
        }
        catch (JsonException ex)
        {
            throw new ProfileFormatException("The file is not a valid profile.", ex);
        }

        if (profile is null)
            throw new ProfileFormatException("The file is not a valid profile.");
        if (profile.SchemaVersion != OptimizerProfile.CurrentSchemaVersion)
            throw new ProfileFormatException(
                $"Unsupported profile version {profile.SchemaVersion}."
            );

        profile.Optimizations ??= [];
        profile.Customize ??= [];
        profile.Optimizations = profile.Optimizations.Distinct().ToList();
        return profile;
    }

    /// <summary>Resolves the optimizations a profile names, in discovery order.</summary>
    public async Task<IReadOnlyList<IOptimization>> ResolveOptimizationsAsync(
        OptimizerProfile profile
    )
    {
        await optimizationRegistry.EnsurePreloadedAsync().ConfigureAwait(false);
        var wanted = profile.Optimizations.ToHashSet();
        return AllOptimizations().Where(o => wanted.Contains(o.Id)).ToList();
    }

    /// <summary>
    ///     Applies every optimization and customize value of the profile that is not already in
    ///     place. Items this build does not know, and items not supported on this machine, are
    ///     reported and left alone.
    /// </summary>
    /// <param name="profile">The profile to apply.</param>
    /// <param name="progress">Receives the name of each item as it starts.</param>
    /// <param name="cancellationToken">The token that stops the run between items.</param>
    public async Task<ProfileApplyReport> ApplyAsync(
        OptimizerProfile profile,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(profile);
        await optimizationRegistry.EnsurePreloadedAsync().ConfigureAwait(false);
        await customizeRegistry.EnsurePreloadedAsync().ConfigureAwait(false);
        var snapshot = await systemInfoService
            .EnsureSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);

        var report = new ProfileApplyReport();
        var byId = AllOptimizations().ToDictionary(o => o.Id);
        var applied = new List<IOptimization>();

        foreach (var id in profile.Optimizations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byId.TryGetValue(id, out var optimization))
            {
                report.Unknown.Add(id.ToString());
                continue;
            }

            var name = optimization.Name;
            if (await RevertManager.IsAppliedAsync(id).ConfigureAwait(false))
            {
                report.AlreadyApplied.Add(name);
                continue;
            }

            if (
                ConditionEvaluator.Evaluate(optimization.ConditionType, snapshot, logger).IsBlocking
            )
            {
                report.Unsupported.Add(name);
                continue;
            }

            progress?.Report(name);
            try
            {
                var result = await optimizationService
                    .ApplyAsync(optimization, SilentProgress.Instance, cancellationToken)
                    .ConfigureAwait(false);
                switch (result.Status)
                {
                    case OptimizationSuccessResult.Success:
                        report.Applied.Add(name);
                        applied.Add(optimization);
                        break;
                    case OptimizationSuccessResult.NothingToDo:
                        report.AlreadyApplied.Add(name);
                        break;
                    default:
                        report.Failed.Add($"{name}: {result.Message}");
                        applied.Add(optimization);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Profile apply failed for {Key}", optimization.OptimizationKey);
                report.Failed.Add($"{name}: {ex.Message}");
            }
        }

        if (applied.Count > 0)
            await OptimizationService.UpdateOptimizationStateAsync(applied).ConfigureAwait(false);

        var settings = customizeRegistry
            .Categories.SelectMany(c => c.Features)
            .GroupBy(CustomizeKey)
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var (key, token) in profile.Customize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!settings.TryGetValue(key, out var setting))
            {
                report.Unknown.Add(key);
                continue;
            }

            if (ConditionEvaluator.Evaluate(setting.ConditionType, snapshot, logger).IsBlocking)
            {
                report.Unsupported.Add(setting.Name);
                continue;
            }

            progress?.Report(setting.Name);
            await ApplyCustomizeAsync(setting, token, report).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Profile {Name}: {Applied} applied, {Already} already in place, {Unsupported} unsupported, {Failed} failed, {Unknown} unknown",
            profile.Name,
            report.Applied.Count,
            report.AlreadyApplied.Count,
            report.Unsupported.Count,
            report.Failed.Count,
            report.Unknown.Count
        );
        return report;
    }

    private async Task ApplyCustomizeAsync(
        ICustomizeSetting setting,
        JToken? token,
        ProfileApplyReport report
    )
    {
        try
        {
            object? value;
            if (setting.ControlType == CustomizeControlType.Toggle)
            {
                if (token is not { Type: JTokenType.Boolean })
                {
                    report.Failed.Add($"{setting.Name}: invalid value");
                    return;
                }

                value = token.Value<bool>();
                if (await setting.GetStateAsync().ConfigureAwait(false) == (bool)value)
                {
                    report.AlreadyApplied.Add(setting.Name);
                    return;
                }
            }
            else if (setting.ControlType == CustomizeControlType.Dropdown)
            {
                var raw = (token as JValue)?.Value;
                var option = setting.Options?.FirstOrDefault(o =>
                    BaseCustomizeSetting.ValuesEqual(o.Value, raw)
                );
                if (option is null)
                {
                    report.Failed.Add($"{setting.Name}: invalid value");
                    return;
                }

                if (BaseCustomizeSetting.ValuesEqual(setting.CurrentValue, option.Value))
                {
                    report.AlreadyApplied.Add(setting.Name);
                    return;
                }

                value = option.Value;
            }
            else
            {
                report.Unknown.Add(CustomizeKey(setting));
                return;
            }

            var result = await setting
                .ApplyAsync(value, new OpCall { Logger = logger })
                .ConfigureAwait(false);
            if (result.Ok)
                report.Applied.Add(setting.Name);
            else
                report.Failed.Add($"{setting.Name}: {result.Error}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Profile apply failed for {Setting}", setting.FeatureKey);
            report.Failed.Add($"{setting.Name}: {ex.Message}");
        }
    }

    private static async Task<JToken?> ReadCustomizeValue(ICustomizeSetting setting)
    {
        switch (setting.ControlType)
        {
            case CustomizeControlType.Toggle:
                return new JValue(await setting.GetStateAsync().ConfigureAwait(false));
            case CustomizeControlType.Dropdown:
                var current = setting.CurrentValue;
                // Only a declared option is portable; "Custom" and "Not set" are not.
                var option = setting.Options?.FirstOrDefault(o =>
                    ReferenceEquals(o.Value, current)
                    || BaseCustomizeSetting.ValuesEqual(o.Value, current)
                );
                return option?.Value is IConvertible ? new JValue(option.Value) : null;
            default:
                return null;
        }
    }

    private IEnumerable<IOptimization> AllOptimizations()
    {
        return optimizationRegistry.OptimizationCategories.SelectMany(c => c.Optimizations);
    }

    private static OptimizerProfile NewProfile(string name)
    {
        return new OptimizerProfile
        {
            Name = name,
            AppVersion = Shared.FileVersion,
            CreatedAt = DateTime.Now,
        };
    }

    private sealed class SilentProgress : IProgress<ProcessingProgress>
    {
        public static readonly SilentProgress Instance = new();

        public void Report(ProcessingProgress value) { }
    }
}
