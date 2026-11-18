namespace PurgeWindows.Core;

/// <summary>What the deletion safety policy decided about one path.</summary>
public enum PolicyVerdict
{
    /// <summary>The path may be cleaned (subject to the tier/confirmation rules).</summary>
    Allow,
    /// <summary>The path must never be deleted, by any route. It never appears in
    /// the catalog or the allowlist, and reaching it is a bug, not a choice.</summary>
    BlockedNeverDelete,
    /// <summary>The path is not on the allowlist. Unknowns never surface and are
    /// never cleaned.</summary>
    BlockedNotWhitelisted,
}

/// <summary>One policy decision, with the step that made it (for the clean log and tests).</summary>
public sealed record PolicyDecision(PolicyVerdict Verdict, int Step, string Matched);

/// <summary>
/// The well-known roots the policy is built from. <see cref="FromEnvironment"/>
/// reads them from <see cref="Env"/>; tests construct explicit roots so no real
/// machine layout is involved.
/// </summary>
public sealed record PolicyRoots(
    string Profile, string Local, string Roaming, string ProgramData, string Windows, string SystemRoot,
    string Downloads, string Documents, string Desktop, string Pictures, string Videos, string Music,
    string Temp, string ProgramFiles, string ProgramFilesX86)
{
    public static PolicyRoots FromEnvironment() => new(
        Env.UserProfile, Env.Local, Env.Roaming, Env.ProgramData, Env.Windows, Env.SystemRoot,
        Env.Downloads,
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        Env.Temp, Env.ProgramFiles, Env.ProgramFilesX86);
}

/// <summary>
/// Live inputs to a policy decision. These are time-varying — the caller re-reads
/// them fresh for every run and every item — and they are NEVER memoized: the
/// policy keeps no cache at all, so every evaluation reads the world as it is now.
/// </summary>
public sealed record LiveContext(
    /// <summary>Roots the user excluded in Settings, read fresh from disk by the caller.</summary>
    IReadOnlyList<string> ExcludedRoots)
{
    public static LiveContext Empty { get; } = new([]);
}

/// <summary>
/// The fixed allowlist + never-touch policy, ported from Purge's
/// <c>DeletionSafetyPolicy.evaluate()</c>. Paths come from scanners + this
/// allowlist; unknowns never surface and are never cleaned.
/// <para/>
/// The 12-step decision order is preserved, with Windows meanings. The order
/// matters and the subtleties are deliberate:
/// <list type="number">
/// <item>Protected sets (exact roots, system prefixes, <c>.pst</c> files) → never-delete.
/// The <c>.pst</c> extension denial lives here, before the allowlist, because an
/// allowlisted folder (Outlook) can contain never-delete files — a deliberate,
/// strictly-safer deviation from Purge's step order.</item>
/// <item>JetBrains settings tree → never-delete.</item>
/// <item>Protected app-data paths (version-control, keys, editor state, whole
/// browser profiles) → never-delete.</item>
/// <item>AI model caches → never-delete.</item>
/// <item>Allowlist prefixes, file rules and segment rules → allow.</item>
/// <item>Shape rules (cache-leaf names, Steam cache segments) → allow. Deliberately
/// above the never-delete guards, as in Purge (its clang module-cache rule).</item>
/// <item>Never-delete prefixes → never-delete.</item>
/// <item>Never-delete exact paths (pagefile, hiberfil, swapfile) → never-delete.</item>
/// <item>Folder-name rule (inside the profile) → allow, unless the resolved path
/// lands inside a never-delete location.</item>
/// <item>Project-artifact rule → allow, unless it resolves into never-delete.</item>
/// <item>Agent worktree inside a project → allow.</item>
/// <item>Anything else → not allowlisted.</item>
/// </list>
/// An allowlisted path inside a protected root still cleans (step 5 before step 7:
/// e.g. <c>SoftwareDistribution\Download</c> cleans while <c>SoftwareDistribution</c>
/// as a whole does not). The folder-name and project rules run AFTER the guards.
/// <para/>
/// Symlink/junction resolution is NOT this policy's job: it works on normalized
/// lexical paths. <see cref="SafeFs"/> resolves the real location at delete time,
/// and the per-file re-check (<see cref="AllowsContained"/>) runs on real paths.
/// </summary>
public sealed class DeletionSafetyPolicy
{
    static DeletionSafetyPolicy? _default;
    public static DeletionSafetyPolicy Default => _default ??= new DeletionSafetyPolicy(PolicyRoots.FromEnvironment());

