using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Models.Tools;
using optimizerDuck.Services.System.Primitives;

namespace optimizerDuck.Services.UI;

/// <summary>
///     Lists and switches Windows optional features through the DISM PowerShell module, the same
///     path "Turn Windows features on or off" takes.
/// </summary>
public partial class OptionalFeaturesService(
    ShellService shell,
    ILogger<OptionalFeaturesService> logger
)
{
    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex FeatureNameRegex();

    /// <summary>Whether a name can be passed to DISM; anything else is refused.</summary>
    public static bool IsValidName(string name) => FeatureNameRegex().IsMatch(name);

    /// <summary>Every optional feature on this edition of Windows, described ones first.</summary>
    public async Task<List<OptionalFeature>> GetFeaturesAsync(
        CancellationToken cancellationToken = default
    )
    {
        var result = await shell
            .QueryPowerShellAsync(
                "Get-WindowsOptionalFeature -Online | "
                    + "Select-Object FeatureName,@{n='State';e={$_.State.ToString()}} | "
                    + "ConvertTo-Json -Compress",
                logger,
                ct: cancellationToken
            )
            .ConfigureAwait(false);

        return Parse(result.Stdout);
    }

    internal static List<OptionalFeature> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        var token = JToken.Parse(json);
        IEnumerable<JToken> items = token is JArray array ? array.Children() : [token];
        return items
            .OfType<JObject>()
            .Select(o => (Name: (string?)o["FeatureName"], State: (string?)o["State"] ?? ""))
            .Where(f => f.Name is not null && IsValidName(f.Name))
            .Select(f => new OptionalFeature
            {
                FeatureName = f.Name!,
                State = f.State,
                IsEnabled = IsOn(f.State),
            })
            .OrderByDescending(f => f.IsDescribed)
            .ThenBy(f => f.FeatureName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Whether a DISM state counts as on.</summary>
    public static bool IsOn(string state) =>
        state.Equals("Enabled", StringComparison.OrdinalIgnoreCase)
        || state.Equals("EnablePending", StringComparison.OrdinalIgnoreCase);

    /// <summary>Turns a feature on or off without restarting; the caller says when to restart.</summary>
    public Task<OpResult> SetEnabledAsync(OpCall call, string featureName, bool enable)
    {
        if (!IsValidName(featureName))
            throw new ArgumentException(
                $"Invalid feature name: {featureName}",
                nameof(featureName)
            );

        var command = enable
            ? $"Enable-WindowsOptionalFeature -Online -FeatureName '{featureName}' -All -NoRestart | Out-Null"
            : $"Disable-WindowsOptionalFeature -Online -FeatureName '{featureName}' -NoRestart | Out-Null";
        return shell.PowerShellAsync(command, call);
    }
}
