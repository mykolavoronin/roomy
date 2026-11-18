using System.Diagnostics;
using PurgeWindows.Localization;

namespace PurgeWindows.Core;

public sealed class ScanContext
{
    public bool IsAdmin { get; init; }
    public CancellationToken Token { get; init; }

    /// <summary>When set, every folder is reported just before it is measured, so the scan
    /// view can show what is being looked at right now. Best-effort; never affects the scan.</summary>
    public IProgress<string>? PathProgress { get; init; }

    /// <summary>
    /// Downloads, streamed: files are yielded as they are found, so a huge Downloads
    /// folder never materializes into one big list (P1-7). Each caller enumerates
    /// fresh; enumeration stops early when the token cancels.
    /// </summary>
    public IEnumerable<FileInfo> EnumerateDownloads()
    {
        var found = TryGetDownloads();
        if (found is null) yield break;
        foreach (var f in Stream(found)) yield return f;
    }

    static IEnumerable<FileInfo>? TryGetDownloads()
    {
        try
        {
            var d = new DirectoryInfo(Env.Downloads);
            if (!d.Exists) return null;
            return d.EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            });
        }
        catch { return null; }
    }

    /// <summary>Streams an enumeration to the end, ending quietly if the walk breaks partway.</summary>
    IEnumerable<FileInfo> Stream(IEnumerable<FileInfo> found)
    {
        using var e = found.GetEnumerator();
        while (true)
        {
            bool moved = false, broken = false;
            try { moved = e.MoveNext(); }
            catch { broken = true; }
            if (broken || !moved) yield break;
            Token.ThrowIfCancellationRequested();
            yield return e.Current;
        }
    }
}

public sealed record ScanStep(string Id, string Title, Func<ScanContext, CleanupCategory?> Run);

/// <summary>Each step looks in a few well-known places and reports what it found. Nothing here deletes.</summary>
public static class Scanner
{
    static readonly TimeSpan OneDay = TimeSpan.FromDays(1);
    static readonly TimeSpan ThirtyDays = TimeSpan.FromDays(30);

