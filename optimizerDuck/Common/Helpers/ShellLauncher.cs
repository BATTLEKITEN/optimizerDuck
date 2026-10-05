using System.Diagnostics;
using System.IO;

namespace optimizerDuck.Common.Helpers;

/// <summary>
///     Opens links and folders from the elevated app. A path handed to the shell would be run
///     by whatever it names, so folders open through Explorer only after the path proves to be a
///     directory, and links open only when they are HTTPS.
/// </summary>
public static class ShellLauncher
{
    /// <summary>Opens an HTTPS link in the default browser.</summary>
    /// <exception cref="ArgumentException">The link is not an absolute HTTPS URL.</exception>
    public static void OpenUrl(string url)
    {
        if (!IsHttpsUrl(url))
            throw new ArgumentException($"Refusing to open a non HTTPS link: {url}", nameof(url));

        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    /// <summary>Opens an existing directory in Explorer.</summary>
    /// <exception cref="DirectoryNotFoundException">The path is not an existing directory.</exception>
    public static void OpenFolder(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException(full);

        // A drive root keeps its separator; anything else drops it, because a backslash before
        // the closing quote would escape the quote.
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar);
        StartExplorer(Path.GetPathRoot(full) == full ? full : $"\"{trimmed}\"");
    }

    /// <summary>Opens Explorer with an existing file or directory selected.</summary>
    /// <exception cref="FileNotFoundException">Nothing exists at the path.</exception>
    public static void Reveal(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full))
            throw new FileNotFoundException(full);

        StartExplorer($"/select,\"{full}\"");
    }

    /// <summary>Whether the text is an absolute HTTPS URL.</summary>
    public static bool IsHttpsUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps;
    }

    private static void StartExplorer(string arguments)
    {
        var explorer = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe"
        );
        Process
            .Start(
                new ProcessStartInfo
                {
                    FileName = explorer,
                    Arguments = arguments,
                    UseShellExecute = false,
                }
            )
            ?.Dispose();
    }
}
