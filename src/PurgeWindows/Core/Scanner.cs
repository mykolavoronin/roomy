using System.Diagnostics;
using PurgeWindows.Localization;

namespace PurgeWindows.Core;

public sealed class ScanContext
{
    public bool IsAdmin { get; init; }
    public CancellationToken Token { get; init; }

    readonly Lazy<List<FileInfo>> _downloads = new(LoadDownloads);
    public List<FileInfo> DownloadFiles => _downloads.Value;

    static List<FileInfo> LoadDownloads()
    {
        try
        {
            var d = new DirectoryInfo(Env.Downloads);
            if (!d.Exists) return [];
            return d.EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            }).ToList();
        }
        catch { return []; }
    }
}

public sealed record ScanStep(string Id, string Title, Func<ScanContext, CleanupCategory?> Run);

/// <summary>Each step looks in a few well-known places and reports what it found. Nothing here deletes.</summary>
public static class Scanner
{
    static readonly TimeSpan OneDay = TimeSpan.FromDays(1);

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
        new("devcaches", S.CatDevCaches, DevCaches),
        new("shaders", S.StepShaders, Shaders),
        new("avd", S.CatAvd, AvdCaches),
        new("crash", S.CatCrash, Crashes),
        new("thumbnails", S.CatThumbnails, Thumbnails),
        new("windows", S.StepWindows, WindowsLeftovers),
        new("installers", S.CatInstallers, Installers),
        new("downloads", S.StepDownloads, BigDownloads),
        new("games", S.StepGames, Games),
        new("virtual", S.CatVirtual, VirtualMachines),
        new("winold", S.StepWinOld, WindowsOld),
    ];

    // ---------- helpers ----------

    static CleanupItem? Item(string name, TimeSpan? minAge, CancellationToken ct, IEnumerable<string> paths)
    {
        var existing = paths.Where(FileOps.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (existing.Count == 0) return null;
        long size = 0;
        int files = 0;
        foreach (var p in existing)
        {
            var (b, f) = FileOps.Measure(p, minAge, ct);
            size += b;
            files += f;
        }
        if (size <= 0) return null;
        return new CleanupItem
        {
            Name = name, Paths = existing, Size = size, FileCount = files, MinAge = minAge,
            Detail = $"{files:N0} {Loc.Files(files)}",
        };
    }

    /// <summary>Like <see cref="Item"/>, but for sizes Purge for Windows only reports: the detail tells the user where to act.</summary>
    static CleanupItem? Report(string name, string detail, CancellationToken ct, IEnumerable<string> paths)
    {
        var existing = paths.Where(FileOps.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (existing.Count == 0) return null;
        long size = 0;
        foreach (var p in existing) size += FileOps.Measure(p, null, ct).Bytes;
        if (size <= 0) return null;
        return new CleanupItem { Name = name, Paths = existing, Size = size, Detail = detail };
    }

    static void Add(CleanupCategory c, CleanupItem? i)
    {
        if (i != null) c.Items.Add(i);
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
        Add(cat, Item(S.ItemTempFolder, OneDay, c.Token, [Env.Temp]));
        return cat;
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
                yield return Path.Combine(dir, "Code Cache");
                yield return Path.Combine(dir, "GPUCache");
            }
        }
    }

    static CleanupCategory Browsers(ScanContext c)
    {
        var cat = Catalog.Create("browser");
        foreach (var (name, ud) in Chromium) Add(cat, Item(name, null, c.Token, ChromiumCaches(ud)));
        Add(cat, Item(S.ItemFirefox, null, c.Token,
            Subdirs(Env.L("Mozilla", "Firefox", "Profiles")).Select(p => Path.Combine(p, "cache2"))));
        Add(cat, Item(S.ItemOpera, null, c.Token,
            [Env.L("Opera Software", "Opera Stable", "Cache"), Env.L("Opera Software", "Opera GX Stable", "Cache")]));
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

        Add(cat, Item(S.ItemDiscord, null, c.Token,
            new[] { "discord", "discordptb", "discordcanary" }.SelectMany(d => Bits(Env.R(d)))));
        Add(cat, Item(S.ItemSlack, null, c.Token, Bits(Env.R("Slack"))));
        Add(cat, Item(S.ItemTeamsClassic, null, c.Token, Bits(Env.R("Microsoft", "Teams"))));
        // New Teams keeps its web cache in WebView2 profiles; settings and sign-in live elsewhere.
        Add(cat, Item("Microsoft Teams", null, c.Token,
            Subdirs(Env.L("Packages", "MSTeams_8wekyb3d8bbwe", "LocalCache", "Microsoft", "MSTeams", "EBWebView"))
                .Where(p => Path.GetFileName(p) is var n && (n == "Default" || n.StartsWith("WV2Profile", StringComparison.Ordinal)))
                .SelectMany(Bits)));
        // Spotify's streaming cache (Data) and its built-in browser cache. Offline downloads (Storage) are left alone.
        Add(cat, Item("Spotify", null, c.Token,
            Bits(Env.L("Spotify", "Browser"))
                .Append(Env.L("Spotify", "Data"))
                .Append(Env.L("Packages", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0", "LocalCache", "Spotify", "Data"))));
        Add(cat, Item("Epic Games Launcher", null, c.Token,
            Subdirs(Env.L("EpicGamesLauncher", "Saved"))
                .Where(p => Path.GetFileName(p).StartsWith("webcache", StringComparison.OrdinalIgnoreCase))));
        Add(cat, Item(S.ItemVsCode, null, c.Token,
            Bits(Env.R("Code")).Append(Env.R("Code", "CachedData")).Append(Env.R("Code", "CachedExtensionVSIXs"))));
        Add(cat, Item(S.ItemSteamBrowser, null, c.Token, [Env.L("Steam", "htmlcache")]));
        Add(cat, Item(S.ItemNpm, null, c.Token, [Env.L("npm-cache", "_cacache")]));
        Add(cat, Item(S.ItemPip, null, c.Token, [Env.L("pip", "Cache")]));
        Add(cat, Item(S.ItemYarn, null, c.Token, [Env.L("Yarn", "Cache")]));
        Add(cat, Item(S.ItemNuget, null, c.Token, [Env.L("NuGet", "v3-cache"), Env.L("NuGet", "plugins-cache")]));
        // AI assistants are Electron apps: only their Chromium cache leaves are touched.
        // Config, chats and history live elsewhere and are never included. ChatGPT's
        // folder depends on the installer, so both classic locations are probed.
        Add(cat, Item(S.ItemChatGpt, null, c.Token,
            new[] { Env.R("ChatGPT"), Env.R("OpenAI", "ChatGPT") }.SelectMany(Bits)));
        Add(cat, Item(S.ItemClaude, null, c.Token,
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
        Add(cat, Item(S.ItemCursor, null, c.Token,
            Bits(Env.R("Cursor")).Append(Env.R("Cursor", "CachedData"))));
        // Game launcher web caches: contents only, launcher closed. Games, saves and
        // sign-ins live elsewhere. Xbox has no hand-delete folder, so it stays out.
        Add(cat, Item(S.ItemBattleNet, null, c.Token,
            [Env.PD("Battle.net"), Env.PD("Blizzard Entertainment", "Battle.net", "Cache"),
             Env.L("Blizzard Entertainment", "Battle.net", "Cache"), Env.L("Battle.net")]));
        Add(cat, Item(S.ItemEaApp, null, c.Token,
            [Env.L("Electronic Arts", "EA Desktop", "Cache"), Env.L("Electronic Arts", "EA Desktop", "cache")]));
        Add(cat, Item(S.ItemGog, null, c.Token, [Env.L("GOG.com", "Galaxy", "webcache")]));
        Add(cat, Item(S.ItemUbisoft, null, c.Token, [Env.L("Ubisoft Game Launcher", "cache")]));
        Add(cat, Item(S.ItemObs, null, c.Token,
            [Env.R("obs-studio", "logs"), Env.R("obs-studio", "crashes"),
             Env.R("obs-studio", "plugin_config", "obs-browser", "Cache")]));
        // Adobe's shared media cache: conformed audio, waveforms and indexes. Projects,
        // footage and presets live elsewhere. Adobe rebuilds the cache on next open.
        Add(cat, Item(S.ItemAdobeCache, null, c.Token,
            [Env.R("Adobe", "Common", "Media Cache Files"), Env.R("Adobe", "Common", "Media Cache"),
             Env.R("Adobe", "Common", "Peak Files")]));
        Add(cat, Item(S.ItemVisualStudio, null, c.Token,
            Subdirs(Env.L("Microsoft", "VisualStudio")).Select(p => Path.Combine(p, "ComponentModelCache"))));
        Add(cat, Item(S.ItemChocolatey, null, c.Token,
            [Env.PD("ChocolateyHttpCache"), Path.Combine(Env.UserProfile, ".chocolatey", "http-cache")]));
        if (cat.Items.Count > 0)
            cat.Note = RunningNote(Running(("Discord", S.ItemDiscord), ("slack", S.ItemSlack), ("Code", S.ItemVsCode), ("Teams", "Teams"),
                ("ms-teams", "Teams"), ("Spotify", "Spotify"), ("EpicGamesLauncher", "Epic Games Launcher")));
        return Sorted(cat);
    }

    static CleanupCategory DevCaches(ScanContext c)
    {
        var cat = Catalog.Create("devcaches");
        // Package and build caches only — never stores other projects link into
        // (pnpm, uv) and never settings, credentials or installed tools.
        Add(cat, Item(S.ItemMaven, null, c.Token, [Path.Combine(Env.UserProfile, ".m2", "repository")]));
        Add(cat, Item(S.ItemGradle, null, c.Token,
            [Path.Combine(Env.UserProfile, ".gradle", "caches"), Path.Combine(Env.UserProfile, ".gradle", "wrapper", "dists")]));
        Add(cat, Item(S.ItemCargo, null, c.Token,
            [Path.Combine(Env.UserProfile, ".cargo", "registry", "cache"), Path.Combine(Env.UserProfile, ".cargo", "registry", "src"),
             Path.Combine(Env.UserProfile, ".cargo", "git")]));
        Add(cat, Item(S.ItemGoCache, null, c.Token,
            [Env.L("go-build"), Path.Combine(Env.UserProfile, "go", "pkg", "mod")]));
        return Sorted(cat);
    }

    static CleanupCategory Shaders(ScanContext c)
    {
        var cat = Catalog.Create("shaders");
        Add(cat, Item(S.ItemDirectX, null, c.Token, [Env.L("D3DSCache")]));
        Add(cat, Item(S.ItemNvidia, null, c.Token,
            [Env.L("NVIDIA", "DXCache"), Env.L("NVIDIA", "GLCache"), Env.L("NVIDIA Corporation", "NV_Cache"), Env.PD("NVIDIA Corporation", "NV_Cache")]));
        Add(cat, Item("AMD", null, c.Token,
            [Env.L("AMD", "DxCache"), Env.L("AMD", "DxcCache"), Env.L("AMD", "GLCache"), Env.L("AMD", "VkCache")]));
        Add(cat, Item("Intel", null, c.Token, [Env.L("Intel", "ShaderCache")]));
        Add(cat, Item(S.ItemSteamGames, null, c.Token,
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
            Add(cat, Item(name[..^4], null, c.Token,
                [Path.Combine(dir, "snapshots"), Path.Combine(dir, "cache.img")]));
        }
        if (cat.Items.Count > 0)
            cat.Note = RunningNote(Running(("qemu-system-x86_64", "Android Emulator"), ("emulator", "Android Emulator")));
        return Sorted(cat);
    }

    static CleanupCategory Crashes(ScanContext c)
    {
        var cat = Catalog.Create("crash");
        Add(cat, Item(S.ItemAppCrashDumps, null, c.Token, [Env.L("CrashDumps")]));
        Add(cat, Item(S.ItemWindowsErrorReports, null, c.Token, [Env.L("Microsoft", "Windows", "WER")]));
        return Sorted(cat);
    }

    static CleanupCategory Thumbnails(ScanContext c)
    {
        var cat = Catalog.Create("thumbnails");
        // Only the thumbnail and icon database files themselves, never the folder.
        Add(cat, Item(S.ItemThumbnails, null, c.Token,
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
        Add(cat, Item(S.ItemDownloadedUpdates, null, c.Token, [Env.W("SoftwareDistribution", "Download")]));
        Add(cat, Item(S.ItemDeliveryOptimization, null, c.Token,
            [Env.W("ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache")]));
        Add(cat, Item(S.ItemWindowsTemp, OneDay, c.Token, [Env.W("Temp")]));
        Add(cat, Item(S.ItemSystemCrashDumps, null, c.Token,
            [Env.W("Minidump"), Env.W("MEMORY.DMP"), Env.W("LiveKernelReports"), Env.PD("Microsoft", "Windows", "WER", "ReportArchive"), Env.PD("Microsoft", "Windows", "WER", "ReportQueue")]));
        return Sorted(cat);
    }

    static CleanupCategory Installers(ScanContext c)
    {
        var cat = Catalog.Create("installers");
        var cutoff = DateTime.Now.AddDays(-7);
        foreach (var f in c.DownloadFiles)
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
        foreach (var f in c.DownloadFiles)
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
        Add(cat, Report(S.ItemVirtualBox, detail, c.Token, [Path.Combine(Env.UserProfile, "VirtualBox VMs")]));
        Add(cat, Report(S.ItemVMware, detail, c.Token, [Path.Combine(Env.UserProfile, "Documents", "Virtual Machines")]));
        Add(cat, Report(S.ItemHyperV, detail, c.Token,
            [Path.Combine(Env.SystemRoot, "Users", "Public", "Documents", "Hyper-V", "Virtual Hard Disks")]));
        // WSL2 distro disks, one item per distro.
        foreach (var dir in Subdirs(Env.L("Packages")))
        {
            if (!Path.GetFileName(dir).StartsWith("CanonicalGroupLimited", StringComparison.OrdinalIgnoreCase)) continue;
            var vhdx = Path.Combine(dir, "LocalState", "ext4.vhdx");
            if (File.Exists(vhdx)) Add(cat, Report(Path.GetFileName(dir), detail, c.Token, [vhdx]));
        }
        foreach (var dir in Subdirs(Env.L("wsl")))
        {
            var vhdx = Path.Combine(dir, "ext4.vhdx");
            if (File.Exists(vhdx)) Add(cat, Report(Path.GetFileName(dir), detail, c.Token, [vhdx]));
        }
        Add(cat, Report(S.ItemDocker, detail, c.Token,
            [Env.L("Docker", "wsl", "disk", "docker_data.vhdx"), Env.L("Docker", "wsl", "data", "ext4.vhdx")]));
        Add(cat, Report(S.ItemBlueStacks, detail, c.Token, [Env.PD("BlueStacks_nxt", "Engine")]));
        var drive = Env.SystemRoot;
        Add(cat, Report(S.ItemLdPlayer, detail, c.Token,
            [Path.Combine(drive, "LDPlayer", "LDPlayer9", "vms"), Path.Combine(drive, "LDPlayer", "LDPlayer14", "vms"),
             Path.Combine(drive, "leidian", "LDPlayer9", "vms"), Path.Combine(drive, "leidian", "LDPlayer14", "vms")]));
        Add(cat, Report(S.ItemNox, detail, c.Token,
            [Path.Combine(Env.ProgramFiles, "Nox", "bin", "BignoxVMS"),
             Path.Combine(Env.ProgramFilesX86, "Nox", "bin", "BignoxVMS"),
             Env.R("Nox", "bin", "BignoxVMS")]));
        Add(cat, Report(S.ItemMemu, detail, c.Token,
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
            var (b, _) = FileOps.Measure(p, null, c.Token);
            if (b <= 0) continue;
            if (folder != "Windows.old") staging = true;
            cat.Items.Add(new CleanupItem { Name = folder, Paths = [p], Size = b });
        }
        if (cat.Items.Count == 0) return null;
        if (staging) cat.Note = S.WinOldStagingNote;
        return cat;
    }
}
