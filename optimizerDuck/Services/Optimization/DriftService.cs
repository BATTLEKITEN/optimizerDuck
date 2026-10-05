using Microsoft.Extensions.Logging;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Services.Conditions;
using optimizerDuck.Services.History;
using optimizerDuck.Services.Revert;
using optimizerDuck.Services.System;

namespace optimizerDuck.Services.Optimization;

/// <summary>An applied optimization whose effect is no longer in place.</summary>
/// <param name="Optimization">The optimization that drifted.</param>
/// <param name="PendingSteps">The steps a fresh apply would take to put it back.</param>
public sealed record DriftEntry(IOptimization Optimization, IReadOnlyList<Change> PendingSteps);

/// <summary>
///     Finds applied optimizations that Windows (usually a feature update) or another program
///     has put back to their old state. Each applied item is previewed: a preview that would
///     change something means the item no longer holds, and nothing is changed by the check.
/// </summary>
public class DriftService(
    OptimizationRegistry registry,
    OptimizationService optimizationService,
    SystemInfoService systemInfoService,
    ILogger<DriftService> logger
)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Gets the outcome of the most recent check.</summary>
    public IReadOnlyList<DriftEntry> LastResult { get; private set; } = [];

    /// <summary>Raised after every completed check.</summary>
    public event EventHandler? Checked;

    /// <summary>Previews every applied optimization and returns the ones that drifted.</summary>
    /// <param name="cancellationToken">The token that cancels the check.</param>
    public async Task<IReadOnlyList<DriftEntry>> CheckAsync(
        CancellationToken cancellationToken = default
    )
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await registry.EnsurePreloadedAsync().ConfigureAwait(false);
            var snapshot = await systemInfoService
                .EnsureSnapshotAsync(cancellationToken)
                .ConfigureAwait(false);

            var drifted = new List<DriftEntry>();
            foreach (
                var optimization in registry.OptimizationCategories.SelectMany(c => c.Optimizations)
            )
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await RevertManager.IsAppliedAsync(optimization.Id).ConfigureAwait(false))
                    continue;

                if (
                    ConditionEvaluator
                        .Evaluate(optimization.ConditionType, snapshot, logger)
                        .IsBlocking
                )
                    continue;

                var preview = await Task.Run(
                        () => optimizationService.PreviewAsync(optimization, cancellationToken),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                var pending = PendingSteps(
                    preview,
                    ChangeRecordStore.TryRead(optimization.Id, logger)
                );
                if (pending.Count == 0)
                    continue;

                logger.LogInformation(
                    "{Key} no longer holds: {Count} step(s) would be reapplied",
                    optimization.OptimizationKey,
                    pending.Count
                );
                drifted.Add(new DriftEntry(optimization, pending));
            }

            LastResult = drifted;
            Checked?.Invoke(this, EventArgs.Empty);
            return drifted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     The steps of a preview that would change the machine, leaving out the ones the last
    ///     apply found refused or not applicable: those never held, so they did not drift, and
    ///     reporting them would flag the same item after every check. A step that failed still
    ///     counts, so a failed apply stays visible until it is reapplied.
    /// </summary>
    internal static IReadOnlyList<Change> PendingSteps(ChangeSet preview, ChangeRecord? lastApply)
    {
        var neverHeld = lastApply is { Operation: ChangeRecordOperation.Apply }
            ? lastApply
                .Steps.Where(static s =>
                    s.Ok && s.Kind is ChangeKind.Refused or ChangeKind.NotApplicable
                )
                .Select(static s => (s.Name, s.Description))
                .ToHashSet()
            : [];

        return preview
            .Changes.Where(c =>
                c.Ok
                && c.Kind is ChangeKind.Change or ChangeKind.Irreversible
                && !neverHeld.Contains((c.Name, c.Description))
            )
            .ToList();
    }
}
