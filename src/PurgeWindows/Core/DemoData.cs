namespace PurgeWindows.Core;

/// <summary>
/// Made-up but realistic data for `--demo` and `--shots`, so screenshots and videos
/// never show anyone's real files.
/// </summary>
public static class DemoData
{
    const long MB = 1024L * 1024, GB = MB * 1024;
    static long Gb(double v) => (long)(v * GB);
    static long Mb(double v) => (long)(v * MB);

    public static readonly (string Root, string Label, long Total, long Free)[] Drives =
    [
        (@"C:\", "", Gb(237.9), Gb(9.6)),
        (@"D:\", "Games", Gb(931.5), Gb(212.4)),
    ];

    static CleanupItem I(string name, long size, string detail, bool file = false, int files = 0) => new()
    {
        Name = name, Paths = [$@"C:\Users\you\demo\{name}"], Size = size, Detail = detail, IsFile = file, FileCount = files,
    };

    public static CleanupCategory? Category(string id)
    {
        var c = Catalog.Create(id);
        void A(string name, long size, string detail, bool file = false, int files = 0) => c.Items.Add(I(name, size, detail, file, files));
        switch (id)
        {
            case "recycle":
                A("Recycle Bin", Gb(3.21), "214 items", files: 214);
                break;
            case "temp":
                A("Your temp folder", Gb(4.62), "18,402 files", files: 18402);
                break;
            case "browser":
                A("Google Chrome", Gb(2.08), "9,311 files", files: 9311);
                A("Microsoft Edge", Mb(712), "3,904 files", files: 3904);
                A("Firefox", Mb(388), "2,117 files", files: 2117);
                break;
            case "apps":
                A("npm cache", Gb(2.26), "41,870 files", files: 41870);
                A("Discord", Gb(1.38), "6,220 files", files: 6220);
                A("VS Code", Mb(864), "2,951 files", files: 2951);
                A("Steam's web browser", Mb(412), "1,480 files", files: 1480);
                A("Slack", Mb(296), "1,022 files", files: 1022);
                break;
            case "shaders":
                A("Steam games", Gb(3.62), "12,094 files", files: 12094);
                A("NVIDIA", Gb(1.91), "4,388 files", files: 4388);
                A("DirectX", Mb(742), "1,203 files", files: 1203);
                break;
            case "crash":
                A("App crash dumps", Gb(1.12), "37 files", files: 37);
                A("Windows error reports", Mb(286), "412 files", files: 412);
                break;
            case "windows":
                A("Downloaded updates", Gb(6.31), "1,904 files", files: 1904);
                A("Delivery Optimization", Gb(1.82), "96 files", files: 96);
                A("Windows temp folder", Mb(524), "3,311 files", files: 3311);
                break;
            case "installers":
                A("Blender-4.3-windows-x64.msi", Mb(341), "Downloaded 4 months ago", true, 1);
                A("OBS-Studio-31.0-Windows-Installer.exe", Mb(152), "Downloaded 3 months ago", true, 1);
                A("NVIDIA_App_v11.0.exe", Mb(148), "Downloaded 5 weeks ago", true, 1);
                A("UnityHubSetup.exe", Mb(127), "Downloaded 7 months ago", true, 1);
                A("DiscordSetup.exe", Mb(112), "Downloaded a year ago", true, 1);
                A("VSCodeUserSetup-x64.exe", Mb(98), "Downloaded 2 months ago", true, 1);
                A("python-3.13.1-amd64.exe", Mb(27), "Downloaded 6 months ago", true, 1);
                A("node-v22.11.0-x64.msi", Mb(29), "Downloaded 4 months ago", true, 1);
                A("SteamSetup.exe", Mb(2.3), "Downloaded a year ago", true, 1);
                break;
            case "downloads":
                A("Windows11_InsiderPreview.iso", Gb(6.12), "Untouched for 8 months", true, 1);
                A("Recording 2026-03-14.mkv", Gb(3.24), "Untouched for 7 months", true, 1);
                A("final-edit-v3-export.mp4", Gb(2.41), "Untouched for 2 months", true, 1);
                A("dataset-full.zip", Gb(1.83), "Untouched for 5 months", true, 1);
                break;
            case "games":
                c.Items.Add(new CleanupItem { Name = "Iron Frontier", Paths = [@"D:\Steam\steamapps\common\Iron Frontier"], Size = Gb(118.6), AppId = "0", Detail = "Last played a year ago" });
                c.Items.Add(new CleanupItem { Name = "Neon Drift", Paths = [@"D:\Steam\steamapps\common\Neon Drift"], Size = Gb(71.2), AppId = "0", Detail = "Last played 7 months ago" });
                c.Items.Add(new CleanupItem { Name = "Starfall Odyssey", Paths = [@"D:\Steam\steamapps\common\Starfall Odyssey"], Size = Gb(43.9), AppId = "0", Detail = "Last played 4 months ago" });
                c.Items.Add(new CleanupItem { Name = "Pixel Kingdoms", Paths = [@"D:\Steam\steamapps\common\Pixel Kingdoms"], Size = Gb(3.4), AppId = "0", Detail = "Never played" });
                break;
            case "winold":
                A("Windows.old", Gb(21.4), "");
                break;
            case "thumbnails":
                A("Thumbnail and icon cache", Mb(184), "1,204 files", files: 1204);
                break;
            case "avd":
                A("Pixel_9_API_35", Gb(2.41), "312 files", files: 312);
                A("Medium_Phone_API_34", Gb(1.08), "188 files", files: 188);
                break;
            case "devcaches":
                A("Maven local repository", Gb(3.82), "18,204 files", files: 18204);
                A("Gradle caches", Gb(2.24), "9,431 files", files: 9431);
                A("Cargo registry cache", Gb(1.41), "3,872 files", files: 3872);
                A("Go build & module cache", Mb(986), "5,118 files", files: 5118);
                break;
            case "virtual":
                A("VirtualBox VMs", Gb(48.2), "Compact or remove through VirtualBox");
                A("VMware VMs", Gb(32.8), "Compact or remove through VMware");
                A("WSL2: Ubuntu-24.04", Gb(18.6), "Compact with wsl --manage --compact");
                A("Docker Desktop disk", Gb(12.4), "Prune, then compact through Docker");
                break;
            case "diagnostics":
                A("Windows servicing logs", Gb(4.18), "Stop the service first, then delete the contents");
                A("Diagnostic trace logs", Gb(1.36), "Delete in Settings → Diagnostics & feedback");
                A("Windows event logs", Mb(214), "Clear in Event Viewer, keeping the Security log");
                break;
            case "winlogs":
                A("Windows setup logs", Mb(486), "1,204 files", files: 1204);
                A("Web server (IIS) logs", Mb(96), "312 files", files: 312);
                break;
            case "outlook":
                A("mailbox.ost", Gb(2.84), "", true, 1);
                break;
            case "messaging":
                A("WhatsApp media", Gb(3.12), "1,847 files", files: 1847);
                break;
            case "captures":
                A("ranked-clutch-4k.mp4", Gb(2.8), "Captured 3 months ago", true, 1);
                A("boss-fight-no-damage.mp4", Gb(1.9), "Captured 5 months ago", true, 1);
                A("Screenshot 2026-09-02.png", Mb(4.2), "Captured 2 months ago", true, 1);
                break;
        }
        return c;
    }

    // ---------- space map ----------

    sealed record N(string Name, double Gb, params N[] Kids);

    static readonly N Tree = new("C:", 0,
        new N("Users", 88.6,
            new N("you", 88.1,
                new N("AppData", 37.9,
                    new N("Local", 29.0, new N("Temp", 4.6), new N("npm-cache", 2.3), new N("Google", 3.1), new N("Packages", 6.4), new N("Microsoft", 5.2), new N("Docker", 6.8)),
                    new N("Roaming", 8.2, new N("discord", 1.6), new N("Code", 1.9), new N("Spotify", 3.1)),
                    new N("LocalLow", 0.7)),
                new N("Downloads", 22.7),
                new N("Videos", 14.2),
                new N("Documents", 6.3),
                new N("Pictures", 4.1),
                new N("Desktop", 1.9),
                new N("OneDrive", 0.8)),
            new N("Public", 0.5)),
        new N("Program Files (x86)", 52.4,
            new N("Steam", 47.8),
            new N("Microsoft", 3.1),
            new N("Epic Games", 1.2)),
        new N("Windows", 28.9,
            new N("WinSxS", 9.6), new N("SoftwareDistribution", 6.4), new N("System32", 6.0), new N("Installer", 3.1), new N("SysWOW64", 1.6)),
        new N("Windows.old", 21.4),
        new N("Program Files", 15.2,
            new N("Adobe", 5.4), new N("NVIDIA Corporation", 2.6), new N("Microsoft Office", 3.9), new N("Docker", 1.8)),
        new N("ProgramData", 5.1),
        new N("$Recycle.Bin", 3.2));

    const double RootFilesGb = 12.0; // pagefile.sys, hiberfil.sys…

    public static SpaceNode SpaceTree() => Build(Tree, null, @"C:\");

    static SpaceNode Build(N n, SpaceNode? parent, string path)
    {
        var node = new SpaceNode { Name = n.Name, FullPath = path, Parent = parent };
        var kids = n.Kids.Select(k => Build(k, node, Path.Combine(path, k.Name))).OrderByDescending(k => k.Size).ToList();
        node.Children = kids;
        long kidBytes = kids.Sum(k => k.Size);
        long own = parent is null ? Gb(RootFilesGb) : Math.Max(0, Gb(n.Gb) - kidBytes);
        node.FileBytes = own;
        node.FileCount = own > 0 ? (int)(own / (3 * MB)) + 1 : 0;
        node.Size = own + kidBytes;
        return node;
    }

    public static IReadOnlyList<BigFile> BigFiles() =>
    [
        new(@"C:\pagefile.sys", Gb(8.0)),
        new(@"C:\Users\you\Downloads\Windows11_InsiderPreview.iso", Gb(6.12)),
        new(@"C:\Users\you\AppData\Local\Docker\wsl\disk\docker_data.vhdx", Gb(5.9)),
        new(@"C:\hiberfil.sys", Gb(3.9)),
        new(@"C:\Users\you\Downloads\Recording 2026-03-14.mkv", Gb(3.24)),
        new(@"C:\Users\you\Videos\Captures\ranked-clutch-4k.mp4", Gb(2.8)),
        new(@"C:\Users\you\Downloads\final-edit-v3-export.mp4", Gb(2.41)),
        new(@"C:\Program Files (x86)\Steam\steamapps\common\Neon Drift\data0.pak", Gb(2.2)),
        new(@"C:\Users\you\Downloads\dataset-full.zip", Gb(1.83)),
        new(@"C:\Windows.old\Windows\System32\MRT.exe", Gb(1.1)),
        new(@"C:\Users\you\AppData\Roaming\Spotify\Data\cache.bnk", Gb(0.96)),
        new(@"C:\Users\you\Videos\vacation-2025.mov", Gb(0.91)),
    ];
}
