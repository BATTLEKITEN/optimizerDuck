using Wpf.Ui.Appearance;

namespace optimizerDuck.Domain.Configuration;

/// <summary>
///     Application settings that can be persisted to disk.
/// </summary>
public sealed class AppSettings
{
    public AppOptions App { get; set; } = new();

    public OptimizeOptions Optimize { get; set; } = new();

    public BloatwareOptions Bloatware { get; set; } = new();

    public sealed class AppOptions
    {
        /// <summary>
        ///     The UI language code (e.g., "en-US").
        /// </summary>
        public string Language { get; set; } = "en-US";

        /// <summary>
        ///     The UI theme (e.g., "Dark").
        /// </summary>
        public ApplicationTheme Theme { get; set; } = ApplicationTheme.Dark;

        public bool LegalAccepted { get; set; } = false;
    }

    public sealed class OptimizeOptions
    {
        public const int MinShellTimeoutMs = 1_000;
        public const int MaxShellTimeoutMs = 3_600_000;

        public int ShellTimeoutMs { get; set; } = 120000;

        /// <summary>
        ///     The timeout a shell call actually uses: the settings file is writable without
        ///     elevation, so a zero, negative or absurd value is pulled back into range.
        /// </summary>
        public static int ClampShellTimeout(int value) =>
            Math.Clamp(value, MinShellTimeoutMs, MaxShellTimeoutMs);

        /// <summary>
        ///     Whether to show the success snackbar after applying an optimization.
        /// </summary>
        public bool ShowCompletionNotification { get; set; } = false;

        /// <summary>
        ///     Whether smooth scrolling is enabled globally.
        /// </summary>
        public bool SmoothScrolling { get; set; } = false;
    }

    public sealed class BloatwareOptions
    {
        /// <summary>
        ///     Whether to remove provisioned AppX packages.
        /// </summary>
        public bool RemoveProvisioned { get; set; } = true;
    }
}
