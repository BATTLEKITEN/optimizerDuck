using optimizerDuck.Common.Helpers;
using optimizerDuck.Services.System;

namespace optimizerDuck.Test.Services;

/// <summary>
///     Pins how the update check identifies itself and when a release counts as an update.
/// </summary>
public class UpdaterServiceTests
{
    private const string Hash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    [Fact]
    public void ParseChecksum_ReadsTheLineForTheFile()
    {
        var text =
            $"{new string('0', 64)}  other.exe\n{Hash}  optimizerDuck-Windows-x64-3.0.0.exe\n";

        Assert.Equal(
            Hash,
            UpdaterService.ParseChecksum(text, "optimizerDuck-Windows-x64-3.0.0.exe")
        );
    }

    [Fact]
    public void ParseChecksum_AcceptsBinaryMarkerAndBareHash()
    {
        Assert.Equal(Hash, UpdaterService.ParseChecksum($"{Hash} *app.exe", "app.exe"));
        Assert.Equal(Hash, UpdaterService.ParseChecksum(Hash, "app.exe"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a hash  app.exe")]
    [InlineData("9f86d081  app.exe")]
    public void ParseChecksum_NoUsableLine_ReturnsNull(string text)
    {
        Assert.Null(UpdaterService.ParseChecksum(text, "app.exe"));
    }

    [Fact]
    public void ParseChecksum_OtherFileOnly_ReturnsNull()
    {
        Assert.Null(UpdaterService.ParseChecksum($"{Hash}  other.exe", "app.exe"));
    }

    [Fact]
    public void BuildUserAgentHeader_NamesTheRunningBuild()
    {
        var header = UpdaterService.BuildUserAgentHeader();

        Assert.Equal("optimizerDuck", header.Product?.Name);
        Assert.Equal(Shared.FileVersion, header.Product?.Version);
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3-fix", "1.2.3")]
    [InlineData("v2.0", "2.0")]
    public void ParseReleaseTag_ReadsTheVersionItNames(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), UpdaterService.ParseReleaseTag(tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("v")]
    public void ParseReleaseTag_WithoutAVersion_IsNothing(string? tag)
    {
        Assert.Null(UpdaterService.ParseReleaseTag(tag));
    }

    [Fact]
    public void FindExecutableAsset_WithoutTheExecutable_IsNothing()
    {
        Assert.Null(UpdaterService.FindExecutableAsset(null));
        Assert.Null(UpdaterService.FindExecutableAsset(Release("optimizerDuck.zip")));
        Assert.Null(UpdaterService.FindExecutableAsset(Release("notes.txt")));
    }

    [Fact]
    public void FindExecutableAsset_WithTheExecutable_FindsIt()
    {
        var asset = UpdaterService.FindExecutableAsset(Release("optimizerDuck-v1.2.3-win.exe"));

        Assert.NotNull(asset);
        Assert.Equal("optimizerDuck-v1.2.3-win.exe", asset!.Name);
    }

    private static GitHubRelease Release(params string[] assetNames)
    {
        return new GitHubRelease
        {
            TagName = "v1.2.3",
            Body = string.Empty,
            Assets =
            [
                .. assetNames.Select(name => new GitHubAsset
                {
                    Name = name,
                    BrowserDownloadUrl = $"https://example.invalid/{name}",
                }),
            ],
        };
    }
}