    readonly PolicyRoots _roots;
    readonly string[] _protectedExact;
    readonly string[] _protectedPrefixes;
    readonly string[] _neverDeletePrefixes;
    readonly string[] _allowlistPrefixes;
    readonly string[] _extraAllowlist;

    public DeletionSafetyPolicy(PolicyRoots roots, IEnumerable<string>? extraAllowlistPrefixes = null)
    {
        _roots = roots;
        _extraAllowlist = (extraAllowlistPrefixes ?? []).Select(Normalize).Where(s => s.Length > 0).ToArray();

        string W(params string[] p) => Join(roots.Windows, p);
        string L(params string[] p) => Join(roots.Local, p);
        string R(params string[] p) => Join(roots.Roaming, p);
        string PD(params string[] p) => Join(roots.ProgramData, p);
        string P(params string[] p) => Join(roots.Profile, p);
        string S(params string[] p) => Join(roots.SystemRoot, p);

        _protectedExact =
        [
            roots.SystemRoot, roots.Windows, roots.Profile,
            Path.GetDirectoryName(roots.Profile.TrimEnd('\\')) ?? "",
            roots.ProgramFiles, roots.ProgramFilesX86, roots.ProgramData,
            roots.Local, roots.Roaming, roots.Downloads, roots.Documents,
            roots.Desktop, roots.Pictures, roots.Videos, roots.Music,
        ].Select(Normalize).Where(s => s.Length > 0).ToArray();

        _protectedPrefixes =
        [
            W("System32"), W("SysWOW64"), W("Boot"),
            S("$Recycle.Bin"),
        ].Select(Normalize).Where(s => s.Length > 0).ToArray();

        _neverDeletePrefixes =
        [
            W("WinSxS"), W("Installer"), W("CSC"), W("SoftwareDistribution"),
            PD("Package Cache"), PD("Microsoft", "Windows", "WER"),
            S("MSOCache"), S("Config.Msi"), S("Recovery"),
        ].Select(Normalize).Where(s => s.Length > 0).ToArray();

        _allowlistPrefixes =
        [
            roots.Temp,
            L("Microsoft", "Windows", "INetCache"), L("Microsoft", "Feeds Cache"),
            L("CrashDumps"), L("Microsoft", "Windows", "WER"),
            W("SoftwareDistribution", "Download"),
            W("ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"),
            W("Temp"), W("Minidump"), W("MEMORY.DMP"), W("LiveKernelReports"),
            PD("Microsoft", "Windows", "WER", "ReportArchive"), PD("Microsoft", "Windows", "WER", "ReportQueue"),
            W("Panther"), S("inetpub", "logs", "LogFiles"), S("PerfLogs"),
            L("Microsoft", "Outlook"),
            L("Spotify", "Data"),
            L("Packages", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0", "LocalCache", "Spotify", "Data"),
            L("Steam", "htmlcache"),
            L("NuGet", "v3-cache"), L("NuGet", "plugins-cache"),
            L("pip", "Cache"), L("Yarn", "Cache"),
            L("npm-cache"), R("npm-cache"),
            P(".m2", "repository"),
            P(".gradle", "caches"), P(".gradle", "wrapper", "dists"),
            P(".cargo", "registry", "cache"), P(".cargo", "registry", "src"), P(".cargo", "git"),
            L("go-build"), P("go", "pkg", "mod"),
            P(".nuget", "packages"),
            L("Microsoft", "vscode-cpptools"),
            L("D3DSCache"), L("Microsoft", "D3DSCache"),
            L("NVIDIA", "DXCache"), L("NVIDIA", "GLCache"), L("NVIDIA", "ComputeCache"),
            L("NVIDIA Corporation", "NV_Cache"), PD("NVIDIA Corporation", "NV_Cache"),
            L("AMD", "DxCache"), L("AMD", "DxcCache"), L("AMD", "GLCache"), L("AMD", "VkCache"),
            L("Intel", "ShaderCache"),
            L("Battle.net"), L("Blizzard Entertainment", "Battle.net", "Cache"),
            PD("Battle.net"), PD("Blizzard Entertainment", "Battle.net", "Cache"),
            L("Electronic Arts", "EA Desktop", "Cache"),
            L("GOG.com", "Galaxy", "webcache"),
            L("Ubisoft Game Launcher", "cache"),
            R("obs-studio", "logs"), R("obs-studio", "crashes"), R("obs-studio", "plugin_config", "obs-browser", "Cache"),
            R("Adobe", "Common", "Media Cache Files"), R("Adobe", "Common", "Media Cache"), R("Adobe", "Common", "Peak Files"),
            PD("ChocolateyHttpCache"), P(".chocolatey", "http-cache"),
            R("Zoom", "data"), R("Zoom", "logs"), R("Signal", "logs"),
            L("Packages", "MSTeams_8wekyb3d8bbwe", "LocalCache", "Microsoft", "MSTeams", "Logs"),
            L("Packages", "MSTeams_8wekyb3d8bbwe", "LocalCache", "Microsoft", "MSTeams", "UserAvatarIcons"),
            R("Code", "logs"), R("Code - Insiders", "logs"),
            L("IconCache.db"),
            roots.Downloads,
            Join(roots.Videos, ["Captures"]),
        ].Select(Normalize).Where(s => s.Length > 0).ToArray();
    }

    static string Join(string root, string[] parts) =>
        root.Length == 0 ? "" : Path.Combine(new[] { root }.Concat(parts).ToArray());

    /// <summary>
    /// A policy that additionally allows <paramref name="roots"/> (and everything
    /// under them). Test/tooling seam: the production default never uses it.
    /// </summary>
    public static DeletionSafetyPolicy AllowRoots(params string[] roots) =>
        new(PolicyRoots.FromEnvironment(), roots);

    // ---------- normalization ----------

    static string Normalize(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            var p = path;
            if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
            if (p.StartsWith(@"\\", StringComparison.Ordinal)) return ""; // UNC: never trusted
            if (!Path.IsPathRooted(p)) return "";
            p = Path.GetFullPath(p);
            return p.Length > 3 ? p.TrimEnd('\\') : p;
        }
        catch { return ""; }
    }

