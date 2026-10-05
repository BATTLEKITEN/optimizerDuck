using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Models.Services;
using optimizerDuck.Services.System.Primitives;
using optimizerDuck.Test.TestDoubles;

namespace optimizerDuck.Test.Services.OptimizationServices;

public class DryRunTests : IDisposable
{
    private const string KeyNative = @"Software\TestOptimizerDuckDryRun";
    private const string Key = @"HKCU\" + KeyNative;

    public DryRunTests() => Cleanup();

    public void Dispose() => Cleanup();

    private static OpCall DryCall() =>
        new()
        {
            Changes = new ChangeSet(),
            Logger = NullLogger.Instance,
            DryRun = true,
        };

    private static void Cleanup() =>
        Registry.CurrentUser.DeleteSubKeyTree(KeyNative, throwOnMissingSubKey: false);

    [Fact]
    public void Write_DryRun_RecordsThePlanWithoutCreatingTheKey()
    {
        var call = DryCall();

        var result = RegistryService.Write(call, new RegistryItem($@"{Key}\Sub", "Value", 1));

        Assert.True(result.Ok);
        var change = Assert.Single(call.Changes.Changes);
        Assert.Equal(ChangeKind.Change, change.Kind);
        Assert.Null(change.Revert);
        using var key = Registry.CurrentUser.OpenSubKey(KeyNative);
        Assert.Null(key);
    }

    [Fact]
    public void Write_DryRun_ValueAlreadySet_RecordsASkip()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(KeyNative))
            key.SetValue("Value", 1, RegistryValueKind.DWord);
        var call = DryCall();

        RegistryService.Write(call, new RegistryItem(Key, "Value", 1));

        Assert.Equal(ChangeKind.Skip, Assert.Single(call.Changes.Changes).Kind);
    }

    [Fact]
    public void Write_DryRun_DifferentValue_LeavesTheValueAlone()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(KeyNative))
            key.SetValue("Value", 0, RegistryValueKind.DWord);
        var call = DryCall();

        RegistryService.Write(call, new RegistryItem(Key, "Value", 1));

        Assert.Equal(ChangeKind.Change, Assert.Single(call.Changes.Changes).Kind);
        using var read = Registry.CurrentUser.OpenSubKey(KeyNative)!;
        Assert.Equal(0, read.GetValue("Value"));
    }

    [Fact]
    public void DeleteValue_DryRun_KeepsTheValue()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(KeyNative))
            key.SetValue("Value", 1, RegistryValueKind.DWord);
        var call = DryCall();

        RegistryService.DeleteValue(call, new RegistryItem(Key, "Value"));

        Assert.Equal(ChangeKind.Change, Assert.Single(call.Changes.Changes).Kind);
        using var read = Registry.CurrentUser.OpenSubKey(KeyNative)!;
        Assert.Equal(1, read.GetValue("Value"));
    }

    [Fact]
    public void DeleteSubKeyTree_DryRun_KeepsTheKey()
    {
        Registry.CurrentUser.CreateSubKey($@"{KeyNative}\Child")!.Dispose();
        var call = DryCall();

        RegistryService.DeleteSubKeyTree(call, new RegistryItem(Key));

        Assert.Equal(ChangeKind.Change, Assert.Single(call.Changes.Changes).Kind);
        using var read = Registry.CurrentUser.OpenSubKey($@"{KeyNative}\Child");
        Assert.NotNull(read);
    }

    [Fact]
    public async Task Shell_DryRun_DoesNotRunTheCommand()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"optimizerDuck_dry_{Guid.NewGuid():N}");
        var call = DryCall();

        var result = await TestShell
            .New()
            .CMDAsync($"echo x> \"{marker}\"", call, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(ChangeKind.Change, Assert.Single(call.Changes.Changes).Kind);
        Assert.False(File.Exists(marker));
    }
}
