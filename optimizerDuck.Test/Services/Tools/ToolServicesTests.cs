using optimizerDuck.Services.System;
using optimizerDuck.Services.UI;

namespace optimizerDuck.Test.Services.Tools;

public class ToolServicesTests
{
    [Fact]
    public void AppCatalog_IdsAreValidAndUnique()
    {
        var catalog = AppInstallerService.Catalog();

        Assert.NotEmpty(catalog);
        Assert.All(catalog, app => Assert.True(AppInstallerService.IsValidId(app.PackageId)));
        Assert.Equal(catalog.Count, catalog.Select(a => a.PackageId).Distinct().Count());
    }

    [Theory]
    [InlineData("Mozilla.Firefox", true)]
    [InlineData("Microsoft.VCRedist.2015+.x64", true)]
    [InlineData("Evil & calc", false)]
    [InlineData("a\"b", false)]
    [InlineData("", false)]
    public void IsValidId_RejectsShellCharacters(string id, bool expected)
    {
        Assert.Equal(expected, AppInstallerService.IsValidId(id));
    }

    [Theory]
    [InlineData("Microsoft-Hyper-V-All", true)]
    [InlineData("NetFx3", true)]
    [InlineData("x'; Remove-Item C:\\ -Recurse; '", false)]
    public void IsValidName_RejectsInjection(string name, bool expected)
    {
        Assert.Equal(expected, OptionalFeaturesService.IsValidName(name));
    }

    [Fact]
    public void ParseFeatures_ReadsArrayAndSingleObject()
    {
        var many = OptionalFeaturesService.Parse(
            """[{"FeatureName":"NetFx3","State":"Enabled"},{"FeatureName":"TelnetClient","State":"Disabled"},{"FeatureName":"bad name;","State":"Enabled"}]"""
        );
        var one = OptionalFeaturesService.Parse(
            """{"FeatureName":"NetFx3","State":"EnablePending"}"""
        );

        Assert.Equal(2, many.Count);
        Assert.True(many.Single(f => f.FeatureName == "NetFx3").IsEnabled);
        Assert.False(many.Single(f => f.FeatureName == "TelnetClient").IsEnabled);
        Assert.True(Assert.Single(one).IsEnabled);
        Assert.Empty(OptionalFeaturesService.Parse(""));
    }

    [Theory]
    [InlineData("{b2f1b0b8-6e6a-4b1a-9d59-1c2f0f7e3a10}", "{B2F1B0B8-6E6A-4B1A-9D59-1C2F0F7E3A10}")]
    [InlineData("B2F1B0B8-6E6A-4B1A-9D59-1C2F0F7E3A10", "{B2F1B0B8-6E6A-4B1A-9D59-1C2F0F7E3A10}")]
    [InlineData("7-Zip", null)]
    [InlineData(null, null)]
    public void NormalizeClsid_AcceptsOnlyGuids(string? text, string? expected)
    {
        Assert.Equal(expected, ContextMenuService.NormalizeClsid(text));
    }

    [Fact]
    public void HealthToHtml_EncodesEverything()
    {
        var html = HealthCheckService.ToHtml(
            [new HealthCheck("<script>", HealthStatus.Bad, "a & b")],
            new DateTime(2026, 1, 1)
        );

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("a &amp; b", html);
    }
}
