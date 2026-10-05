using Microsoft.Extensions.Logging.Abstractions;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Optimization;
using optimizerDuck.Services.Revert;
using optimizerDuck.Services.System;
using optimizerDuck.Test.TestDoubles;

namespace optimizerDuck.Test.Services;

public class PreviewIntegrationTests
{
    /// <summary>
    ///     A preview of every real optimization must not change the machine. Every provider that
    ///     changes something records how to undo it, so a preview step carrying compensation, or
    ///     any record or revert file appearing, means a mutation slipped past the dry run.
    /// </summary>
    [Fact]
    public async Task PreviewAsync_EveryOptimization_ChangesNothing()
    {
        var registry = new OptimizationRegistry(NullLoggerFactory.Instance);
        await registry.PreloadOptimizationsAsync();
        var runner = TestRunner.New(
            new RevertManager(
                NullLogger<RevertManager>.Instance,
                TestShell.New(),
                new PowerPlanService(NullLogger<PowerPlanService>.Instance),
                TimeProvider.System
            )
        );

        var optimizations = registry
            .OptimizationCategories.SelectMany(c => c.Optimizations)
            .ToList();
        Assert.NotEmpty(optimizations);

        foreach (var optimization in optimizations)
        {
            var revertExisted = File.Exists(
                Path.Combine(Shared.RevertDirectory, optimization.Id + ".json")
            );
            var recordExisted = File.Exists(
                optimizerDuck.Services.History.ChangeRecordStore.PathFor(optimization.Id)
            );

            var preview = await runner.PreviewAsync(
                new OperationRequest(
                    new OperationSubject(
                        optimization.Id,
                        optimization.OptimizationKey,
                        optimization.OptimizationKey
                    ),
                    optimization.OptimizationKey,
                    NullLogger.Instance,
                    RevertPersistence.Disabled,
                    optimization.ApplyAsync
                ),
                TestContext.Current.CancellationToken
            );

            Assert.All(
                preview.Changes,
                change =>
                    Assert.True(
                        change.Revert is null,
                        $"{optimization.OptimizationKey}: step '{change.Description}' changed the machine during a preview"
                    )
            );
            Assert.Equal(
                revertExisted,
                File.Exists(Path.Combine(Shared.RevertDirectory, optimization.Id + ".json"))
            );
            Assert.Equal(
                recordExisted,
                File.Exists(
                    optimizerDuck.Services.History.ChangeRecordStore.PathFor(optimization.Id)
                )
            );
        }
    }
}
