using System.Collections;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Resources.Languages;
using optimizerDuck.Services.Optimization;

namespace optimizerDuck.Test.Services;

public class SideEffectsTests
{
    [Fact]
    public async Task SideEffectsKeys_EachNamesAnExistingOptimization()
    {
        var registry = new OptimizationRegistry(NullLoggerFactory.Instance);
        await registry.PreloadOptimizationsAsync();
        var known = registry
            .OptimizationCategories.SelectMany(c => c.Optimizations)
            .OfType<BaseOptimization>()
            .Select(o => $"Optimizer.{o.OwnerKey}.{o.OptimizationKey}.SideEffects")
            .ToHashSet();

        var keys = Translations
            .ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!
            .Cast<DictionaryEntry>()
            .Select(e => (string)e.Key)
            .Where(k => k.StartsWith("Optimizer.", StringComparison.Ordinal))
            .Where(k => k.EndsWith(".SideEffects", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.Contains(key, known));
    }

    [Fact]
    public async Task SideEffects_ResolvesForAnItemThatHasOne()
    {
        var registry = new OptimizationRegistry(NullLoggerFactory.Instance);
        await registry.PreloadOptimizationsAsync();
        var oneDrive = registry
            .OptimizationCategories.SelectMany(c => c.Optimizations)
            .OfType<BaseOptimization>()
            .Single(o => o.OptimizationKey == "DisableOneDrive");

        Assert.True(oneDrive.HasSideEffects);
        Assert.DoesNotContain("SideEffects", oneDrive.SideEffects);
    }
}
