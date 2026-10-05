using System.IO;
using System.Management;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using optimizerDuck.Domain.Optimizations.Models.Services;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.System.Primitives;

namespace optimizerDuck.Services.System;

/// <summary>How a health check came out.</summary>
public enum HealthStatus
{
    Good,
    Info,
    Warning,
    Bad,
    Unknown,
}

/// <summary>One line of the health report.</summary>
/// <param name="Title">The localized name of what was checked.</param>
/// <param name="Status">How it came out.</param>
/// <param name="Detail">The localized finding.</param>
public sealed record HealthCheck(string Title, HealthStatus Status, string Detail);

/// <summary>
///     Reads the security and hardware state that matters for a healthy PC. Every check only
///     reads; a check that cannot be read reports <see cref="HealthStatus.Unknown" /> instead of
///     failing the report.
/// </summary>
public class HealthCheckService(ShellService shell, ILogger<HealthCheckService> logger)
{
    /// <summary>Runs every check. Slow parts (WMI, the event log) run off the caller's thread.</summary>
    public async Task<IReadOnlyList<HealthCheck>> RunAsync(
        CancellationToken cancellationToken = default
    )
    {
        var checks = await Task.Run(
                () =>
                {
                    var list = new List<HealthCheck>
                    {
                        Safe(CheckSecureBoot),
                        Safe(CheckTpm),
                        Safe(CheckVbs),
                        Safe(CheckMemoryIntegrity),
                        Safe(CheckDefender),
                    };
                    list.AddRange(SafeMany(CheckDisks));
                    list.Add(Safe(CheckTrim));
                    list.Add(Safe(CheckFreeSpace));
                    list.Add(Safe(CheckPendingRestart));
                    list.AddRange(SafeMany(CheckBattery));
                    return list;
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        checks.Add(await CheckBootTimeAsync(cancellationToken).ConfigureAwait(false));
        return checks;
    }

    /// <summary>Writes the checks as a self-contained HTML page.</summary>
    public static string ToHtml(IReadOnlyList<HealthCheck> checks, DateTime at)
    {
        var html = new StringBuilder();
        html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>")
            .Append(WebUtility.HtmlEncode(Loc.Instance["Health.Header.Title"]))
            .Append(
                "</title><style>body{font-family:Segoe UI,sans-serif;margin:32px;color:#1b1b1b}"
                    + "table{border-collapse:collapse;width:100%}td,th{padding:8px 12px;"
                    + "border-bottom:1px solid #ddd;text-align:left}.Good{color:#0f7b0f}"
                    + ".Warning{color:#9d5d00}.Bad{color:#c42b1c}.Info,.Unknown{color:#555}"
                    + "</style></head><body><h1>"
            )
            .Append(WebUtility.HtmlEncode(Loc.Instance["Health.Header.Title"]))
            .Append("</h1><p>")
            .Append(WebUtility.HtmlEncode($"{Environment.MachineName} · {at:G}"))
            .Append("</p><table>");

        foreach (var check in checks)
            html.Append("<tr><th>")
                .Append(WebUtility.HtmlEncode(check.Title))
                .Append("</th><td class=\"")
                .Append(check.Status)
                .Append("\">")
                .Append(WebUtility.HtmlEncode(Loc.Instance[$"Health.Status.{check.Status}"]))
                .Append("</td><td>")
                .Append(WebUtility.HtmlEncode(check.Detail))
                .Append("</td></tr>");

        return html.Append("</table></body></html>").ToString();
    }

    private HealthCheck Safe(Func<HealthCheck> check)
    {
        try
        {
            return check();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Health check {Check} failed", check.Method.Name);
            return new HealthCheck(
                Loc.Instance[$"Health.Check.{check.Method.Name[5..]}"],
                HealthStatus.Unknown,
                Loc.Instance["Health.Unknown"]
            );
        }
    }

    private IEnumerable<HealthCheck> SafeMany(Func<IEnumerable<HealthCheck>> checks)
    {
        try
        {
            return checks().ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Health check {Check} failed", checks.Method.Name);
            return [];
        }
    }

    private static HealthCheck CheckSecureBoot()
    {
        var title = Loc.Instance["Health.Check.SecureBoot"];
        if (
            !RegistryService.TryReadValue(
                new RegistryItem(
                    @"HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State",
                    "UEFISecureBootEnabled"
                ),
                out var value
            ) || value is null
        )
            return new HealthCheck(
                title,
                HealthStatus.Warning,
                Loc.Instance["Health.SecureBoot.Unsupported"]
            );

        return Convert.ToInt32(value) == 1
            ? new HealthCheck(title, HealthStatus.Good, Loc.Instance["Health.State.On"])
            : new HealthCheck(title, HealthStatus.Warning, Loc.Instance["Health.SecureBoot.Off"]);
    }

    private static HealthCheck CheckTpm()
    {
        var title = Loc.Instance["Health.Check.Tpm"];
        var tpm = WmiHelper.QueryFirst(
            "SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion FROM Win32_Tpm",
            mo =>
                (
                    Found: true,
                    Enabled: WmiHelper.GetBool(mo, "IsEnabled_InitialValue") == true,
                    Activated: WmiHelper.GetBool(mo, "IsActivated_InitialValue") == true,
                    Version: WmiHelper.GetString(mo, "SpecVersion")?.Split(',')[0].Trim() ?? "?"
                ),
            @"root\cimv2\Security\MicrosoftTpm"
        );

        if (!tpm.Found)
            return new HealthCheck(title, HealthStatus.Bad, Loc.Instance["Health.Tpm.None"]);

        return tpm.Enabled && tpm.Activated
            ? new HealthCheck(
                title,
                HealthStatus.Good,
                Loc.Instance["Health.Tpm.Ready", tpm.Version]
            )
            : new HealthCheck(
                title,
                HealthStatus.Warning,
                Loc.Instance["Health.Tpm.NotReady", tpm.Version]
            );
    }

    private static HealthCheck CheckVbs()
    {
        var title = Loc.Instance["Health.Check.Vbs"];
        var status = DeviceGuard<int?>(mo =>
            WmiHelper.GetInt(mo, "VirtualizationBasedSecurityStatus")
        );
        return status switch
        {
            2 => new HealthCheck(title, HealthStatus.Info, Loc.Instance["Health.Vbs.Running"]),
            1 => new HealthCheck(title, HealthStatus.Info, Loc.Instance["Health.Vbs.Enabled"]),
            0 => new HealthCheck(title, HealthStatus.Info, Loc.Instance["Health.State.Off"]),
            _ => new HealthCheck(title, HealthStatus.Unknown, Loc.Instance["Health.Unknown"]),
        };
    }

    private static HealthCheck CheckMemoryIntegrity()
    {
        var title = Loc.Instance["Health.Check.MemoryIntegrity"];
        // SecurityServicesRunning lists 2 when hypervisor-enforced code integrity runs.
        var running = DeviceGuard<bool?>(mo =>
            mo["SecurityServicesRunning"] is int[] services ? services.Contains(2)
            : mo["SecurityServicesRunning"] is uint[] unsigned ? unsigned.Contains(2u)
            : null
        );
        return running switch
        {
            true => new HealthCheck(title, HealthStatus.Info, Loc.Instance["Health.State.On"]),
            false => new HealthCheck(title, HealthStatus.Info, Loc.Instance["Health.State.Off"]),
            _ => new HealthCheck(title, HealthStatus.Unknown, Loc.Instance["Health.Unknown"]),
        };
    }

    private static T? DeviceGuard<T>(Func<ManagementObject, T> selector)
    {
        return WmiHelper.QueryFirst(
            "SELECT * FROM Win32_DeviceGuard",
            selector,
            @"root\Microsoft\Windows\DeviceGuard"
        );
    }

    private static HealthCheck CheckDefender()
    {
        var title = Loc.Instance["Health.Check.Defender"];
        var status = WmiHelper.QueryFirst(
            "SELECT RealTimeProtectionEnabled, AntivirusSignatureAge FROM MSFT_MpComputerStatus",
            mo =>
                (
                    Found: true,
                    RealTime: WmiHelper.GetBool(mo, "RealTimeProtectionEnabled"),
                    Age: WmiHelper.GetInt(mo, "AntivirusSignatureAge")
                ),
            @"root\Microsoft\Windows\Defender"
        );

        if (!status.Found || status.RealTime is null)
            return new HealthCheck(
                title,
                HealthStatus.Unknown,
                Loc.Instance["Health.Defender.Unknown"]
            );
        if (status.RealTime == false)
            return new HealthCheck(
                title,
                HealthStatus.Warning,
                Loc.Instance["Health.Defender.Off"]
            );
        if (status.Age is > 7)
            return new HealthCheck(
                title,
                HealthStatus.Warning,
                Loc.Instance["Health.Defender.Old", status.Age.Value]
            );

        return new HealthCheck(title, HealthStatus.Good, Loc.Instance["Health.Defender.On"]);
    }

    private static IEnumerable<HealthCheck> CheckDisks()
    {
        var disks =
            WmiHelper.Query(
                "SELECT FriendlyName, HealthStatus, MediaType FROM MSFT_PhysicalDisk",
                items =>
                    items
                        .Select(mo =>
                            (
                                Name: WmiHelper.GetString(mo, "FriendlyName") ?? "?",
                                Health: WmiHelper.GetInt(mo, "HealthStatus"),
                                Media: WmiHelper.GetInt(mo, "MediaType")
                            )
                        )
                        .ToList(),
                @"root\Microsoft\Windows\Storage"
            ) ?? [];

        foreach (var disk in disks)
        {
            var title = Loc.Instance["Health.Check.Disk", disk.Name];
            var media = disk.Media switch
            {
                3 => "HDD",
                4 => "SSD",
                5 => "SCM",
                _ => "?",
            };
            yield return disk.Health switch
            {
                0 => new HealthCheck(
                    title,
                    HealthStatus.Good,
                    Loc.Instance["Health.Disk.Healthy", media]
                ),
                1 => new HealthCheck(
                    title,
                    HealthStatus.Warning,
                    Loc.Instance["Health.Disk.Warning", media]
                ),
                2 => new HealthCheck(
                    title,
                    HealthStatus.Bad,
                    Loc.Instance["Health.Disk.Unhealthy", media]
                ),
                _ => new HealthCheck(title, HealthStatus.Unknown, Loc.Instance["Health.Unknown"]),
            };
        }
    }

    private static HealthCheck CheckTrim()
    {
        var title = Loc.Instance["Health.Check.Trim"];
        RegistryService.TryReadValue(
            new RegistryItem(
                @"HKLM\SYSTEM\CurrentControlSet\Control\FileSystem",
                "DisableDeleteNotification"
            ),
            out var value
        );
        // Absent means the Windows default, which is TRIM on.
        return value is not null && Convert.ToInt32(value) != 0
            ? new HealthCheck(title, HealthStatus.Warning, Loc.Instance["Health.Trim.Off"])
            : new HealthCheck(title, HealthStatus.Good, Loc.Instance["Health.State.On"]);
    }

    private static HealthCheck CheckFreeSpace()
    {
        var title = Loc.Instance["Health.Check.FreeSpace"];
        var drive = new DriveInfo(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:");
        var percent = drive.TotalSize == 0 ? 0 : drive.AvailableFreeSpace * 100 / drive.TotalSize;
        var detail = Loc.Instance[
            "Health.FreeSpace.Value",
            FormatBytes(drive.AvailableFreeSpace),
            FormatBytes(drive.TotalSize),
            percent
        ];
        var status = percent switch
        {
            < 5 => HealthStatus.Bad,
            < 10 => HealthStatus.Warning,
            _ => HealthStatus.Good,
        };
        return new HealthCheck(title, status, detail);
    }

    private static HealthCheck CheckPendingRestart()
    {
        var title = Loc.Instance["Health.Check.PendingRestart"];
        var pending =
            RegistryService.KeyExists(
                new RegistryItem(
                    @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"
                )
            )
            || RegistryService.KeyExists(
                new RegistryItem(
                    @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"
                )
            );
        return pending
            ? new HealthCheck(
                title,
                HealthStatus.Warning,
                Loc.Instance["Health.PendingRestart.Yes"]
            )
            : new HealthCheck(title, HealthStatus.Good, Loc.Instance["Health.PendingRestart.No"]);
    }

    private static IEnumerable<HealthCheck> CheckBattery()
    {
        var design = WmiHelper.QueryFirst(
            "SELECT DesignedCapacity FROM BatteryStaticData",
            mo => WmiHelper.GetLong(mo, "DesignedCapacity"),
            @"root\wmi"
        );
        var full = WmiHelper.QueryFirst(
            "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity",
            mo => WmiHelper.GetLong(mo, "FullChargedCapacity"),
            @"root\wmi"
        );
        if (design is not > 0 || full is not > 0)
            yield break;

        var percent = (int)Math.Min(100, full.Value * 100 / design.Value);
        yield return new HealthCheck(
            Loc.Instance["Health.Check.Battery"],
            percent switch
            {
                < 60 => HealthStatus.Bad,
                < 80 => HealthStatus.Warning,
                _ => HealthStatus.Good,
            },
            Loc.Instance["Health.Battery.Value", percent]
        );
    }

    private async Task<HealthCheck> CheckBootTimeAsync(CancellationToken cancellationToken)
    {
        var title = Loc.Instance["Health.Check.BootTime"];
        try
        {
            // Event 100 of the boot performance log carries the last boot's duration in ms.
            var result = await shell
                .QueryPowerShellAsync(
                    "$e = Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-Diagnostics-Performance/Operational';Id=100} -MaxEvents 1 -ErrorAction Stop; "
                        + "(([xml]$e.ToXml()).Event.EventData.Data | Where-Object Name -eq 'BootTime').'#text'",
                    logger,
                    ct: cancellationToken
                )
                .ConfigureAwait(false);
            if (long.TryParse(result.Stdout.Trim(), out var ms) && ms > 0)
                return new HealthCheck(
                    title,
                    HealthStatus.Info,
                    Loc.Instance["Health.BootTime.Value", Math.Round(ms / 1000.0, 1)]
                );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the last boot duration");
        }

        return new HealthCheck(
            title,
            HealthStatus.Unknown,
            Loc.Instance["Health.BootTime.Unknown"]
        );
    }

    private static string FormatBytes(long bytes)
    {
        return bytes >= 1L << 40 ? $"{bytes / (double)(1L << 40):F1} TB"
            : bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GB"
            : $"{bytes / (double)(1L << 20):F0} MB";
    }
}
