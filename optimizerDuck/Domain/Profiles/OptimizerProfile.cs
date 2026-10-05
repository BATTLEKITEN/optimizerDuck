using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace optimizerDuck.Domain.Profiles;

/// <summary>
///     A portable selection of optimizations and customize values, saved as JSON so the same
///     setup can be applied on another machine or from the command line.
/// </summary>
public sealed class OptimizerProfile
{
    /// <summary>The only schema version this build reads and writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The file extension profiles are saved with.</summary>
    public const string FileExtension = ".duckprofile";

    [JsonProperty(Required = Required.Always)]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>The name shown when the profile is imported.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The app version that wrote the profile, for diagnostics only.</summary>
    public string? AppVersion { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>The ids of the optimizations to apply.</summary>
    public List<Guid> Optimizations { get; set; } = [];

    /// <summary>
    ///     Customize values keyed by <c>Category.Setting</c>: a boolean for a toggle, the
    ///     option value for a dropdown.
    /// </summary>
    public Dictionary<string, JToken?> Customize { get; set; } = [];
}

/// <summary>A built-in profile, built from the discovered optimizations at runtime.</summary>
/// <param name="Key">The stable key, also the localization suffix.</param>
/// <param name="Profile">The selection the preset applies.</param>
public sealed record ProfilePreset(string Key, OptimizerProfile Profile);

/// <summary>What applying a profile did, item by item.</summary>
public sealed class ProfileApplyReport
{
    public List<string> Applied { get; } = [];

    public List<string> AlreadyApplied { get; } = [];

    public List<string> Unsupported { get; } = [];

    public List<string> Failed { get; } = [];

    /// <summary>Ids or keys the profile names that this build does not know.</summary>
    public List<string> Unknown { get; } = [];

    public bool HasFailures => Failed.Count > 0;
}

/// <summary>A profile file that cannot be read.</summary>
public sealed class ProfileFormatException(string message, Exception? inner = null)
    : Exception(message, inner);
