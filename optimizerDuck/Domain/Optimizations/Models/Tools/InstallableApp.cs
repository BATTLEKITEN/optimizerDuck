using CommunityToolkit.Mvvm.ComponentModel;

namespace optimizerDuck.Domain.Optimizations.Models.Tools;

/// <summary>An app the installer offers, by its winget package id.</summary>
public partial class InstallableApp : ObservableObject
{
    /// <summary>The product name; a brand, so it is not translated.</summary>
    public required string Name { get; init; }

    /// <summary>The winget package id, matched exactly.</summary>
    public required string PackageId { get; init; }

    /// <summary>The category key, resolved through <c>AppInstaller.Category.{Key}</c>.</summary>
    public required string Category { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}
