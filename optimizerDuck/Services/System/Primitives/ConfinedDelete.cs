using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace optimizerDuck.Services.System.Primitives;

/// <summary>
///     Deletes files and empty directories under a root without ever following a junction or a
///     symbolic link. The cleanup runs elevated inside folders any user can write to, so a path
///     is opened once, its real location is checked through that handle, and the same handle
///     deletes it: swapping a directory for a link between the check and the delete cannot
///     redirect the delete outside the root.
/// </summary>
internal static class ConfinedDelete
{
    private const uint Delete = 0x00010000;
    private const uint FileReadAttributes = 0x0080;
    private const uint Synchronize = 0x00100000;
    private const uint ShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FlagOpenReparsePoint = 0x00200000;
    private const uint FlagBackupSemantics = 0x02000000;
    private const int FileDispositionInfo = 4;

    /// <summary>
    ///     The real, link-free path of a directory, with a trailing separator, or
    ///     <see langword="null" /> when it cannot be opened.
    /// </summary>
    public static string? ResolveRoot(string directory)
    {
        using var handle = Open(directory, FileReadAttributes | Synchronize);
        if (handle.IsInvalid)
            return null;

        var final = FinalPath(handle);
        return final is null ? null : final.TrimEnd(Path.DirectorySeparatorChar) + '\\';
    }

    /// <summary>
    ///     Deletes a file or an empty directory when it is not a reparse point and its real
    ///     location is under <paramref name="resolvedRoot" />.
    /// </summary>
    /// <returns>The deleted file's length, or <see langword="null" /> when nothing was deleted.</returns>
    public static long? TryDelete(string path, string resolvedRoot)
    {
        using var handle = Open(path, Delete | FileReadAttributes | Synchronize);
        if (handle.IsInvalid)
            return null;

        var attributes = File.GetAttributes(handle);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
            return null;

        var final = FinalPath(handle);
        if (
            final is null
            || !final.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase)
            || final.Length == resolvedRoot.Length
        )
            return null;

        var length = attributes.HasFlag(FileAttributes.Directory)
            ? 0
            : RandomAccess.GetLength(handle);

        var info = new FileDispositionInformation { DeleteFile = 1 };
        return SetFileInformationByHandle(
            handle,
            FileDispositionInfo,
            ref info,
            (uint)Marshal.SizeOf<FileDispositionInformation>()
        )
            ? length
            : null;
    }

    /// <summary>
    ///     Walks a directory tree without descending into junctions or directory symbolic links.
    ///     Directories come back deepest first, after the files they hold.
    /// </summary>
    public static IEnumerable<FileSystemInfo> Walk(
        DirectoryInfo root,
        string searchPattern,
        bool recurse
    )
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        IEnumerable<FileInfo> files;
        try
        {
            files = root.EnumerateFiles(searchPattern, options).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
            if (!file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                yield return file;

        if (!recurse)
            yield break;

        List<DirectoryInfo> directories;
        try
        {
            directories = root.EnumerateDirectories("*", options).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;

            foreach (var entry in Walk(directory, searchPattern, recurse))
                yield return entry;
            yield return directory;
        }
    }

    private static SafeFileHandle Open(string path, uint access)
    {
        return CreateFile(
            path,
            access,
            ShareAll,
            IntPtr.Zero,
            OpenExisting,
            FlagOpenReparsePoint | FlagBackupSemantics,
            IntPtr.Zero
        );
    }

    private static string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length > buffer.Capacity)
        {
            buffer = new StringBuilder((int)length);
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        }

        if (length == 0)
            return null;

        var path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path[4..];
        return path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        public byte DeleteFile;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile
    );

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle hFile,
        int fileInformationClass,
        ref FileDispositionInformation lpFileInformation,
        uint dwBufferSize
    );
}
