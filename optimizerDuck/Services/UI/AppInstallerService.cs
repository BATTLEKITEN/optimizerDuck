using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Models.Services;
using optimizerDuck.Domain.Optimizations.Models.Tools;
using optimizerDuck.Services.System.Primitives;

namespace optimizerDuck.Services.UI;

/// <summary>
///     Installs popular apps through winget, Microsoft's package manager, from a fixed list of
///     package ids. Nothing typed by the user reaches the command line.
/// </summary>
public partial class AppInstallerService(ShellService shell, ILogger<AppInstallerService> logger)
{
    /// <summary>winget's "already installed" exit code (APPINSTALLER_CLI_ERROR_PACKAGE_ALREADY_INSTALLED).</summary>
    internal const int AlreadyInstalled = unchecked((int)0x8A150061);

    /// <summary>winget's "no newer version" exit code (APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE).</summary>
    internal const int NoNewerVersion = unchecked((int)0x8A15002B);

    [GeneratedRegex("^[A-Za-z0-9.+_-]+$")]
    private static partial Regex PackageIdRegex();

    /// <summary>The apps on offer, in display order.</summary>
    public static IReadOnlyList<InstallableApp> Catalog() =>
        [
            App("Firefox", "Mozilla.Firefox", "Browsers"),
            App("Google Chrome", "Google.Chrome", "Browsers"),
            App("Brave", "Brave.Brave", "Browsers"),
            App("7-Zip", "7zip.7zip", "Utilities"),
            App("Everything", "voidtools.Everything", "Utilities"),
            App("PowerToys", "Microsoft.PowerToys", "Utilities"),
            App("Notepad++", "Notepad++.Notepad++", "Utilities"),
            App("ShareX", "ShareX.ShareX", "Utilities"),
            App("VLC media player", "VideoLAN.VLC", "Media"),
            App("OBS Studio", "OBSProject.OBSStudio", "Media"),
            App("Steam", "Valve.Steam", "Gaming"),
            App("Epic Games Launcher", "EpicGames.EpicGamesLauncher", "Gaming"),
            App("Discord", "Discord.Discord", "Gaming"),
            App("Visual Studio Code", "Microsoft.VisualStudioCode", "Development"),
            App("Git", "Git.Git", "Development"),
            App("Windows Terminal", "Microsoft.WindowsTerminal", "Development"),
            App(".NET Desktop Runtime 8", "Microsoft.DotNet.DesktopRuntime.8", "Runtimes"),
            App("Visual C++ Redistributable", "Microsoft.VCRedist.2015+.x64", "Runtimes"),
        ];

    /// <summary>Whether a package id is safe to put on a command line.</summary>
    public static bool IsValidId(string id) => PackageIdRegex().IsMatch(id);

    /// <summary>Whether winget is installed and runs.</summary>
    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var result = await shell
                .QueryCMDAsync("winget --version", logger)
                .ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "winget is not available");
            return false;
        }
    }

    /// <summary>Installs one package machine-wide and silently, recording the step.</summary>
    public Task<OpResult> InstallAsync(OpCall call, InstallableApp app)
    {
        if (!IsValidId(app.PackageId))
            throw new ArgumentException($"Invalid package id: {app.PackageId}", nameof(app));

        return shell.CMDAsync(
            $"winget install --id {app.PackageId} --exact --silent --accept-source-agreements "
                + "--accept-package-agreements --disable-interactivity",
            call,
            revertStep: null,
            policy: ShellPolicy.From(r => r.ExitCode is 0 or AlreadyInstalled or NoNewerVersion)
        );
    }

    private static InstallableApp App(string name, string id, string category) =>
        new()
        {
            Name = name,
            PackageId = id,
            Category = category,
        };
}
