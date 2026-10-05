using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using optimizerDuck.Common.Extensions;
using optimizerDuck.Common.Helpers;

namespace optimizerDuck.UI.ViewModels.Windows;

public partial class MainWindowViewModel : LocalizedObject
{
    /// <summary>
    ///     Gets the application version displayed in the title bar.
    /// </summary>
    public string Version => $"[v{Shared.FileVersion}]";

    /// <summary>
    ///     Opens the support/donation link in the default browser.
    /// </summary>
    [RelayCommand]
    private static void OpenSupportLink()
    {
        try
        {
            ShellLauncher.OpenUrl(Shared.ContributeURL);
        }
        catch
        {
            // Silently fail: opening a link is non-critical.
        }
    }

    /// <summary>
    ///     Opens the Discord invite link in the default browser.
    /// </summary>
    [RelayCommand]
    private static void OpenDiscordLink()
    {
        try
        {
            ShellLauncher.OpenUrl(Shared.DiscordInviteURL);
        }
        catch
        {
            // Silently fail: opening a link is non-critical.
        }
    }
}
