using CommunityToolkit.Mvvm.ComponentModel;

namespace optimizerDuck.Domain.Optimizations.Models.Tools;

/// <summary>A shell extension that adds entries to the File Explorer context menu.</summary>
public partial class ContextMenuEntry : ObservableObject
{
    /// <summary>The COM class id the handler is registered under, with braces.</summary>
    public required string Clsid { get; init; }

    public required string Name { get; init; }

    /// <summary>The DLL that implements the handler, or empty when it cannot be resolved.</summary>
    public string DllPath { get; init; } = string.Empty;

    /// <summary>Where the handler appears: files, folders, folder background, drives.</summary>
    public required string Locations { get; init; }

    /// <summary>Whether the handler ships with Windows (its DLL lives under the Windows folder).</summary>
    public bool IsSystem { get; init; }

    [ObservableProperty]
    private bool _isEnabled;
}
