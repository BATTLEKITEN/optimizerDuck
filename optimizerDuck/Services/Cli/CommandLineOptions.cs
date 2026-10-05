namespace optimizerDuck.Services.Cli;

/// <summary>What a command line run does.</summary>
public enum CliCommand
{
    /// <summary>No command: start the window as usual.</summary>
    None,
    Help,
    ApplyProfile,
    ApplyPreset,
    ExportProfile,
    CheckDrift,
    ReapplyDrift,
}

/// <summary>
///     The parsed command line. Exactly one command runs, without the window; any argument this
///     build does not know is a usage error rather than something silently ignored.
/// </summary>
public sealed record CommandLineOptions
{
    public CliCommand Command { get; init; } = CliCommand.None;

    /// <summary>The profile file, or the preset key, the command acts on.</summary>
    public string? Argument { get; init; }

    /// <summary>Where to write the JSON report, if anywhere.</summary>
    public string? ReportPath { get; init; }

    /// <summary>Whether to create a restore point before changing anything.</summary>
    public bool RestorePoint { get; init; }

    /// <summary>Whether the user accepts the terms on the command line.</summary>
    public bool AcceptTerms { get; init; }

    /// <summary>The resource key of the usage error, when the arguments cannot be understood.</summary>
    public string? Error { get; init; }

    /// <summary>The argument the usage error names, if any.</summary>
    public string? ErrorArg { get; init; }

    public bool IsCommand => Command != CliCommand.None || Error is not null;

    /// <summary>Parses the arguments the app was started with.</summary>
    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        var options = new CommandLineOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--help":
                case "-h":
                case "/?":
                    options = WithCommand(options, CliCommand.Help, null);
                    break;
                case "--apply-profile":
                    options = WithCommand(options, CliCommand.ApplyProfile, Value(args, ref i));
                    break;
                case "--apply-preset":
                    options = WithCommand(options, CliCommand.ApplyPreset, Value(args, ref i));
                    break;
                case "--export-profile":
                    options = WithCommand(options, CliCommand.ExportProfile, Value(args, ref i));
                    break;
                case "--check-drift":
                    options = WithCommand(options, CliCommand.CheckDrift, null);
                    break;
                case "--reapply-drift":
                    options = WithCommand(options, CliCommand.ReapplyDrift, null);
                    break;
                case "--report":
                    options = options with { ReportPath = Value(args, ref i) };
                    break;
                case "--restore-point":
                    options = options with { RestorePoint = true };
                    break;
                case "--accept-terms":
                    options = options with { AcceptTerms = true };
                    break;
                default:
                    return options with { Error = "Cli.Error.UnknownArgument", ErrorArg = arg };
            }

            if (options.Error is not null)
                return options;
        }

        if (options.Command == CliCommand.None && options.Error is null)
        {
            if (options.ReportPath is not null || options.RestorePoint || options.AcceptTerms)
                return options with { Error = "Cli.Error.OptionWithoutCommand" };
        }

        return options;

        static string? Value(IReadOnlyList<string> args, ref int i)
        {
            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return null;
            return args[++i];
        }

        static CommandLineOptions WithCommand(
            CommandLineOptions current,
            CliCommand command,
            string? value
        )
        {
            if (current.Command != CliCommand.None)
                return current with { Error = "Cli.Error.OneCommand" };

            var needsValue =
                command
                is CliCommand.ApplyProfile
                    or CliCommand.ApplyPreset
                    or CliCommand.ExportProfile;
            if (needsValue && string.IsNullOrWhiteSpace(value))
                return current with
                {
                    Error = "Cli.Error.MissingValue",
                    ErrorArg = command switch
                    {
                        CliCommand.ApplyProfile => "--apply-profile",
                        CliCommand.ApplyPreset => "--apply-preset",
                        _ => "--export-profile",
                    },
                };

            return current with
            {
                Command = command,
                Argument = value,
            };
        }
    }
}
