using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Models.Services;
using optimizerDuck.Domain.Optimizations.Models.Tools;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.System.Primitives;

namespace optimizerDuck.Services.UI;

/// <summary>
///     Lists the shell extension handlers that add File Explorer context menu entries and turns
///     them off through the documented <c>Shell Extensions\Blocked</c> list, which Windows reads
///     before loading any handler. Nothing is deleted: removing the entry from the list brings
///     the handler back.
/// </summary>
public class ContextMenuService(ILogger<ContextMenuService> logger)
{
    internal const string BlockedKey =
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    private static readonly (string Root, string Label)[] Roots =
    [
        ("*", "Files"),
        ("AllFilesystemObjects", "Files"),
        ("Directory", "Folders"),
        ("Folder", "Folders"),
        ("Directory\\Background", "Background"),
        ("Drive", "Drives"),
    ];

    /// <summary>Every registered context menu handler, one entry per class id.</summary>
    public List<ContextMenuEntry> GetEntries()
    {
        var blocked = ReadBlocked();
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var found = new Dictionary<string, (string Name, string Dll, SortedSet<string> Where)>(
            StringComparer.OrdinalIgnoreCase
        );

        foreach (var (root, label) in Roots)
        {
            try
            {
                using var handlers = Registry.ClassesRoot.OpenSubKey(
                    $@"{root}\shellex\ContextMenuHandlers"
                );
                if (handlers is null)
                    continue;

                foreach (var name in handlers.GetSubKeyNames())
                {
                    using var handler = handlers.OpenSubKey(name);
                    var clsid =
                        NormalizeClsid(handler?.GetValue(null) as string) ?? NormalizeClsid(name);
                    if (clsid is null)
                        continue;

                    if (!found.TryGetValue(clsid, out var entry))
                    {
                        var (display, dll) = DescribeClsid(clsid);
                        entry = (
                            string.IsNullOrWhiteSpace(display) ? name : display,
                            dll,
                            new SortedSet<string>()
                        );
                        found[clsid] = entry;
                    }

                    entry.Where.Add(Loc.Instance[$"ContextMenu.Location.{label}"]);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read context menu handlers under {Root}", root);
            }
        }

        return found
            .Select(pair => new ContextMenuEntry
            {
                Clsid = pair.Key,
                Name = pair.Value.Name,
                DllPath = pair.Value.Dll,
                Locations = string.Join(", ", pair.Value.Where),
                IsSystem =
                    pair.Value.Dll.Length > 0
                    && pair.Value.Dll.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase),
                IsEnabled = !blocked.Contains(pair.Key),
            })
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Removes the handler from the blocked list or adds it there.</summary>
    public static OpResult SetEnabled(OpCall call, string clsid, bool enabled)
    {
        var item = new RegistryItem(BlockedKey, clsid, string.Empty);
        return enabled
            ? RegistryService.DeleteValue(call, item)
            : RegistryService.Write(call, item);
    }

    private static HashSet<string> ReadBlocked()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked"
        );
        return new HashSet<string>(
            key?.GetValueNames().Select(NormalizeClsid).OfType<string>() ?? [],
            StringComparer.OrdinalIgnoreCase
        );
    }

    private static (string Name, string Dll) DescribeClsid(string clsid)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}");
        var name = key?.GetValue(null) as string ?? string.Empty;
        using var server = key?.OpenSubKey("InprocServer32");
        var dll = Environment.ExpandEnvironmentVariables(
            (server?.GetValue(null) as string ?? string.Empty).Trim('"')
        );
        if (string.IsNullOrWhiteSpace(name) && dll.Length > 0)
            name = Path.GetFileNameWithoutExtension(dll);
        return (name, dll);
    }

    internal static string? NormalizeClsid(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return Guid.TryParse(text.Trim().Trim('{', '}'), out var id)
            ? id.ToString("B").ToUpperInvariant()
            : null;
    }
}