    static bool IsSameOrInside(string path, string prefix) =>
        string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(prefix.EndsWith('\\') ? prefix : prefix + "\\", StringComparison.OrdinalIgnoreCase);

    static string[] Segments(string normalized) =>
        normalized.Split(['\\'], StringSplitOptions.RemoveEmptyEntries);

    // ---------- evaluation ----------

    /// <summary>
    /// Decides one path, fresh, every time. No verdict is ever memoized: time-varying
    /// refusals (exclusions, app state) are re-checked live by the caller passing a
    /// fresh <see cref="LiveContext"/> per run and per item.
    /// </summary>
    public PolicyDecision Evaluate(string path, LiveContext? live = null)
    {
        var n = Normalize(path);
        if (n.Length == 0)
            return new PolicyDecision(PolicyVerdict.BlockedNotWhitelisted, 12, "unparseable, relative, or UNC path");

        // Step 0 (live): user exclusions, read fresh by the caller. Never memoized.
        if (live is not null)
        {
            foreach (var excluded in live.ExcludedRoots)
            {
                var e = Normalize(excluded);
                if (e.Length > 0 && IsSameOrInside(n, e))
                    return new PolicyDecision(PolicyVerdict.BlockedNotWhitelisted, 0, "excluded in Settings: " + e);
            }
        }

        // Step 1: protected sets. Exact roots, system prefixes, and the .pst
        // extension denial (see the class doc for why .pst lives here).
        if (n.Length <= 3)
            return new PolicyDecision(PolicyVerdict.BlockedNeverDelete, 1, "drive root");
        foreach (var p in _protectedExact)
            if (string.Equals(n, p, StringComparison.OrdinalIgnoreCase))
                return new PolicyDecision(PolicyVerdict.BlockedNeverDelete, 1, "protected location: " + p);
        foreach (var p in _protectedPrefixes)
            if (IsSameOrInside(n, p))
                return new PolicyDecision(PolicyVerdict.BlockedNeverDelete, 1, "protected tree: " + p);
        if (string.Equals(Path.GetExtension(n), ".pst", StringComparison.OrdinalIgnoreCase))
            return new PolicyDecision(PolicyVerdict.BlockedNeverDelete, 1, ".pst files are never deleted");

        var segments = Segments(n);

        // Step 2: JetBrains settings tree.
        var jetBrains = Normalize(Join(_roots.Roaming, ["JetBrains"]));
        if (jetBrains.Length > 0 && IsSameOrInside(n, jetBrains))
            return new PolicyDecision(PolicyVerdict.BlockedNeverDelete, 2, "JetBrains settings tree");

        // Step 3: protected app-data paths.
        if (IsProtectedAppData(n, segments))
            return new PolicyDecision(PolicyVerdict.BlockedNeverDelete, 3, "protected app data");

        // Step 4: AI model caches (weights are downloads, not rebuildable caches).
        foreach (var model in new[] { Normalize(Join(_roots.Profile, [".cache", "huggingface"])), Normalize(Join(_roots.Profile, [".ollama"])) })
            if (model.Length > 0 && IsSameOrInside(n, model))
                return new PolicyDecision(PolicyVerdict.BlockedNeverDelete, 4, "AI model cache: " + model);

        // Step 5: allowlist prefixes, file rules, segment rules.
        foreach (var p in _allowlistPrefixes)
            if (IsSameOrInside(n, p))
                return new PolicyDecision(PolicyVerdict.Allow, 5, "allowlisted: " + p);
        foreach (var p in _extraAllowlist)
            if (IsSameOrInside(n, p))
                return new PolicyDecision(PolicyVerdict.Allow, 5, "allowlisted (extra root): " + p);
        if (IsAllowlistedFile(n, segments))
            return new PolicyDecision(PolicyVerdict.Allow, 5, "allowlisted file pattern");
        if (IsAllowlistedSegmentShape(n, segments))
            return new PolicyDecision(PolicyVerdict.Allow, 5, "allowlisted segment shape");

        // Step 6: shape rules — cache-leaf names and Steam cache segments.
        if (IsCacheLeafShape(n, segments))
            return new PolicyDecision(PolicyVerdict.Allow, 6, "cache-leaf shape");
        if (IsSteamCacheShape(segments))
            return new PolicyDecision(PolicyVerdict.Allow, 6, "Steam cache shape");

        // Step 7: never-delete prefixes.
        if (IsInNeverDeletePrefixes(n, segments))
            return new PolicyDecision(PolicyVerdict.BlockedNeverDelete, 7, "never-delete location");

        // Step 8: never-delete exact paths (paging/hibernation files at a drive root).
        if (segments.Length == 2)
        {
            var leaf = segments[1];
            if (leaf.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) ||
                leaf.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase) ||
                leaf.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase))
                return new PolicyDecision(PolicyVerdict.BlockedNeverDelete, 8, "system paging file");
        }

        // Steps 9–11: folder-name / project-artifact / worktree rules, guarded by
        // the never-delete sets (they run AFTER the guards, as in Purge).
        if (IsInProfile(n) && !IsInNeverDelete(n, segments))
        {
            var last = segments[^1];
            if (FolderNames.Contains(last, StringComparer.OrdinalIgnoreCase))
                return new PolicyDecision(PolicyVerdict.Allow, 9, "cache folder name: " + last);
            if (ProjectArtifacts.Contains(last, StringComparer.OrdinalIgnoreCase))
                return new PolicyDecision(PolicyVerdict.Allow, 10, "project artifact: " + last);
            if (segments.Contains(".worktrees", StringComparer.OrdinalIgnoreCase))
                return new PolicyDecision(PolicyVerdict.Allow, 11, "agent worktree in project");
        }

        // Step 12: anything else is not allowlisted.
        return new PolicyDecision(PolicyVerdict.BlockedNotWhitelisted, 12, "not on the allowlist");
    }

    /// <summary>
    /// The delete-time re-check for one file under a root the policy already
    /// allowed: the file must really be inside that root, and must not itself sit
    /// in a protected set (steps 1–4). This is the per-file fast path — it skips
    /// the full 12-step evaluation without weakening it, because containment in
    /// an allowlisted root plus the protected-set re-check is exactly what the
    /// full evaluation would conclude for a descendant (P1-1).
    /// </summary>
    public bool AllowsContained(string file, string allowedRoot)
    {
        var f = Normalize(file);
        var r = Normalize(allowedRoot);
        if (f.Length == 0 || r.Length == 0) return false;
        if (!IsSameOrInside(f, r)) return false;
        return !IsProtected(f, Segments(f));
    }

    /// <summary>
    /// Tool-resolved paths (npm/Go cache locations the tool itself reported): the
    /// trust comes from the tool's own answer plus containment in the profile,
    /// validated at resolution time by <c>Scanner.TrustedToolPath</c>. The path must
    /// still clear the protected sets (steps 1–4) and stay inside the profile.
    /// Only callers holding a genuinely tool-resolved path may use this.
    /// </summary>
    public PolicyDecision EvaluateToolResolved(string path)
    {
        var n = Normalize(path);
        if (n.Length == 0)
            return new PolicyDecision(PolicyVerdict.BlockedNotWhitelisted, 12, "unparseable, relative, or UNC path");
        if (IsProtected(n, Segments(n)))
            return new PolicyDecision(PolicyVerdict.BlockedNeverDelete, 1, "protected location");
        if (IsInProfile(n))
            return new PolicyDecision(PolicyVerdict.Allow, 5, "tool-resolved inside profile");
        return new PolicyDecision(PolicyVerdict.BlockedNotWhitelisted, 12, "tool-resolved path outside profile");
    }

    // ---------- step helpers ----------

    bool IsInProfile(string normalized)
    {
        var profile = Normalize(_roots.Profile);
        return profile.Length > 0 && IsSameOrInside(normalized, profile);
    }

    /// <summary>True when steps 1–4 block the path (the protected sets).</summary>
    bool IsProtected(string n, string[] segments)
    {
        if (n.Length <= 3) return true;
        foreach (var p in _protectedExact)
            if (string.Equals(n, p, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var p in _protectedPrefixes)
            if (IsSameOrInside(n, p)) return true;
        if (string.Equals(Path.GetExtension(n), ".pst", StringComparison.OrdinalIgnoreCase)) return true;
        var jetBrains = Normalize(Join(_roots.Roaming, ["JetBrains"]));
        if (jetBrains.Length > 0 && IsSameOrInside(n, jetBrains)) return true;
        if (IsProtectedAppData(n, segments)) return true;
        foreach (var model in new[] { Normalize(Join(_roots.Profile, [".cache", "huggingface"])), Normalize(Join(_roots.Profile, [".ollama"])) })
            if (model.Length > 0 && IsSameOrInside(n, model)) return true;
        return false;
    }

    bool IsProtectedAppData(string n, string[] segments)
    {
        // Version control, keys, and editor state: any segment with these names.
        if (segments.Contains(".git", StringComparer.OrdinalIgnoreCase)) return true;
        if (segments.Contains(".ssh", StringComparer.OrdinalIgnoreCase)) return true;
        if (segments.Contains(".gnupg", StringComparer.OrdinalIgnoreCase)) return true;
        // Editor state databases and user registry hives, by file name.
        var file = segments[^1];
        if (file.Equals("state.vscdb", StringComparison.OrdinalIgnoreCase)) return true;
        if (file.StartsWith("NTUSER.DAT", StringComparison.OrdinalIgnoreCase)) return true;
        if (file.StartsWith("UsrClass.dat", StringComparison.OrdinalIgnoreCase)) return true;
        // A whole Chromium profile directory (its cache leaves are allowlisted
        // individually; the profile itself — history, cookies, logins — never is).
        if (segments.Length >= 2 && segments[^2].Equals("User Data", StringComparison.OrdinalIgnoreCase) &&
            IsProfileDirName(file)) return true;
        return false;
    }

    static bool IsProfileDirName(string name) =>
        name.Equals("Default", StringComparison.Ordinal) ||
        name.Equals("Guest Profile", StringComparison.Ordinal) ||
        name.StartsWith("Profile ", StringComparison.Ordinal);

    bool IsAllowlistedFile(string n, string[] segments)
    {
        // Thumbnail/icon database files only — never the Explorer folder itself.
        var explorer = Normalize(Join(_roots.Local, ["Microsoft", "Windows", "Explorer"]));
        if (explorer.Length == 0) return false;
        var parent = Normalize(Path.GetDirectoryName(n) ?? "");
        if (!string.Equals(parent, explorer, StringComparison.OrdinalIgnoreCase)) return false;
        var file = segments[^1];
        return (file.StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase) ||
                file.StartsWith("iconcache_", StringComparison.OrdinalIgnoreCase)) &&
               file.EndsWith(".db", StringComparison.OrdinalIgnoreCase);
    }

    bool IsAllowlistedSegmentShape(string n, string[] segments)
    {
        // WhatsApp media: the package name varies by build, so it is matched by
        // segment content, never hardcoded in full. Only the transfers leaf.
        var packages = Normalize(Join(_roots.Local, ["Packages"]));
        if (packages.Length > 0 && IsSameOrInside(n, packages))
        {
            for (int i = 0; i + 3 < segments.Length; i++)
            {
                if (segments[i].Contains("WhatsAppDesktop", StringComparison.OrdinalIgnoreCase) &&
                    segments[i + 1].Equals("LocalState", StringComparison.OrdinalIgnoreCase) &&
                    segments[i + 2].Equals("shared", StringComparison.OrdinalIgnoreCase) &&
                    segments[i + 3].Equals("transfers", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        // Android emulator: only Quick Boot snapshots and the scratch cache image
        // inside a *.avd folder. User data, SD images and the debug keystore stay out.
        var avdRoot = Normalize(Join(_roots.Profile, [".android", "avd"]));
        if (avdRoot.Length > 0 && IsSameOrInside(n, avdRoot))
        {
            var rest = n[avdRoot.Length..].Split(['\\'], StringSplitOptions.RemoveEmptyEntries);
            if (rest.Length >= 2 && rest[0].EndsWith(".avd", StringComparison.OrdinalIgnoreCase) &&
                (rest[1].Equals("snapshots", StringComparison.OrdinalIgnoreCase) ||
                 rest[1].Equals("cache.img", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        // Visual Studio component-model cache, per installed version.
        var vsRoot = Normalize(Join(_roots.Local, ["Microsoft", "VisualStudio"]));
        if (vsRoot.Length > 0 && IsSameOrInside(n, vsRoot))
        {
            var rest = n[vsRoot.Length..].Split(['\\'], StringSplitOptions.RemoveEmptyEntries);
            if (rest.Length >= 2 && rest[1].Equals("ComponentModelCache", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        // Epic Games Launcher web caches: webcache* folders under Saved.
        var epicSaved = Normalize(Join(_roots.Local, ["EpicGamesLauncher", "Saved"]));
        if (epicSaved.Length > 0 && IsSameOrInside(n, epicSaved))
        {
            var rest = n[epicSaved.Length..].Split(['\\'], StringSplitOptions.RemoveEmptyEntries);
            if (rest.Length >= 1 && rest[0].StartsWith("webcache", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static readonly HashSet<string> CacheLeaves = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache",
        "GraphiteDawnCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache",
        "Cache_Data", "cache2", "startupCache", "shader-cache",
        "CachedData", "CachedExtensionVSIXs", "Crashpad",
    };

    static readonly HashSet<string> NpmLeaves = new(StringComparer.OrdinalIgnoreCase)
    {
        "_cacache", "_npx", "_logs",
    };

    bool IsCacheLeafShape(string n, string[] segments)
    {
        var leaf = segments[^1];
        if (CacheLeaves.Contains(leaf))
        {
            // Inside Local or Roaming, at least an app folder deep: %LOCALAPPDATA%\Cache
            // alone does not pass, but %LOCALAPPDATA%\SomeApp\Cache does.
            foreach (var root in new[] { Normalize(_roots.Local), Normalize(_roots.Roaming) })
            {
                if (root.Length == 0 || !IsSameOrInside(n, root)) continue;
                var rest = n[root.Length..].Split(['\\'], StringSplitOptions.RemoveEmptyEntries);
                if (rest.Length >= 2) return true;
            }
            return false;
        }
        // npm's leaves may sit under a tool-resolved root anywhere in the profile.
        if (NpmLeaves.Contains(leaf)) return IsInProfile(n);
        return false;
    }

    static bool IsSteamCacheShape(string[] segments)
    {
        var last = segments[^1];
        if (last.Equals("depotcache", StringComparison.OrdinalIgnoreCase)) return true;
        return last.Equals("shadercache", StringComparison.OrdinalIgnoreCase) &&
               segments.Length >= 2 && segments[^2].Equals("steamapps", StringComparison.OrdinalIgnoreCase);
    }

    bool IsInNeverDeletePrefixes(string n, string[] segments)
    {
        foreach (var p in _neverDeletePrefixes)
            if (IsSameOrInside(n, p)) return true;
        // System Volume Information on any drive.
        if (segments.Length >= 2 && segments[1].Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    bool IsInNeverDelete(string n, string[] segments) =>
        IsProtected(n, segments) || IsInNeverDeletePrefixes(n, segments);

    static readonly HashSet<string> FolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "__pycache__", ".cache", "Cache", "Caches", "caches",
    };

    static readonly HashSet<string> ProjectArtifacts = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "target", "bin", "obj", "build", "dist", ".next", "out",
    };
}
