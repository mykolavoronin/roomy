using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;
using Roomy.Localization;

namespace Roomy.Core;

/// <summary>What happened when Roomy asked Windows to change the hibernation setting.</summary>
public enum HibernationResult
{
    /// <summary>powercfg ran and reported success.</summary>
    Applied,
    /// <summary>The user said no to the admin prompt. Nothing changed.</summary>
    Cancelled,
    /// <summary>powercfg failed or could not run. See the error text.</summary>
    Failed,
}

/// <summary>
/// Windows settings Roomy can read and change safely. Everything here goes through
/// Windows' own mechanisms and is reversible: hibernation via powercfg, and the
/// Delivery Optimization cache via the same guarded delete a cleanup uses.
/// </summary>
public static class WindowsFeatures
{
    /// <summary>The hibernation file. Never deleted directly — only powercfg may remove it.</summary>
    public static string HiberfilPath => Path.Combine(Env.SystemRoot, "hiberfil.sys");

    /// <summary>The Delivery Optimization download cache. Contents only, never the folder.</summary>
    public static string DeliveryOptimizationCache => Env.W("ServiceProfiles", "NetworkService", "AppData", "Local",
        "Microsoft", "Windows", "DeliveryOptimization", "Cache");

    /// <summary>powercfg's own argument for the wanted state.</summary>
    public static string PowerCfgArgs(bool enable) => enable ? "/h on" : "/h off";

    /// <summary>
    /// Whether hibernation is currently enabled, from the Power key powercfg itself uses.
    /// Null when the setting can't be read on this PC.
    /// </summary>
    public static bool? HibernationEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
            return key?.GetValue("HibernateEnabled") switch
            {
                int i => i != 0,
                _ => null,
            };
        }
        catch
        {
            // Any failure to read the key means "unknown", never a crash.
            return null;
        }
    }

    /// <summary>How big hiberfil.sys currently is, or 0 when it isn't there or can't be read.</summary>
    public static long HiberfilSize()
    {
        try { return new FileInfo(HiberfilPath).Length; }
        catch { return 0; }
    }

    /// <summary>
    /// Turns hibernation on or off through powercfg. When Roomy isn't running as admin,
    /// Windows asks for approval first; saying no leaves everything unchanged.
    /// </summary>
    public static async Task<(HibernationResult Result, string? Error)> SetHibernationAsync(bool enable)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powercfg.exe",
            Arguments = PowerCfgArgs(enable),
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (!Admin.IsElevated) psi.Verb = "runas";
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (HibernationResult.Failed, S.HibernateUnknown);
            await p.WaitForExitAsync();
            return p.ExitCode == 0
                ? (HibernationResult.Applied, null)
                : (HibernationResult.Failed, S.HibernateFailed + $"powercfg exited with code {p.ExitCode}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = the user cancelled the UAC prompt.
            return (HibernationResult.Cancelled, null);
        }
        catch (Exception ex)
        {
            return (HibernationResult.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Clears the Delivery Optimization cache (contents only, with the same guards as a
    /// cleanup). Needs admin: without it nothing is deleted and nothing is freed.
    /// </summary>
    public static FileOps.DeleteResult ClearDeliveryOptimizationCache(CancellationToken ct = default) =>
        FileOps.Delete(DeliveryOptimizationCache, null, ct);
}
