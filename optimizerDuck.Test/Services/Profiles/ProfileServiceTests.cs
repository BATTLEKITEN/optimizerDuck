using Newtonsoft.Json.Linq;
using optimizerDuck.Domain.Profiles;
using optimizerDuck.Services.Profiles;

namespace optimizerDuck.Test.Services.Profiles;

public class ProfileServiceTests
{
    [Fact]
    public void Serialize_ThenParse_RoundTrips()
    {
        var id = Guid.NewGuid();
        var profile = new OptimizerProfile
        {
            Name = "Mine",
            AppVersion = "1.0.0",
            CreatedAt = new DateTime(2026, 1, 1),
            Optimizations = [id],
            Customize = { ["Desktop.Thing"] = true, ["Gaming.Mode"] = 2 },
        };

        var read = ProfileService.Parse(ProfileService.Serialize(profile));

        Assert.Equal("Mine", read.Name);
        Assert.Equal([id], read.Optimizations);
        Assert.True(read.Customize["Desktop.Thing"]!.Value<bool>());
        Assert.Equal(2, read.Customize["Gaming.Mode"]!.Value<int>());
    }

    [Fact]
    public void Parse_DuplicateIds_AreCollapsed()
    {
        var id = Guid.NewGuid();
        var json = $$"""{"SchemaVersion":1,"Optimizations":["{{id}}","{{id}}"]}""";

        Assert.Single(ProfileService.Parse(json).Optimizations);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"SchemaVersion":99}""")]
    [InlineData("""{"SchemaVersion":1,"Optimizations":["not-a-guid"]}""")]
    public void Parse_InvalidProfile_Throws(string json)
    {
        Assert.Throws<ProfileFormatException>(() => ProfileService.Parse(json));
    }

    [Fact]
    public void Parse_MissingCollections_DefaultToEmpty()
    {
        var profile = ProfileService.Parse("""{"SchemaVersion":1,"Optimizations":null}""");

        Assert.Empty(profile.Optimizations);
        Assert.Empty(profile.Customize);
    }

    [Fact]
    public void Load_OversizedFile_Throws()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"optimizerDuck_{Guid.NewGuid():N}.duckprofile"
        );
        try
        {
            File.WriteAllText(path, new string(' ', (int)ProfileService.MaxFileBytes + 1));

            Assert.Throws<ProfileFormatException>(() => ProfileService.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"optimizerDuck_{Guid.NewGuid():N}.duckprofile"
        );
        try
        {
            var profile = new OptimizerProfile { Name = "Saved", Optimizations = [Guid.NewGuid()] };
            ProfileService.Save(profile, path);

            var read = ProfileService.Load(path);

            Assert.Equal(profile.Optimizations, read.Optimizations);
            Assert.Equal("Saved", read.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
