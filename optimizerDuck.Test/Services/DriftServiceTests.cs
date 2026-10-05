using optimizerDuck.Domain.Execution;
using optimizerDuck.Services.Optimization;

namespace optimizerDuck.Test.Services;

public class DriftServiceTests
{
    [Fact]
    public void PendingSteps_OnlyPlannedChangesCount()
    {
        var preview = new ChangeSet();
        preview.AddPlanned("Registry", "write A");
        preview.AddSkip("Registry", "write B");
        preview.AddNotApplicable("Service", "missing");

        var pending = DriftService.PendingSteps(preview, null);

        Assert.Equal("write A", Assert.Single(pending).Description);
    }

    [Fact]
    public void PendingSteps_StepTheLastApplyCouldNotMake_IsNotDrift()
    {
        var applied = new ChangeSet();
        applied.Add("Registry", "write A", true);
        applied.Add("Registry", "write B", false, error: "denied");
        applied.AddRefused("Registry", "write C");
        var record = ChangeRecord.From(
            applied,
            new OperationSubject(Guid.NewGuid(), "Key", "Name"),
            "PartialSuccess"
        );

        var preview = new ChangeSet();
        preview.AddPlanned("Registry", "write A");
        preview.AddPlanned("Registry", "write B");
        preview.AddPlanned("Registry", "write C");

        var pending = DriftService.PendingSteps(preview, record);

        Assert.Equal("write A", Assert.Single(pending).Description);
    }

    [Fact]
    public void PendingSteps_AfterARevertRecord_CountsEverything()
    {
        var applied = new ChangeSet();
        applied.Add("Registry", "write B", false, error: "denied");
        var record = ChangeRecord
            .From(applied, new OperationSubject(Guid.NewGuid(), "Key", "Name"), "Failed")
            .Reverted(DateTime.Now);

        var preview = new ChangeSet();
        preview.AddPlanned("Registry", "write B");

        Assert.Single(DriftService.PendingSteps(preview, record));
    }
}
