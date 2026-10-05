using System.IO;
using Microsoft.Extensions.Logging;
using optimizerDuck.Resources.Languages;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.System.Primitives;
using Wpf.Ui.Controls;
using CleanupItem = optimizerDuck.Domain.Optimizations.Models.Cleanup.CleanupItem;

namespace optimizerDuck.Services.UI;

public class DiskCleanupService(ILogger<DiskCleanupService> logger)
{
    private const string RecycleBinItemId = "RecycleBin";

    private static readonly string DotNetTempPath =
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), ".net"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    /// <summary>
    ///     Gets the available cleanup items.
    /// </summary>
    /// <returns>A list of cleanup items.</returns>
    public static List<CleanupItem> GetCleanupItems()
    {
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData
        );
        var systemDrive = Path.GetPathRoot(windowsDir) ?? "C:\\";

        return
        [
            new CleanupItem
            {
                Id = "TempFiles",
                NameKey = "DiskCleanup.Item.TempFiles",
                DescriptionKey = "DiskCleanup.Item.TempFiles.Description",
                Path = Path.GetTempPath(),
                Icon = SymbolRegular.Document24,
            },
            new CleanupItem
            {
                Id = "SystemTemp",
                NameKey = "DiskCleanup.Item.SystemTemp",
                DescriptionKey = "DiskCleanup.Item.SystemTemp.Description",
                Path = Path.Combine(windowsDir, "Temp"),
                Icon = SymbolRegular.DocumentError24,
            },
            new CleanupItem
            {
                Id = "WindowsUpdate",
                NameKey = "DiskCleanup.Item.WindowsUpdate",
                DescriptionKey = "DiskCleanup.Item.WindowsUpdate.Description",
                Path = Path.Combine(windowsDir, @"SoftwareDistribution\Download"),
                Icon = SymbolRegular.ArrowDownload24,
            },
            new CleanupItem
            {
                Id = "Prefetch",
                NameKey = "DiskCleanup.Item.Prefetch",
                DescriptionKey = "DiskCleanup.Item.Prefetch.Description",
                Path = Path.Combine(windowsDir, "Prefetch"),
                Icon = SymbolRegular.Flash24,
            },
            new CleanupItem
            {
                Id = "Thumbnails",
                NameKey = "DiskCleanup.Item.Thumbnails",
                DescriptionKey = "DiskCleanup.Item.Thumbnails.Description",
                Path = Path.Combine(localAppData, @"Microsoft\Windows\Explorer"),
                Icon = SymbolRegular.Image24,
            },
            new CleanupItem
            {
                Id = RecycleBinItemId,
                NameKey = "DiskCleanup.Item.RecycleBin",
                DescriptionKey = "DiskCleanup.Item.RecycleBin.Description",
                // Not a directory: size and emptying come from the shell Recycle Bin APIs.
                Path = string.Empty,
                Icon = SymbolRegular.Delete24,
                IsCommand = true,
            },
            new CleanupItem
            {
                Id = "ErrorReports",
                NameKey = "DiskCleanup.Item.ErrorReports",
                DescriptionKey = "DiskCleanup.Item.ErrorReports.Description",
                Path = Path.Combine(localAppData, "CrashDumps"),
                Icon = SymbolRegular.Bug24,
            },
            new CleanupItem
            {
                Id = "OldWindowsInstallation",
                NameKey = "DiskCleanup.Item.OldWindowsInstallation",
                DescriptionKey = "DiskCleanup.Item.OldWindowsInstallation.Description",
                Path = Path.Combine(systemDrive, "Windows.old"),
                Icon = SymbolRegular.Building24,
            },
        ];
    }

    /// <summary>
    ///     Scans a cleanup item to calculate its size and file count.
    /// </summary>
    /// <param name="item">The cleanup item to scan.</param>
    public async Task ScanAsync(CleanupItem item)
    {
        item.IsScanning = true;
        try
        {
            if (item.Id == RecycleBinItemId)
            {
                var totals = RecycleBinService.Query();
                item.SizeBytes = totals.SizeBytes;
                item.FileCount = totals.ItemCount;
            }
            else if (!item.IsCommand)
            {
                // Command items have no path to walk; their size stays unknown until they run.
                var metrics = await Task.Run(() => CalculateDirectoryMetrics(item.Path, item.Id));
                item.SizeBytes = metrics.Size;
                item.FileCount = metrics.Count;
            }

            item.IsScanned = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to scan {ItemId}", item.Id);
            item.SizeBytes = 0;
            item.FileCount = 0;
            item.IsScanned = true;
        }
        finally
        {
            item.IsScanning = false;
        }
    }

    /// <summary>
    ///     Scans all cleanup items in parallel.
    /// </summary>
    /// <param name="items">The items to scan.</param>
    public async Task ScanAllAsync(IEnumerable<CleanupItem> items)
    {
        await Task.WhenAll(items.Select(ScanAsync));
    }

    /// <summary>
    ///     Cleans a single cleanup item.
    /// </summary>
    /// <param name="item">The item to clean.</param>
    /// <returns>The number of bytes freed.</returns>
    public async Task<long> CleanAsync(CleanupItem item)
    {
        item.IsCleaning = true;
        long freedBytes = 0;

        try
        {
            if (item.Id == RecycleBinItemId)
            {
                var sizeBefore = item.SizeBytes;
                var (succeeded, errorCode) = RecycleBinService.Empty();
                if (succeeded)
                {
                    freedBytes = sizeBefore;
                    logger.LogInformation(
                        "Emptied {ItemId} via shell, freed ~{Size}",
                        item.Id,
                        CleanupItem.FormatBytes(freedBytes)
                    );
                }
                else
                {
                    // Never report freed space for an empty that failed; the HRESULT is the only
                    // thing that distinguishes "emptied" from "did nothing".
                    logger.LogError(
                        "Emptying {ItemId} failed with HRESULT 0x{Code:X8}",
                        item.Id,
                        errorCode
                    );
                }
            }
            else
            {
                freedBytes = await Task.Run(() => DeleteFilesInDirectory(item.Path, item.Id));
                item.SizeBytes = Math.Max(0, item.SizeBytes - freedBytes);
                logger.LogInformation(
                    "Cleaned {ItemId}, freed {Size}",
                    item.Id,
                    CleanupItem.FormatBytes(freedBytes)
                );
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to clean {ItemId}", item.Id);
        }
        finally
        {
            item.IsCleaning = false;
        }

        return freedBytes;
    }

    /// <summary>
    ///     Cleans all selected items.
    /// </summary>
    /// <param name="items">The items to clean.</param>
    /// <returns>The total number of bytes freed.</returns>
    public async Task<long> CleanSelectedAsync(IEnumerable<CleanupItem> items)
    {
        long totalFreed = 0;
        foreach (var item in items.Where(i => i is { IsSelected: true, SizeBytes: > 0 }))
            totalFreed += await CleanAsync(item);
        return totalFreed;
    }

    private (long Size, long Count) CalculateDirectoryMetrics(string path, string itemId)
    {
        if (!Directory.Exists(path))
            return (0, 0);

        long size = 0;
        long count = 0;

        try
        {
            foreach (var file in EnumerateCleanable(path, itemId).OfType<FileInfo>())
                try
                {
                    // .Length is already cached from the enumeration (WIN32_FIND_DATA)
                    size += file.Length;
                    count++;
                }
                catch
                {
                    // Ignore inaccessible single files
                }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error calculating metrics for {ItemId} at {Path}", itemId, path);
        }

        return (size, count);
    }

    private long DeleteFilesInDirectory(string path, string itemId)
    {
        if (!Directory.Exists(path))
            return 0;

        // The real location of the root: every delete is checked against it through the handle
        // that performs the delete, so a link planted in a user-writable folder cannot redirect
        // an elevated delete elsewhere.
        var resolvedRoot = ConfinedDelete.ResolveRoot(path);
        if (resolvedRoot is null)
        {
            logger.LogWarning("Cleanup root {Path} could not be opened", path);
            return 0;
        }

        long freed = 0;
        foreach (var entry in EnumerateCleanable(path, itemId))
            try
            {
                var deleted = ConfinedDelete.TryDelete(entry.FullName, resolvedRoot);
                if (deleted is { } length)
                    freed += length;
            }
            catch
            {
                // skip locked/inaccessible entries; a directory that is not empty stays
            }

        return freed;
    }

    /// <summary>
    ///     The files of a cleanup item, then its directories deepest first, never descending
    ///     into a junction or a symbolic link and never touching the active .NET scratch folder.
    /// </summary>
    private static IEnumerable<FileSystemInfo> EnumerateCleanable(string path, string itemId)
    {
        var searchPattern = itemId == "Thumbnails" ? "thumbcache_*" : "*";
        var isRecursive = itemId != "Thumbnails";

        // Only filter the .net path if the .net temp directory is a descendant of the scan root
        var needsDotNetFilter =
            isRecursive
            && DotNetTempPath.StartsWith(
                path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            );

        foreach (
            var entry in ConfinedDelete.Walk(new DirectoryInfo(path), searchPattern, isRecursive)
        )
        {
            if (needsDotNetFilter)
            {
                var directory =
                    (entry is FileInfo file ? file.DirectoryName : entry.FullName) ?? string.Empty;
                directory =
                    directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (directory.StartsWith(DotNetTempPath, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            yield return entry;
        }
    }
}
