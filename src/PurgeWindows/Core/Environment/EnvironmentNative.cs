using System.Runtime.InteropServices;

namespace PurgeWindows.Core.Environment;

/// <summary>
/// The only P/Invoke surface for environment detection: read-only Service Control Manager
/// queries and power status. Nothing here starts, stops, or reconfigures a service, and
/// nothing here writes. (New P/Invoke goes here, never in Native.cs.)
/// </summary>
internal static class EnvironmentNative
{
    // Service Control Manager access: enumerate services, query a service's status and config.
    // SC_MANAGER_ENUMERATE_SERVICE (0x4) + SC_MANAGER_CONNECT (0x1) = 0x5.
    // SERVICE_QUERY_STATUS (0x4) + SERVICE_QUERY_CONFIG (0x1) = 0x5.
    private const uint ScManagerAccess = 0x5;
    private const uint ServiceQueryAccess = 0x5;
    private const int ErrorServiceDoesNotExist = 1060;

    // SERVICE_STATUS_PROCESS.dwCurrentState: SERVICE_RUNNING = 4.
    private const int ServiceRunning = 4;
    // QUERY_SERVICE_CONFIG.dwStartType: SERVICE_DISABLED = 4.
    private const uint ServiceStartDisabled = 4;
    // SC_STATUS_PROCESS_INFO = 0.
    private const int ScStatusProcessInfo = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public int dwServiceType;
        public int dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
        public int dwProcessId;
        public int dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct QUERY_SERVICE_CONFIG
    {
        public int dwServiceType;
        public uint dwStartType;
        public int dwErrorControl;
        public string? lpBinaryPathName;
        public string? lpLoadOrderGroup;
        public int dwTagId;
        public string? lpDependencies;
        public string? lpServiceStartName;
        public string? lpDisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, IntPtr buffer, int bufferSize, out int bytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(IntPtr service, IntPtr buffer, int bufferSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    /// <summary>
    /// Reads one service's state without changing anything. Returns NotInstalled when the
    /// service does not exist, Disabled only when its config positively says so, Running when
    /// it is running, Stopped when it exists but is not running and is not disabled, and
    /// Unknown when the query itself failed. A stopped service whose config is unreadable is
    /// Unknown, never guessed Disabled.
    /// </summary>
    public static ServiceState QueryServiceState(string serviceName)
    {
        IntPtr scm = IntPtr.Zero;
        IntPtr svc = IntPtr.Zero;
        try
        {
            scm = OpenSCManager(null, null, ScManagerAccess);
            if (scm == IntPtr.Zero) return ServiceState.Unknown;
            svc = OpenService(scm, serviceName, ServiceQueryAccess);
            if (svc == IntPtr.Zero)
                return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist
                    ? ServiceState.NotInstalled
                    : ServiceState.Unknown;

            var size = Marshal.SizeOf<SERVICE_STATUS_PROCESS>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!QueryServiceStatusEx(svc, ScStatusProcessInfo, buffer, size, out _))
                    return ServiceState.Unknown;
                var status = Marshal.PtrToStructure<SERVICE_STATUS_PROCESS>(buffer);
                if (status.dwCurrentState == ServiceRunning) return ServiceState.Running;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            // Not running: disabled or merely stopped? Only the config can say. The sizing
            // call is expected to fail with ERROR_INSUFFICIENT_BUFFER while reporting the
            // required size, so its return value is meaningless — only `needed` matters.
            QueryServiceConfig(svc, IntPtr.Zero, 0, out var needed);
            if (needed <= 0)
                return ServiceState.Unknown;
            var configBuffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!QueryServiceConfig(svc, configBuffer, needed, out _))
                    return ServiceState.Unknown;
                var config = Marshal.PtrToStructure<QUERY_SERVICE_CONFIG>(configBuffer);
                return config.dwStartType == ServiceStartDisabled ? ServiceState.Disabled : ServiceState.Stopped;
            }
            finally
            {
                Marshal.FreeHGlobal(configBuffer);
            }
        }
        catch
        {
            return ServiceState.Unknown;
        }
        finally
        {
            if (svc != IntPtr.Zero) CloseServiceHandle(svc);
            if (scm != IntPtr.Zero) CloseServiceHandle(scm);
        }
    }

    /// <summary>
    /// Reads AC/battery state. BatteryPresent is false only when Windows positively reports no
    /// battery; an unreadable status is Unknown on both fields.
    /// </summary>
    public static PowerStatus GetPowerStatus()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status)) return PowerStatus.Unknown();
            // ACLineStatus: 0 = offline, 1 = online, 255 = unknown.
            // BatteryFlag bit 7 (0x80) = no system battery.
            bool? onBattery = status.ACLineStatus switch
            {
                0 => true,
                1 => false,
                _ => null,
            };
            bool? batteryPresent = (status.BatteryFlag & 0x80) != 0 ? false
                : status.BatteryFlag == 255 ? null
                : true;
            return new PowerStatus(onBattery, batteryPresent);
        }
        catch
        {
            return PowerStatus.Unknown();
        }
    }
}
