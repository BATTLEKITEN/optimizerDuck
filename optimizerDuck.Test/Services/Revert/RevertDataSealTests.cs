using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Revert;
using optimizerDuck.Services.Revert;
using optimizerDuck.Services.System;
using optimizerDuck.Test.Services;
using optimizerDuck.Test.Services.Managers;
using optimizerDuck.Test.TestDoubles;

namespace optimizerDuck.Test.Services.Revert;

public class RevertDataSealTests
{
    private static RevertData Payload(Guid id, string command) =>
        new()
        {
            OptimizationId = id,
            OptimizationName = "SealTest",
            AppliedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Steps =
            [
                new RevertStepData
                {
                    Index = 1,
                    Type = "Shell",
                    Data = new JObject
                    {
                        ["ShellType"] = "CMD",
                        ["Command"] = command,
                        ["When"] = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                        ["Ratio"] = 1.0,
                    },
                },
            ],
        };

    [Fact]
    public void ToJson_ThenVerify_Succeeds()
    {
        var json = RevertDataSeal.ToJson(Payload(Guid.NewGuid(), "exit 0"));

        Assert.True(RevertDataSeal.Verify(json));
    }

    [Fact]
    public void Verify_EditedCommand_Fails()
    {
        var json = RevertDataSeal.ToJson(Payload(Guid.NewGuid(), "exit 0"));

        Assert.False(RevertDataSeal.Verify(json.Replace("exit 0", "exit 1")));
    }

    [Fact]
    public void Verify_MissingSignature_Fails()
    {
        var obj = JObject.Parse(RevertDataSeal.ToJson(Payload(Guid.NewGuid(), "exit 0")));
        obj.Remove(nameof(RevertData.Signature));

        Assert.False(RevertDataSeal.Verify(obj.ToString()));
    }

    [Fact]
    public void Verify_GarbageSignature_Fails()
    {
        var obj = JObject.Parse(RevertDataSeal.ToJson(Payload(Guid.NewGuid(), "exit 0")));
        obj[nameof(RevertData.Signature)] = "not base64 !";

        Assert.False(RevertDataSeal.Verify(obj.ToString()));
    }

    [Fact]
    public async Task RevertAsync_TamperedFile_NeverRunsItsSteps()
    {
        var id = Guid.NewGuid();
        var path = Path.Combine(Shared.RevertDirectory, id + ".json");
        var marker = Path.Combine(Path.GetTempPath(), $"sealtest-{id}.txt");
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Shared.RevertDirectory);

        try
        {
            var signed = RevertDataSeal.ToJson(Payload(id, "exit 0"));
            var tampered = signed.Replace("exit 0", $"echo pwned> {marker.Replace("\\", "\\\\")}");
            await File.WriteAllTextAsync(path, tampered, cancellationToken);

            Assert.Null(await RevertManager.GetRevertDataAsync(id));

            var manager = new RevertManager(
                NullLogger<RevertManager>.Instance,
                TestShell.New(),
                new PowerPlanService(NullLogger<PowerPlanService>.Instance),
                TimeProvider.System
            );
            var result = await manager.RevertAsync(
                new MockOptimization(id),
                cancellationToken: cancellationToken
            );

            Assert.False(result.Success);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Shared.RevertDirectory, id + ".json*"))
                File.Delete(file);
            if (File.Exists(marker))
                File.Delete(marker);
        }
    }

    [Fact]
    public async Task SaveRevertDataAsync_WritesASignedFile()
    {
        var id = Guid.NewGuid();
        var path = Path.Combine(Shared.RevertDirectory, id + ".json");
        var cancellationToken = TestContext.Current.CancellationToken;

        try
        {
            var changes = new optimizerDuck.Domain.Execution.ChangeSet();
            changes.Add("Registry", "wrote a value", true, new MockRevertStep());
            await new RevertManager(
                NullLogger<RevertManager>.Instance,
                TestShell.New(),
                new PowerPlanService(NullLogger<PowerPlanService>.Instance),
                TimeProvider.System
            ).SaveRevertDataAsync(changes, id, "SealTest", cancellationToken);

            var json = await File.ReadAllTextAsync(path, cancellationToken);
            Assert.True(RevertDataSeal.Verify(json));
            Assert.NotNull(await RevertManager.GetRevertDataAsync(id));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Shared.RevertDirectory, id + ".json*"))
                File.Delete(file);
        }
    }
}