    static readonly HashSet<string> InstallerExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle",
    };

    public static readonly IReadOnlyList<ScanStep> Steps =
    [
        new("recycle", S.CatRecycle, RecycleBin),
        new("temp", S.CatTemp, Temp),
        new("browser", S.CatBrowser, Browsers),
        new("apps", S.CatApps, Apps),
        new("outlook", S.CatOutlook, Outlook),
        new("messaging", S.CatMessaging, Messaging),
        new("devcaches", S.CatDevCaches, DevCaches),
        new("shaders", S.StepShaders, Shaders),
        new("avd", S.CatAvd, AvdCaches),
        new("crash", S.CatCrash, Crashes),
        new("thumbnails", S.CatThumbnails, Thumbnails),
        new("windows", S.StepWindows, WindowsLeftovers),
        new("diagnostics", S.CatDiagnostics, Diagnostics),
        new("winlogs", S.CatWinLogs, WindowsLogs),
        new("installers", S.CatInstallers, Installers),
        new("downloads", S.StepDownloads, BigDownloads),
        new("captures", S.CatCaptures, Captures),
        new("games", S.StepGames, Games),
        new("virtual", S.CatVirtual, VirtualMachines),
        new("winold", S.StepWinOld, WindowsOld),
    ];

    // ---------- helpers ----------

    static CleanupItem? Item(string name, TimeSpan? minAge, ScanContext c, IEnumerable<string> paths,
        bool toolResolved = false)
    {
        var existing = paths.Where(FileOps.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (existing.Count == 0) return null;
        long size = 0;
        int files = 0;
        foreach (var p in existing)
        {
            TellPath(c.PathProgress, p);
            var (b, f) = FileOps.Measure(p, minAge, c.Token, c.PathProgress);
            size += b;
            files += f;
        }
        if (size <= 0) return null;
        return new CleanupItem
        {
            Name = name, Paths = existing, Size = size, FileCount = files, MinAge = minAge,
            Detail = $"{files:N0} {Loc.Files(files)}", ToolResolved = toolResolved,
        };
    }

    /// <summary>Like <see cref="Item"/>, but for sizes Purge for Windows only reports: the detail tells the user where to act.</summary>
    static CleanupItem? Report(string name, string detail, ScanContext c, IEnumerable<string> paths)
    {
        var existing = paths.Where(FileOps.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (existing.Count == 0) return null;
        long size = 0;
        foreach (var p in existing)
        {
            TellPath(c.PathProgress, p);
            size += FileOps.Measure(p, null, c.Token, c.PathProgress).Bytes;
        }
        if (size <= 0) return null;
        return new CleanupItem { Name = name, Paths = existing, Size = size, Detail = detail };
    }

    static void Add(CleanupCategory c, CleanupItem? i)
    {
        if (i != null) c.Items.Add(i);
    }

    /// <summary>Reports the folder being scanned without ever letting a listener break the scan.</summary>
    static void TellPath(IProgress<string>? progress, string path)
    {
        try { progress?.Report(path); }
        catch { }
    }

    static CleanupCategory Sorted(CleanupCategory c)
    {
        c.Items.Sort((a, b) => b.Size.CompareTo(a.Size));
        return c;
    }

    static string[] Subdirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : []; }
        catch { return []; }
    }

    static IEnumerable<string> FilesIn(string dir, params string[] patterns)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var pattern in patterns)
        {
            IEnumerable<string> found;
            try { found = Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).ToList(); }
            catch { continue; }
            foreach (var f in found) yield return f;
        }
    }

    static List<string> Running(params (string Proc, string Name)[] apps)
    {
        var list = new List<string>();
        foreach (var (proc, name) in apps)
        {
            try
            {
                var ps = Process.GetProcessesByName(proc);
                if (ps.Length > 0 && !list.Contains(name)) list.Add(name);
                foreach (var p in ps) p.Dispose();
            }
            catch { }
        }
        return list;
    }

    static string? RunningNote(List<string> names)
    {
        if (names.Count == 0) return null;
        var who = names.Count == 1
            ? names[0]
            : string.Join(", ", names.Take(names.Count - 1)) + " " + S.AndWord + " " + names[^1];
        return Loc.Format("RunningNote", who);
    }

    static DateTime Touched(FileInfo f) => f.CreationTime > f.LastWriteTime ? f.CreationTime : f.LastWriteTime;

    // ---------- tool-resolved paths ----------
    //
    // Some caches move when the user configures the tool, so the tool itself is
    // asked where its cache lives. A resolved path is only trusted when it is
    // an absolute path inside the user's profile; anything else falls back to
    // the documented default locations. Tools are only ever asked, never told
    // to clean: no command here deletes anything.
    //
    // VERIFIED at Wave 3 merge time (per §1.2): the 5-second timeout + kill, the
    // exit-code check, the profile working directory (so per-project config never
    // applies), and the inside-profile validation all read soundly, and the
    // Round 3 tests for the validation rules carry over. One hardening fix was
    // applied: stdout is now read asynchronously while waiting, so a chatty tool
    // can never deadlock the 5-second wait by filling the pipe buffer.

    /// <summary>Runs a tool with no window and returns its standard output, or null on any failure or timeout.</summary>
    static string? ToolOutput(string fileName, string arguments)
    {
        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                // The user's profile, so per-project config files never apply.
                WorkingDirectory = Env.UserProfile,
            };
            if (!p.Start()) return null;
            // Read asynchronously while waiting: a tool that fills the pipe
            // buffer must never deadlock the wait.
            var read = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            if (p.ExitCode != 0) return null;
            return read.GetAwaiter().GetResult();
        }
        catch { return null; }
    }

    /// <summary>Non-empty output lines, trimmed. Empty when the tool said nothing.</summary>
    internal static string[] ToolOutputLines(string? output) =>
        (output ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// True when <paramref name="candidate"/> is an absolute path that stays inside
    /// <paramref name="profileRoot"/> once normalised. Rejects relative paths, other
    /// drives, UNC paths, sibling profiles and anything escaping via "..".
    /// </summary>
    internal static bool IsInsideProfile(string? candidate, string profileRoot)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathRooted(candidate)) return false;
        string full, root;
        try
        {
            full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            root = Path.GetFullPath(profileRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { return false; }
        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The tool's own answer when it can be trusted, else null (the caller falls back to defaults).</summary>
    internal static string? TrustedToolPath(string? candidate, string profileRoot) =>
        candidate is not null && IsInsideProfile(candidate, profileRoot) ? Path.GetFullPath(candidate) : null;

    /// <summary>
    /// npm cache roots: the location npm itself reports, plus both documented
    /// defaults (the older Roaming location may still hold debris from an old install).
    /// </summary>
    static string[] NpmCacheRoots()
    {
        var roots = new List<string>();
        var resolved = TrustedToolPath(
            ToolOutputLines(ToolOutput("cmd.exe", "/c npm config get cache")).FirstOrDefault(), Env.UserProfile);
        if (resolved is not null) roots.Add(resolved);
        roots.Add(Env.L("npm-cache"));
        roots.Add(Env.R("npm-cache"));
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>npm cache leaves only: the download cache, the npx cache and the logs.</summary>
    static IEnumerable<string> NpmLeaves() => NpmCacheRoots().SelectMany(r => new[]
    {
        Path.Combine(r, "_cacache"), Path.Combine(r, "_npx"), Path.Combine(r, "_logs"),
    });

    /// <summary>Go's build and module caches, resolved with <c>go env</c>, or the documented defaults.</summary>
    static string[] GoCachePaths()
    {
        var lines = ToolOutputLines(ToolOutput("go.exe", "env GOCACHE GOMODCACHE"));
        var build = TrustedToolPath(lines.ElementAtOrDefault(0), Env.UserProfile) ?? Env.L("go-build");
        var mod = TrustedToolPath(lines.ElementAtOrDefault(1), Env.UserProfile)
            ?? Path.Combine(Env.UserProfile, "go", "pkg", "mod");
        return [build, mod];
    }

    // ---------- steps ----------

    static CleanupCategory RecycleBin(ScanContext c)
    {
        var cat = Catalog.Create("recycle");
        var (size, count) = Native.QueryRecycleBin();
        if (size > 0)
            cat.Items.Add(new CleanupItem
            {
                Name = S.CatRecycle, Paths = ["shell:RecycleBinFolder"], Size = size,
                Detail = $"{count:N0} {Loc.Items(count)}",
            });
        return cat;
    }

    static CleanupCategory Temp(ScanContext c)
    {
        var cat = Catalog.Create("temp");
        Add(cat, Item(S.ItemTempFolder, OneDay, c, [Env.Temp]));
        // Legacy web cache from Internet Explorer-era components. Modern browsers
        // don't use it. Cookies and the WebCache database are left alone: cookies
        // sign the user out, and the database holds history.
        Add(cat, Item(S.ItemLegacyWebCache, OneDay, c,
            [Env.L("Microsoft", "Windows", "INetCache"), Env.L("Microsoft", "Feeds Cache")]));
        return Sorted(cat);
    }

    static readonly (string Name, string UserData)[] Chromium =
    [
        ("Google Chrome", Env.L("Google", "Chrome", "User Data")),
        ("Microsoft Edge", Env.L("Microsoft", "Edge", "User Data")),
        ("Brave", Env.L("BraveSoftware", "Brave-Browser", "User Data")),
        ("Vivaldi", Env.L("Vivaldi", "User Data")),
        ("Chromium", Env.L("Chromium", "User Data")),
    ];

    static IEnumerable<string> ChromiumCaches(string userData)
    {
        if (!Directory.Exists(userData)) yield break;
        yield return Path.Combine(userData, "ShaderCache");
        yield return Path.Combine(userData, "GrShaderCache");
        yield return Path.Combine(userData, "GraphiteDawnCache");
        foreach (var dir in Subdirs(userData))
        {
            var n = Path.GetFileName(dir);
            if (n == "Default" || n == "Guest Profile" || n.StartsWith("Profile ", StringComparison.Ordinal))
            {
                yield return Path.Combine(dir, "Cache");
                yield return Path.Combine(dir, "Cache_Data");
                yield return Path.Combine(dir, "Code Cache");
                yield return Path.Combine(dir, "GPUCache");
                yield return Path.Combine(dir, "DawnCache");
            }
        }
    }

    static CleanupCategory Browsers(ScanContext c)
    {
        var cat = Catalog.Create("browser");
        foreach (var (name, ud) in Chromium) Add(cat, Item(name, null, c, ChromiumCaches(ud)));
        // Firefox keeps its HTTP cache in the Local profile. Site storage in the
        // Roaming profile holds real web-app data and is never included.
        Add(cat, Item(S.ItemFirefox, null, c,
            Subdirs(Env.L("Mozilla", "Firefox", "Profiles")).SelectMany(p => new[]
            {
                Path.Combine(p, "cache2"), Path.Combine(p, "startupCache"),
                Path.Combine(p, "shader-cache"), Path.Combine(p, "GPUCache"),
            })));
        // Opera splits profile (Roaming, never touched) from cache (Local). Since
        // Opera 100.x the cache may instead live under the Roaming profile's
        // Default folder, so those exact leaves are probed too — never the profile.
        Add(cat, Item(S.ItemOpera, null, c,
            [
                Env.L("Opera Software", "Opera Stable", "Cache"),
                Env.L("Opera Software", "Opera GX Stable", "Cache"),
                Env.R("Opera Software", "Opera Stable", "Default", "Cache"),
                Env.R("Opera Software", "Opera Stable", "Default", "Code Cache"),
                Env.R("Opera Software", "Opera Stable", "Default", "GPUCache"),
                Env.R("Opera Software", "Opera GX Stable", "Default", "Cache"),
                Env.R("Opera Software", "Opera GX Stable", "Default", "Code Cache"),
                Env.R("Opera Software", "Opera GX Stable", "Default", "GPUCache"),
            ]));
        if (cat.Items.Count > 0)
            cat.Note = RunningNote(Running(("chrome", "Chrome"), ("msedge", "Edge"), ("firefox", S.ItemFirefox),
                ("brave", "Brave"), ("opera", S.ItemOpera), ("vivaldi", "Vivaldi")));
        return Sorted(cat);
    }

    static CleanupCategory Apps(ScanContext c)
    {
        var cat = Catalog.Create("apps");
        string[] bits = ["Cache", "Code Cache", "GPUCache"];
        IEnumerable<string> Bits(string root) => bits.Select(b => Path.Combine(root, b));
        // VS Code's safe leaves: web caches, per-version bytecode, cached extension
        // packages and logs. Settings, workspace state and chat history live elsewhere.
        IEnumerable<string> CodeLeaves(string root) => Bits(root).Concat(
            [Path.Combine(root, "CachedData"), Path.Combine(root, "CachedExtensionVSIXs"), Path.Combine(root, "logs")]);

        Add(cat, Item(S.ItemDiscord, null, c,
            new[] { "discord", "discordptb", "discordcanary" }.SelectMany(d => Bits(Env.R(d)))));
        Add(cat, Item(S.ItemSlack, null, c, Bits(Env.R("Slack"))));
        Add(cat, Item(S.ItemTeamsClassic, null, c, Bits(Env.R("Microsoft", "Teams"))));
        // New Teams keeps its web cache in WebView2 profiles; settings and sign-in live elsewhere.
        // Its logs and cached avatars sit alongside and are rebuilt too.
        // VERIFIED at Wave 3 merge time: Round 3 also probed `tfl` and `tfw` folders
        // here, but what those opaque names hold is unverified, so they were DROPPED
        // — only the self-describing Logs and UserAvatarIcons leaves are kept.
        Add(cat, Item("Microsoft Teams", null, c,
            Subdirs(Env.L("Packages", "MSTeams_8wekyb3d8bbwe", "LocalCache", "Microsoft", "MSTeams", "EBWebView"))
                .Where(p => Path.GetFileName(p) is var n && (n == "Default" || n.StartsWith("WV2Profile", StringComparison.Ordinal)))
                .SelectMany(Bits)
                .Concat(
                [
                    Env.L("Packages", "MSTeams_8wekyb3d8bbwe", "LocalCache", "Microsoft", "MSTeams", "Logs"),
                    Env.L("Packages", "MSTeams_8wekyb3d8bbwe", "LocalCache", "Microsoft", "MSTeams", "UserAvatarIcons"),
                ])));
        // Spotify's streaming cache (Data) and its built-in browser cache. Offline downloads (Storage) are left alone.
        Add(cat, Item("Spotify", null, c,
            Bits(Env.L("Spotify", "Browser"))
                .Append(Env.L("Spotify", "Data"))
                .Append(Env.L("Packages", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0", "LocalCache", "Spotify", "Data"))));
        Add(cat, Item("Epic Games Launcher", null, c,
            Subdirs(Env.L("EpicGamesLauncher", "Saved"))
                .Where(p => Path.GetFileName(p).StartsWith("webcache", StringComparison.OrdinalIgnoreCase))));
        Add(cat, Item(S.ItemVsCode, null, c, CodeLeaves(Env.R("Code"))));
        Add(cat, Item(S.ItemVsCodeInsiders, null, c, CodeLeaves(Env.R("Code - Insiders"))));
        Add(cat, Item(S.ItemSteamBrowser, null, c, [Env.L("Steam", "htmlcache")]));
        // Steam's encrypted download chunks. They re-download if a download resumes;
        // in-progress and paused downloads live elsewhere and are left alone.
        Add(cat, Item(S.ItemSteamDepot, null, c,
            Steam.Libraries().Select(l => Path.Combine(l, "depotcache"))));
        // The npm cache moves with npm's own config, so npm itself is asked where
        // it lives; the documented defaults are probed as well.
        Add(cat, Item(S.ItemNpm, null, c, NpmLeaves(), toolResolved: true));
        Add(cat, Item(S.ItemPip, null, c, [Env.L("pip", "Cache")]));
        Add(cat, Item(S.ItemYarn, null, c, [Env.L("Yarn", "Cache")]));
        Add(cat, Item(S.ItemNuget, null, c, [Env.L("NuGet", "v3-cache"), Env.L("NuGet", "plugins-cache")]));
        // AI assistants are Electron apps: only their Chromium cache leaves are touched.
        // Config, chats and history live elsewhere and are never included. ChatGPT's
        // folder depends on the installer, so both classic locations are probed.
        Add(cat, Item(S.ItemChatGpt, null, c,
            new[] { Env.R("ChatGPT"), Env.R("OpenAI", "ChatGPT") }.SelectMany(Bits)));
        Add(cat, Item(S.ItemClaude, null, c,
            new[]
            {
                Env.R("Claude"),
                Env.L("Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude"),
                Env.L("AnthropicClaude"),
            }.SelectMany(r => Bits(r).Concat(new[]
            {
                Path.Combine(r, "GrShaderCache"), Path.Combine(r, "DawnGraphiteCache"),
                Path.Combine(r, "DawnWebGPUCache"), Path.Combine(r, "Crashpad"),
            }))));
        // Cursor: cache leaves only. User\ (settings, history) and the chat database
        // (state.vscdb) are never touched.
        Add(cat, Item(S.ItemCursor, null, c,
            Bits(Env.R("Cursor")).Append(Env.R("Cursor", "CachedData"))));
        // Game launcher web caches: contents only, launcher closed. Games, saves and
        // sign-ins live elsewhere. Xbox has no hand-delete folder, so it stays out.
        Add(cat, Item(S.ItemBattleNet, null, c,
            [Env.PD("Battle.net"), Env.PD("Blizzard Entertainment", "Battle.net", "Cache"),
             Env.L("Blizzard Entertainment", "Battle.net", "Cache"), Env.L("Battle.net")]));
        Add(cat, Item(S.ItemEaApp, null, c,
            [Env.L("Electronic Arts", "EA Desktop", "Cache"), Env.L("Electronic Arts", "EA Desktop", "cache")]));
        Add(cat, Item(S.ItemGog, null, c, [Env.L("GOG.com", "Galaxy", "webcache")]));
        Add(cat, Item(S.ItemUbisoft, null, c, [Env.L("Ubisoft Game Launcher", "cache")]));
        Add(cat, Item(S.ItemObs, null, c,
            [Env.R("obs-studio", "logs"), Env.R("obs-studio", "crashes"),
             Env.R("obs-studio", "plugin_config", "obs-browser", "Cache")]));
        // Adobe's shared media cache: conformed audio, waveforms and indexes. Projects,
        // footage and presets live elsewhere. Adobe rebuilds the cache on next open.
        Add(cat, Item(S.ItemAdobeCache, null, c,
            [Env.R("Adobe", "Common", "Media Cache Files"), Env.R("Adobe", "Common", "Media Cache"),
             Env.R("Adobe", "Common", "Peak Files")]));
        Add(cat, Item(S.ItemVisualStudio, null, c,
            Subdirs(Env.L("Microsoft", "VisualStudio")).Select(p => Path.Combine(p, "ComponentModelCache"))));
        Add(cat, Item(S.ItemChocolatey, null, c,
            [Env.PD("ChocolateyHttpCache"), Path.Combine(Env.UserProfile, ".chocolatey", "http-cache")]));
        // Zoom: only its data and logs folders, the documented clear. Settings and
        // local recordings live elsewhere and are never included.
        Add(cat, Item(S.ItemZoom, null, c, [Env.R("Zoom", "data"), Env.R("Zoom", "logs")]));
        // Signal: logs only. Messages and attachments live only on this PC and are
        // end-to-end encrypted — the rest of the folder is never touched.
        Add(cat, Item(S.ItemSignalLogs, null, c, [Env.R("Signal", "logs")]));
        // Outlook's roaming settings cache. It rebuilds itself and holds no mail;
        // the mailbox files themselves are a separate, check-first category.
        Add(cat, Item(S.ItemOutlookRoamCache, null, c, [Env.L("Microsoft", "Outlook", "RoamCache")]));
        if (cat.Items.Count > 0)
            cat.Note = RunningNote(Running(("Discord", S.ItemDiscord), ("slack", S.ItemSlack), ("Code", S.ItemVsCode),
                ("Code - Insiders", S.ItemVsCodeInsiders), ("Teams", "Teams"),
                ("ms-teams", "Teams"), ("Spotify", "Spotify"), ("EpicGamesLauncher", "Epic Games Launcher"),
                ("steam", "Steam"), ("Zoom", "Zoom"), ("OUTLOOK", "Outlook")));
        return Sorted(cat);
    }

    /// <summary>
    /// Outlook's offline mailbox copies (.ost), one item per file so each mailbox
    /// can be judged on its own. .pst files are never listed: they may be the only
    /// copy of the mail. The warning in the category note must stay.
    /// </summary>
    static CleanupCategory Outlook(ScanContext c)
    {
        var cat = Catalog.Create("outlook");
        foreach (var ost in FilesIn(Env.L("Microsoft", "Outlook"), "*.ost"))
        {
            try
            {
                var fi = new FileInfo(ost);
                if (fi.Length <= 0) continue;
                cat.Items.Add(new CleanupItem
                {
                    Name = fi.Name, Paths = [fi.FullName], Size = fi.Length, FileCount = 1, IsFile = true,
                });
            }
            catch { }
        }
        if (cat.Items.Count > 0)
        {
            var running = RunningNote(Running(("OUTLOOK", "Outlook")));
            cat.Note = running is null ? S.OutlookNote : S.OutlookNote + " " + running;
        }
        return Sorted(cat);
    }

    static CleanupCategory Messaging(ScanContext c)
    {
        var cat = Catalog.Create("messaging");
        // WhatsApp's local media copies. The package name varies by build, so it
        // is matched by prefix, never hardcoded in full. Messages and chats live
        // elsewhere and are never included.
        var transfers = Subdirs(Env.L("Packages"))
            .Where(p => Path.GetFileName(p).Contains("WhatsAppDesktop", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.Combine(p, "LocalState", "shared", "transfers"));
        Add(cat, Item(S.ItemWhatsApp, null, c, transfers));
        if (cat.Items.Count > 0)
            cat.Note = RunningNote(Running(("WhatsApp", "WhatsApp")));
        return Sorted(cat);
    }

    static CleanupCategory DevCaches(ScanContext c)
    {
        var cat = Catalog.Create("devcaches");
        // Package and build caches only — never stores other projects link into
        // (pnpm, uv) and never settings, credentials or installed tools.
        Add(cat, Item(S.ItemMaven, null, c, [Path.Combine(Env.UserProfile, ".m2", "repository")]));
        Add(cat, Item(S.ItemGradle, null, c,
            [Path.Combine(Env.UserProfile, ".gradle", "caches"), Path.Combine(Env.UserProfile, ".gradle", "wrapper", "dists")]));
        Add(cat, Item(S.ItemCargo, null, c,
            [Path.Combine(Env.UserProfile, ".cargo", "registry", "cache"), Path.Combine(Env.UserProfile, ".cargo", "registry", "src"),
             Path.Combine(Env.UserProfile, ".cargo", "git")]));
        // Go resolves its cache locations with `go env` (they move with GOPATH);
        // the documented defaults are the fallback when Go isn't installed.
        Add(cat, Item(S.ItemGoCache, null, c, GoCachePaths(), toolResolved: true));
        // NuGet's global packages folder. The web cache lives in App caches; the
        // config file with registry credentials (NuGet.Config) is never touched.
        Add(cat, Item(S.ItemNuGetPackages, null, c, [Path.Combine(Env.UserProfile, ".nuget", "packages")]));
        // C/C++ IntelliSense indexes for VS Code. Clearing them forces a full
        // re-index of every C/C++ project, so this stays your call.
        Add(cat, Item(S.ItemCppCache, null, c, [Env.L("Microsoft", "vscode-cpptools")]));
        return Sorted(cat);
    }

    static CleanupCategory Shaders(ScanContext c)
    {
        var cat = Catalog.Create("shaders");
        // Sources disagree on the DirectX cache location, so both reported spots are probed.
        Add(cat, Item(S.ItemDirectX, null, c, [Env.L("D3DSCache"), Env.L("Microsoft", "D3DSCache")]));
        Add(cat, Item(S.ItemNvidia, null, c,
            [Env.L("NVIDIA", "DXCache"), Env.L("NVIDIA", "GLCache"), Env.L("NVIDIA", "ComputeCache"), Env.L("NVIDIA Corporation", "NV_Cache"), Env.PD("NVIDIA Corporation", "NV_Cache")]));
        Add(cat, Item("AMD", null, c,
            [Env.L("AMD", "DxCache"), Env.L("AMD", "DxcCache"), Env.L("AMD", "GLCache"), Env.L("AMD", "VkCache")]));
        Add(cat, Item("Intel", null, c, [Env.L("Intel", "ShaderCache")]));
        Add(cat, Item(S.ItemSteamGames, null, c,
            Steam.Libraries().Select(l => Path.Combine(l, "steamapps", "shadercache"))));
        return Sorted(cat);
    }

    static CleanupCategory AvdCaches(ScanContext c)
    {
        var cat = Catalog.Create("avd");
        var avdRoot = Path.Combine(Env.UserProfile, ".android", "avd");
        foreach (var dir in Subdirs(avdRoot))
        {
            var name = Path.GetFileName(dir);
            if (!name.EndsWith(".avd", StringComparison.OrdinalIgnoreCase)) continue;
            // Only Quick Boot snapshots and the scratch cache image. User data, SD card
            // images, config and the debug keystore are never included.
            Add(cat, Item(name[..^4], null, c,
                [Path.Combine(dir, "snapshots"), Path.Combine(dir, "cache.img")]));
        }
        if (cat.Items.Count > 0)
            cat.Note = RunningNote(Running(("qemu-system-x86_64", "Android Emulator"), ("emulator", "Android Emulator")));
        return Sorted(cat);
    }

    static CleanupCategory Crashes(ScanContext c)
    {
        var cat = Catalog.Create("crash");
        Add(cat, Item(S.ItemAppCrashDumps, null, c, [Env.L("CrashDumps")]));
        Add(cat, Item(S.ItemWindowsErrorReports, null, c, [Env.L("Microsoft", "Windows", "WER")]));
        // Browser crash uploads: already-sent or uploadable copies. The report IDs
        // stay listed in each browser's own crash page.
        Add(cat, Item(S.ItemBrowserCrashReports, null, c,
            Chromium.Select(ch => Path.Combine(ch.UserData, "Crashpad"))));
        if (cat.Items.Count > 0)
            cat.Note = RunningNote(Running(("chrome", "Chrome"), ("msedge", "Edge"), ("brave", "Brave")));
        return Sorted(cat);
    }

    static CleanupCategory Thumbnails(ScanContext c)
    {
        var cat = Catalog.Create("thumbnails");
        // Only the thumbnail and icon database files themselves, never the folder.
        Add(cat, Item(S.ItemThumbnails, null, c,
            FilesIn(Env.L("Microsoft", "Windows", "Explorer"), "thumbcache_*.db", "iconcache_*.db")
                .Append(Env.L("IconCache.db"))));
        return cat;
    }

    static CleanupCategory WindowsLeftovers(ScanContext c)
    {
        var cat = Catalog.Create("windows");
        if (!c.IsAdmin)
        {
            cat.Blocked = true;
            cat.Note = S.WindowsLocksNote;
            return cat;
        }
        Add(cat, Item(S.ItemDownloadedUpdates, null, c, [Env.W("SoftwareDistribution", "Download")]));
        Add(cat, Item(S.ItemDeliveryOptimization, null, c,
            [Env.W("ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache")]));
        Add(cat, Item(S.ItemWindowsTemp, OneDay, c, [Env.W("Temp")]));
        Add(cat, Item(S.ItemSystemCrashDumps, null, c,
            [Env.W("Minidump"), Env.W("MEMORY.DMP"), Env.W("LiveKernelReports"), Env.PD("Microsoft", "Windows", "WER", "ReportArchive"), Env.PD("Microsoft", "Windows", "WER", "ReportQueue")]));
        return Sorted(cat);
    }

    /// <summary>
    /// Sizes only: logs and indexes that need their owner's own tool, or a service
    /// stopped first, before anything is cleared. Each item says where to act.
    /// Purge for Windows never deletes these files itself.
    /// </summary>
    static CleanupCategory Diagnostics(ScanContext c)
    {
        var cat = Catalog.Create("diagnostics");
        if (!c.IsAdmin)
        {
            cat.Blocked = true;
            cat.Note = S.WindowsLocksNote;
            return cat;
        }
        cat.Note = S.DiagnosticsNote;
        Add(cat, Report(S.ItemCbsLogs, S.CbsLogsDetail, c,
            [Env.W("Logs", "CBS"), Env.W("Logs", "DISM")]));
        Add(cat, Report(S.ItemEtlLogs, S.EtlLogsDetail, c,
            [Env.PD("Microsoft", "Diagnosis", "ETLLogs"), Env.PD("Microsoft", "Diagnosis", "DownloadedLogs.etl")]));
        Add(cat, Report(S.ItemEventLogs, S.EventLogsDetail, c,
            [Env.W("System32", "winevt", "Logs")]));
        Add(cat, Report(S.ItemFontCache, S.FontCacheDetail, c,
            [Env.W("ServiceProfiles", "LocalService", "AppData", "Local", "FontCache"),
             Env.W("System32", "FNTCACHE.DAT")]));
        Add(cat, Report(S.ItemSearchIndex, S.SearchIndexDetail, c,
            [Env.PD("Microsoft", "Search", "Data", "Applications", "Windows")]));
        return Sorted(cat);
    }

    static CleanupCategory WindowsLogs(ScanContext c)
    {
        var cat = Catalog.Create("winlogs");
        if (!c.IsAdmin)
        {
            cat.Blocked = true;
            cat.Note = S.WindowsLocksNote;
            return cat;
        }
        cat.Note = S.WinLogsNote;
        // Setup logs only — the copy under $Windows.~BT stays with the Previous
        // Windows version category, so the same bytes are never counted twice.
        Add(cat, Item(S.ItemPanther, null, c, [Env.W("Panther")]));
        // Web server logs older than 30 days. Only present when IIS is installed.
        Add(cat, Item(S.ItemIisLogs, ThirtyDays, c,
            [Path.Combine(Env.SystemRoot, "inetpub", "logs", "LogFiles")]));
        // The user's own Performance Monitor reports.
        Add(cat, Item(S.ItemPerfLogs, null, c, [Path.Combine(Env.SystemRoot, "PerfLogs")]));
        return Sorted(cat);
    }

    static CleanupCategory Installers(ScanContext c)
    {
        var cat = Catalog.Create("installers");
        var cutoff = DateTime.Now.AddDays(-7);
        foreach (var f in c.EnumerateDownloads())
        {
            try
            {
                if (!InstallerExt.Contains(f.Extension)) continue;
                // Only loose files in Downloads itself. .exe files inside extracted folders are usually
                // portable apps, not installers.
                if (!string.Equals(f.DirectoryName?.TrimEnd('\\'), Env.Downloads.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                var t = Touched(f);
                if (t > cutoff) continue;
                cat.Items.Add(new CleanupItem
                {
                    Name = f.Name, Paths = [f.FullName], Size = f.Length, FileCount = 1, IsFile = true,
                    Detail = Loc.Format("DownloadedAgo", Format.Ago(t)),
                });
            }
            catch { }
        }
        return Sorted(cat);
    }

    static CleanupCategory BigDownloads(ScanContext c)
    {
        var cat = Catalog.Create("downloads");
        const long minSize = 250L * 1024 * 1024;
        var cutoff = DateTime.Now.AddDays(-30);
        foreach (var f in c.EnumerateDownloads())
        {
            try
            {
                if (f.Length < minSize || InstallerExt.Contains(f.Extension)) continue;
                var t = Touched(f);
                if (f.LastAccessTime > t) t = f.LastAccessTime;
                if (t > cutoff) continue;
                cat.Items.Add(new CleanupItem
                {
                    Name = f.Name, Paths = [f.FullName], Size = f.Length, FileCount = 1, IsFile = true,
                    Detail = Loc.Format("UntouchedFor", Format.Ago(t)),
                });
            }
            catch { }
        }
        return Sorted(cat);
    }

    /// <summary>
    /// Game Bar clips and screenshots, one item per file. Only the folder's own
    /// recording and screenshot types are listed, and only loose files in the
    /// folder itself — anything else the user stored there stays out.
    /// </summary>
    static CleanupCategory Captures(ScanContext c)
    {
        var cat = Catalog.Create("captures");
        string videos;
        try { videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos); }
        catch { return cat; }
        if (string.IsNullOrEmpty(videos) || !Path.IsPathRooted(videos)) return cat;
        var dir = Path.Combine(videos, "Captures");
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly).ToList(); }
        catch { return cat; }
        foreach (var f in files)
        {
            try
            {
                var ext = Path.GetExtension(f);
                if (!ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".png", StringComparison.OrdinalIgnoreCase)) continue;
                var fi = new FileInfo(f);
                if (fi.Length <= 0) continue;
                cat.Items.Add(new CleanupItem
                {
                    Name = fi.Name, Paths = [fi.FullName], Size = fi.Length, FileCount = 1, IsFile = true,
                    Detail = Loc.Format("CapturedAgo", Format.Ago(Touched(fi))),
                });
            }
            catch { }
        }
        return Sorted(cat);
    }

    static CleanupCategory Games(ScanContext c)
    {
        var cat = Catalog.Create("games");
        var cutoff = DateTime.Now.AddDays(-60);
        foreach (var g in Steam.Games())
        {
            if (g.Size < 1L << 30) continue;
            if (g.LastPlayed is { } lp && lp > cutoff) continue;
            cat.Items.Add(new CleanupItem
            {
                Name = g.Name, Paths = [g.InstallPath], Size = g.Size, AppId = g.AppId,
                Detail = g.LastPlayed is { } p ? Loc.Format("LastPlayed", Format.Ago(p)) : S.NeverPlayed,
            });
        }
        return Sorted(cat);
    }

    /// <summary>
    /// Sizes only: virtual machine and emulator disks. These are never deleted by Purge for Windows —
    /// a hand-deleted disk image destroys the machine. The owning tool must compact or
    /// remove them, so each item just reports its size and opens its folder.
    /// </summary>
    static CleanupCategory VirtualMachines(ScanContext c)
    {
        var cat = Catalog.Create("virtual");
        cat.Note = S.VirtualNote;
        var detail = S.VirtualItemDetail;
        Add(cat, Report(S.ItemVirtualBox, detail, c, [Path.Combine(Env.UserProfile, "VirtualBox VMs")]));
        Add(cat, Report(S.ItemVMware, detail, c, [Path.Combine(Env.UserProfile, "Documents", "Virtual Machines")]));
        Add(cat, Report(S.ItemHyperV, detail, c,
            [Path.Combine(Env.SystemRoot, "Users", "Public", "Documents", "Hyper-V", "Virtual Hard Disks")]));
        // WSL2 distro disks, one item per distro.
        foreach (var dir in Subdirs(Env.L("Packages")))
        {
            if (!Path.GetFileName(dir).StartsWith("CanonicalGroupLimited", StringComparison.OrdinalIgnoreCase)) continue;
            var vhdx = Path.Combine(dir, "LocalState", "ext4.vhdx");
            if (File.Exists(vhdx)) Add(cat, Report(Path.GetFileName(dir), detail, c, [vhdx]));
        }
        foreach (var dir in Subdirs(Env.L("wsl")))
        {
            var vhdx = Path.Combine(dir, "ext4.vhdx");
            if (File.Exists(vhdx)) Add(cat, Report(Path.GetFileName(dir), detail, c, [vhdx]));
        }
        Add(cat, Report(S.ItemDocker, S.DockerItemDetail, c,
            [Env.L("Docker", "wsl", "disk", "docker_data.vhdx"), Env.L("Docker", "wsl", "data", "ext4.vhdx")]));
        Add(cat, Report(S.ItemBlueStacks, detail, c, [Env.PD("BlueStacks_nxt", "Engine")]));
        var drive = Env.SystemRoot;
        Add(cat, Report(S.ItemLdPlayer, detail, c,
            [Path.Combine(drive, "LDPlayer", "LDPlayer9", "vms"), Path.Combine(drive, "LDPlayer", "LDPlayer14", "vms"),
             Path.Combine(drive, "leidian", "LDPlayer9", "vms"), Path.Combine(drive, "leidian", "LDPlayer14", "vms")]));
        Add(cat, Report(S.ItemNox, detail, c,
            [Path.Combine(Env.ProgramFiles, "Nox", "bin", "BignoxVMS"),
             Path.Combine(Env.ProgramFilesX86, "Nox", "bin", "BignoxVMS"),
             Env.R("Nox", "bin", "BignoxVMS")]));
        Add(cat, Report(S.ItemMemu, detail, c,
            [Path.Combine(Env.ProgramFiles, "Microvirt", "MEmu", "image"),
             Path.Combine(Env.ProgramFilesX86, "Microvirt", "MEmu", "image")]));
        return Sorted(cat);
    }

    static CleanupCategory? WindowsOld(ScanContext c)
    {
        var cat = Catalog.Create("winold");
        var root = Env.SystemRoot;
        var folders = new[] { "Windows.old", "$WINDOWS.~BT", "$WINDOWS.~WS", "ESD", "Windows10Upgrade", "$GetCurrent", "$SysReset" };
        bool staging = false;
        foreach (var folder in folders)
        {
            var p = Path.Combine(root, folder);
            if (!Directory.Exists(p)) continue;
            TellPath(c.PathProgress, p);
            var (b, _) = FileOps.Measure(p, null, c.Token, c.PathProgress);
            if (b <= 0) continue;
            if (folder != "Windows.old") staging = true;
            cat.Items.Add(new CleanupItem { Name = folder, Paths = [p], Size = b });
        }
        if (cat.Items.Count == 0) return null;
        if (staging) cat.Note = S.WinOldStagingNote;
        return cat;
    }
}
