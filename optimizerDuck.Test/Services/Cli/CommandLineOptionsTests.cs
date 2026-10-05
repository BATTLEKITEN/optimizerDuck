using optimizerDuck.Services.Cli;

namespace optimizerDuck.Test.Services.Cli;

public class CommandLineOptionsTests
{
    [Fact]
    public void Parse_NoArguments_IsNotACommand()
    {
        var options = CommandLineOptions.Parse([]);

        Assert.False(options.IsCommand);
        Assert.Equal(CliCommand.None, options.Command);
    }

    [Fact]
    public void Parse_ApplyProfileWithOptions_ReadsEverything()
    {
        var options = CommandLineOptions.Parse([
            "--apply-profile",
            @"C:\p.duckprofile",
            "--restore-point",
            "--report",
            "out.json",
        ]);

        Assert.Null(options.Error);
        Assert.Equal(CliCommand.ApplyProfile, options.Command);
        Assert.Equal(@"C:\p.duckprofile", options.Argument);
        Assert.True(options.RestorePoint);
        Assert.Equal("out.json", options.ReportPath);
    }

    [Theory]
    [InlineData("--apply-preset", "Gaming", CliCommand.ApplyPreset)]
    [InlineData("--export-profile", "x.duckprofile", CliCommand.ExportProfile)]
    public void Parse_CommandWithValue_ReadsTheValue(string flag, string value, CliCommand command)
    {
        var options = CommandLineOptions.Parse([flag, value]);

        Assert.Equal(command, options.Command);
        Assert.Equal(value, options.Argument);
    }

    [Theory]
    [InlineData("--check-drift", CliCommand.CheckDrift)]
    [InlineData("--reapply-drift", CliCommand.ReapplyDrift)]
    [InlineData("--help", CliCommand.Help)]
    [InlineData("/?", CliCommand.Help)]
    public void Parse_Flag_SelectsTheCommand(string flag, CliCommand command)
    {
        Assert.Equal(command, CommandLineOptions.Parse([flag]).Command);
    }

    [Fact]
    public void Parse_MissingValue_IsAUsageError()
    {
        var options = CommandLineOptions.Parse(["--apply-profile", "--restore-point"]);

        Assert.Equal("Cli.Error.MissingValue", options.Error);
        Assert.True(options.IsCommand);
    }

    [Fact]
    public void Parse_ReportWithoutPath_IsAUsageError()
    {
        var options = CommandLineOptions.Parse(["--check-drift", "--report"]);

        Assert.Equal("Cli.Error.MissingValue", options.Error);
        Assert.Equal("--report", options.ErrorArg);
    }

    [Fact]
    public void Parse_TwoCommands_IsAUsageError()
    {
        Assert.Equal(
            "Cli.Error.OneCommand",
            CommandLineOptions.Parse(["--check-drift", "--reapply-drift"]).Error
        );
    }

    [Fact]
    public void Parse_UnknownArgument_IsAUsageError()
    {
        var options = CommandLineOptions.Parse(["--frobnicate"]);

        Assert.Equal("Cli.Error.UnknownArgument", options.Error);
        Assert.Equal("--frobnicate", options.ErrorArg);
    }

    [Fact]
    public void Parse_OptionWithoutCommand_IsAUsageError()
    {
        Assert.Equal(
            "Cli.Error.OptionWithoutCommand",
            CommandLineOptions.Parse(["--restore-point"]).Error
        );
    }
}
