using System.Text;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Services.Configuration;

namespace optimizerDuck.Domain.UI;

/// <summary>Words the steps of a preview as plain text, one line per step with its facts.</summary>
public static class ChangePreviewFormatter
{
    /// <summary>The preview as text: a summary line, then every step.</summary>
    public static string Format(IEnumerable<Change> steps)
    {
        var ordered = steps.OrderBy(s => s.Index).ToList();
        var planned = ordered.Count(s => s.Ok && s.Kind == ChangeKind.Change);
        var text = new StringBuilder();

        text.AppendLine(
            planned == 0
                ? Loc.Instance["Optimizer.Preview.Nothing"]
                : Loc.Instance[
                    "Optimizer.Preview.Summary",
                    planned,
                    ordered.Count(s => s.Kind == ChangeKind.Skip)
                ]
        );

        foreach (var step in ordered)
        {
            text.AppendLine();
            text.Append(Marker(step)).Append(' ').AppendLine(step.Description);

            var row = ChangeDetailPresentation.For(
                step.Detail,
                step.Kind,
                ChangeRecordOperation.Apply
            );
            foreach (var chip in row.Chips)
                text.Append("    ")
                    .Append(Loc.Instance[chip.LabelKey])
                    .Append(": ")
                    .AppendLine(chip.ValueIsKey ? Loc.Instance[chip.Value] : chip.Value);

            if (!step.Ok && step.Error is not null)
                text.Append("    ").AppendLine(step.Error);
        }

        return text.ToString().TrimEnd();
    }

    private static string Marker(Change step) =>
        !step.Ok
            ? "✗"
            : step.Kind switch
            {
                ChangeKind.Change => "→",
                ChangeKind.Skip => "✓",
                _ => "–",
            };
}
