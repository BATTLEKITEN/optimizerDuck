using CommunityToolkit.Mvvm.ComponentModel;
using optimizerDuck.Common.Extensions;
using optimizerDuck.Services.Configuration;

namespace optimizerDuck.Domain.Optimizations.Models.Tools;

/// <summary>A Windows optional feature as DISM reports it.</summary>
public partial class OptionalFeature : LocalizedObject
{
    /// <summary>The features worth a plain-language description, keyed by DISM name.</summary>
    public static readonly HashSet<string> Described = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft-Hyper-V-All",
        "Microsoft-Windows-Subsystem-Linux",
        "VirtualMachinePlatform",
        "Containers-DisposableClientVM",
        "NetFx3",
        "SMB1Protocol",
        "TelnetClient",
        "Printing-PrintToPDFServices-Features",
        "MediaPlayback",
        "Recall",
    };

    /// <summary>The DISM feature name; technical, so it is shown as it is.</summary>
    public required string FeatureName { get; init; }

    /// <summary>The state DISM reported, for example Enabled or DisabledWithPayloadRemoved.</summary>
    [ObservableProperty]
    private string _state = string.Empty;

    [ObservableProperty]
    private bool _isEnabled;

    public bool IsDescribed => Described.Contains(FeatureName);

    public string Description =>
        IsDescribed
            ? Loc.Instance[$"OptionalFeatures.Feature.{FeatureName.Replace('-', '_')}"]
            : string.Empty;
}
