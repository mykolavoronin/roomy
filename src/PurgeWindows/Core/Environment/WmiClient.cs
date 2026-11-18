using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PurgeWindows.Core.Environment;

/// <summary>
/// A tiny late-bound COM helper for the scriptable WMI/WSC APIs. No <c>dynamic</c> and no
/// System.Management: reflection <c>InvokeMember</c> keeps the app dependency-free, and every
/// COM object is released. All calls are read-only queries.
/// </summary>
internal static class ComHelper
{
    private const BindingFlags Get = BindingFlags.GetProperty | BindingFlags.InvokeMethod;

    public static object? Get(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, Get, null, target, args, CultureInfo.InvariantCulture);

    public static object? Call(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);

    public static void Release(object? target)
    {
        if (target is null) return;
        try { Marshal.FinalReleaseComObject(target); }
        catch { /* A failed release must never surface. */ }
    }
}

/// <summary>Conversions from WMI property values (which arrive as boxed COM values) to CLR types.</summary>
public static class WmiValue
{
    public static string? AsString(object? value) => value switch
    {
        null => null,
        string s => s,
        _ => value.ToString(),
    };

    public static bool? AsBool(object? value) => value switch
    {
        null => null,
        bool b => b,
        _ => bool.TryParse(value.ToString(), out var parsed) ? parsed : null,
    };

    public static int? AsInt(object? value) => value switch
    {
        null => null,
        int i => i,
        uint u => (int)u,
        short s => s,
        ushort us => us,
        long l => (int)l,
        ulong ul => (int)ul,
        byte b => b,
        _ => int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
    };

    public static long? AsLong(object? value) => value switch
    {
        null => null,
        long l => l,
        ulong ul => (long)ul,
        int i => i,
        uint u => u,
        _ => long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
    };

    public static uint? AsUInt(object? value) => value switch
    {
        null => null,
        uint u => u,
        int i when i >= 0 => (uint)i,
        long l when l >= 0 => (uint)l,
        ulong ul => (uint)ul,
        ushort us => us,
        _ => uint.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
    };

    /// <summary>Array-typed WMI properties (e.g. ChassisTypes) arrive as object arrays.</summary>
    public static IReadOnlyList<int> AsIntList(object? value)
    {
        if (value is not Array array) return [];
        var result = new List<int>();
        foreach (var item in array)
        {
            var parsed = AsInt(item);
            if (parsed is { } n) result.Add(n);
        }
        return result;
    }
}

/// <summary>
/// WMI queries over the late-bound <c>WbemScripting.SWbemLocator</c> COM API: always on a worker
/// thread (callers run probes off the UI thread), with up to 2 retries and a 5-minute cache of
/// successful results only. Failures are never cached — a later re-check retries for real.
/// Queries are read-only WQL SELECT statements; nothing here writes.
/// </summary>
public sealed class WmiClient
{
    public delegate WmiQueryResult Executor(string namespaceName, string query, IReadOnlyList<string> properties);

    private readonly Executor _executor;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (DateTimeOffset At, WmiQueryResult Result)> _cache = new();

    public const int MaxRetries = 2;
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public WmiClient() : this(ExecuteViaCom, () => DateTimeOffset.UtcNow) { }

    internal WmiClient(Executor executor, Func<DateTimeOffset> clock)
    {
        _executor = executor;
        _clock = clock;
    }

    /// <summary>
    /// Runs one WMI query: cached success first, then the executor with up to 2 retries.
    /// The cache key is namespace + query + property list. Failed results are not cached.
    /// </summary>
    public WmiQueryResult Query(string namespaceName, string query, IReadOnlyList<string> properties)
    {
        var key = namespaceName + "|" + query + "|" + string.Join(",", properties);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var cached) && _clock() - cached.At < CacheTtl)
                return cached.Result;
        }

        WmiQueryResult? last = null;
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                last = _executor(namespaceName, query, properties);
            }
            catch
            {
                last = WmiQueryResult.Failed();
            }
            if (last.Succeeded)
            {
                lock (_cacheLock) { _cache[key] = (_clock(), last); }
                return last;
            }
        }
        return last ?? WmiQueryResult.Failed();
    }

    public void ClearCache()
    {
        lock (_cacheLock) { _cache.Clear(); }
    }

    private static WmiQueryResult ExecuteViaCom(string namespaceName, string query, IReadOnlyList<string> properties)
    {
        object? locator = null;
        object? services = null;
        object? resultSet = null;
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType is null) return WmiQueryResult.Failed();
            locator = Activator.CreateInstance(locatorType);
            if (locator is null) return WmiQueryResult.Failed();
            // ConnectServer's last six parameters are optional; Type.Missing selects the
            // defaults (current user, default locale/authority/flags), as in scripting.
            services = ComHelper.Call(locator, "ConnectServer", ".", namespaceName,
                Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing);
            if (services is null) return WmiQueryResult.Failed();
            // Flags 0: synchronous query, complete result set (no forward-only enumerator),
            // so indexed access below works. The runner's timeout bounds the wait.
            resultSet = ComHelper.Call(services, "ExecQuery", query, 0, Type.Missing);
            if (resultSet is null) return WmiQueryResult.Failed();

            // A late-bound SWbemObjectSet does not expose IEnumerable, so rows are read by
            // index through the documented Count / ItemIndex members.
            var countValue = ComHelper.Get(resultSet, "Count");
            var count = countValue is int c ? c : 0;
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            for (var i = 0; i < count; i++)
            {
                object? item = null;
                try
                {
                    item = ComHelper.Call(resultSet, "ItemIndex", i);
                    if (item is null) continue;
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    object? propertiesCollection = null;
                    try
                    {
                        propertiesCollection = ComHelper.Get(item, "Properties_");
                        foreach (var property in properties)
                        {
                            object? value = null;
                            object? propertyObject = null;
                            try
                            {
                                propertyObject = propertiesCollection is null
                                    ? null
                                    : ComHelper.Call(propertiesCollection, "Item", property);
                                value = propertyObject is null ? null : ComHelper.Get(propertyObject, "Value");
                            }
                            catch { /* A missing property reads as null, not a failed query. */ }
                            finally { ComHelper.Release(propertyObject); }
                            row[property] = value;
                        }
                    }
                    finally { ComHelper.Release(propertiesCollection); }
                    rows.Add(row);
                }
                finally
                {
                    ComHelper.Release(item);
                }
            }
            return WmiQueryResult.Ok(rows);
        }
        catch
        {
            return WmiQueryResult.Failed();
        }
        finally
        {
            ComHelper.Release(resultSet);
            ComHelper.Release(services);
            ComHelper.Release(locator);
        }
    }
}
