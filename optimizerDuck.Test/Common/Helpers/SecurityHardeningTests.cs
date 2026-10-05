using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Configuration;
using optimizerDuck.Services.System;

namespace optimizerDuck.Test.Common.Helpers;

public class SecurityHardeningTests
{
    [Theory]
    [InlineData("https://github.com/itsfatduck/optimizerDuck", true)]
    [InlineData("http://example.com", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData(@"C:\Windows\System32\calc.exe", false)]
    [InlineData("ms-settings:", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsHttpsUrl_OnlyAcceptsAbsoluteHttps(string? url, bool expected)
    {
        Assert.Equal(expected, ShellLauncher.IsHttpsUrl(url));
    }

    [Fact]
    public void OpenUrl_NonHttps_Throws()
    {
        Assert.Throws<ArgumentException>(() => ShellLauncher.OpenUrl(@"C:\Windows\notepad.exe"));
    }

    [Fact]
    public void OpenFolder_MissingDirectory_Throws()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"optimizerDuck_None_{Guid.NewGuid():N}");

        Assert.Throws<DirectoryNotFoundException>(() => ShellLauncher.OpenFolder(missing));
    }

    [Theory]
    [InlineData(0, AppSettings.OptimizeOptions.MinShellTimeoutMs)]
    [InlineData(-5, AppSettings.OptimizeOptions.MinShellTimeoutMs)]
    [InlineData(int.MaxValue, AppSettings.OptimizeOptions.MaxShellTimeoutMs)]
    [InlineData(120000, 120000)]
    public void ClampShellTimeout_PullsValuesIntoRange(int value, int expected)
    {
        Assert.Equal(expected, AppSettings.OptimizeOptions.ClampShellTimeout(value));
    }

    [Fact]
    public void HashMatches_ComparesHexCaseInsensitively()
    {
        var hash = System.Security.Cryptography.SHA256.HashData("duck"u8);
        var hex = Convert.ToHexString(hash);

        Assert.True(StreamService.HashMatches(hash, hex.ToLowerInvariant()));
        Assert.True(StreamService.HashMatches(hash, hex));
        Assert.False(StreamService.HashMatches(hash, new string('0', 64)));
        Assert.False(StreamService.HashMatches(hash, "not hex"));
    }

    [Fact]
    public async Task TryDownloadAsync_NonHttpsUrl_IsRefused()
    {
        using var service = new StreamService(NullLogger<StreamService>.Instance);

        var result = await service.TryDownloadAsync(
            "http://example.com/file.exe",
            "file.exe",
            new string('0', 64)
        );

        Assert.False(result.Ok);
    }

    [Fact]
    public void EnsureAdminOnly_NewDirectory_IsRestrictedToAdministrators()
    {
        var path = Path.Combine(Path.GetTempPath(), $"optimizerDuck_Secure_{Guid.NewGuid():N}");
        try
        {
            Assert.True(SecureDirectory.EnsureAdminOnly(path, NullLogger.Instance));
            Assert.True(SecureDirectory.IsAdminOnly(new DirectoryInfo(path)));
        }
        finally
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }

    [Fact]
    public void EnsureAdminOnly_DirectoryOthersCanWrite_IsMovedAsideAndRecreated()
    {
        var path = Path.Combine(Path.GetTempPath(), $"optimizerDuck_Squat_{Guid.NewGuid():N}");
        var parent = Path.GetDirectoryName(path)!;
        var planted = Path.Combine(path, "planted.exe");
        try
        {
            Directory.CreateDirectory(path);
            var security = new DirectoryInfo(path).GetAccessControl();
            security.AddAccessRule(
                new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    FileSystemRights.Modify,
                    AccessControlType.Allow
                )
            );
            new DirectoryInfo(path).SetAccessControl(security);
            File.WriteAllText(planted, "not ours");

            Assert.False(SecureDirectory.IsAdminOnly(new DirectoryInfo(path)));
            Assert.True(SecureDirectory.EnsureAdminOnly(path, NullLogger.Instance));

            Assert.False(File.Exists(planted));
            Assert.True(SecureDirectory.IsAdminOnly(new DirectoryInfo(path)));
        }
        finally
        {
            foreach (var dir in Directory.GetDirectories(parent, Path.GetFileName(path) + "*"))
                Directory.Delete(dir, true);
        }
    }
}
