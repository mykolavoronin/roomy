# Changelog

## Unreleased

- **"Your call" items go to the Recycle Bin by default.** Old installers, big downloads and other files you're unsure about can now be restored if you change your mind. Caches and other safe stuff is still deleted directly, since apps rebuild it. Settings offers one choice for where cleaned files go: the default, everything to the Recycle Bin, or everything deleted permanently.
- **Clearer progress and results.** While scanning, Roomy shows the check in progress plus how many items and how much space it has found so far. While cleaning, it shows the file being removed plus files removed and space freed so far. The done screen now shows total files removed next to the space freed, a per-category breakdown, and what went to the Recycle Bin vs what was deleted for good.
- **Confirmation lists where everything goes.** Before cleaning, Roomy lists every selected category with whether it goes to the Recycle Bin or is deleted permanently.
- **More things found.** Thumbnail and icon cache; ChatGPT, Claude and Cursor caches (cache files only — chats, settings and history are never touched); Battle.net, EA app, GOG Galaxy and Ubisoft Connect web caches; OBS logs, Adobe media cache, Visual Studio's component cache and the Chocolatey download cache; Android emulator snapshots and scratch cache; developer caches from Maven, Gradle, Cargo and Go (your call — your tools re-download them); and LiveKernelReports plus Windows upgrade staging folders alongside the existing checks.
- **Virtual machines and emulators, sizes only.** Roomy now reports how much space VirtualBox, VMware, Hyper-V, WSL2, Docker Desktop, BlueStacks, LDPlayer, Nox and MEmu disks take — but never deletes them. Virtual disks must be compacted or removed through their own tools, so each item opens its folder instead of offering a delete.
- **Windows space settings.** Settings now has a Windows section: turn hibernation on or off (with what it does to hiberfil.sys and Fast Startup explained, and the current file size shown), and clear the Delivery Optimization cache. Both go through Windows' own tools and are reversible.
- **Nightly builds.** Every push to main now publishes a fresh portable build to the "nightly" release, so the latest code is always downloadable without waiting for a versioned release.

## 0.1.4

- **Safer cleanup with junctions and symlinks.** Roomy now checks where every file really lives before deleting it, and skips anything reached through a junction, symlink or mount point anywhere in the path, not just inside the cleaned folder. The check and the delete use the same open file handle, so the path can't be swapped in between. Thanks to [@Anchoder](https://github.com/Anchoder) for the detailed report ([#4](https://github.com/krezio/roomy/issues/4)).
- **Clear message when Roomy can't start.** Instead of Windows' "unknown hard error", Roomy now says what went wrong and saves the details to `%LOCALAPPDATA%\Roomy\crash.log`, so problems can be reported and fixed. The file stays on your PC.

## 0.1.3

- **Russian translation.** Roomy now follows your Windows language and speaks Russian as well as English. Thanks to [@KVGeorgich](https://github.com/KVGeorgich) ([#2](https://github.com/krezio/roomy/pull/2), [#3](https://github.com/krezio/roomy/pull/3)).
- **Recycle Bin option.** In Settings, choose to send old installers and big downloads to the Recycle Bin instead of deleting them, so you can get them back. Caches are still deleted, since apps rebuild them.
- **More app caches.** Roomy now finds the Spotify, Epic Games Launcher and new Microsoft Teams caches. Spotify offline downloads are left alone.
- **Sharper look.** Badges and pill buttons no longer have distorted corners. Thanks to [@spoonkuh](https://github.com/spoonkuh) ([#1](https://github.com/krezio/roomy/pull/1)).
- The warning about Windows Update files now wraps on narrow windows.

## 0.1.2

- **Settings window.** Choose whether Roomy starts with Windows and how it checks in the background.
- **Low disk space alerts.** Optional background check that lets you know when a drive is running low.
- **Weekly cache check.** Optional reminder when at least 1 GB of temp files and app caches could be cleared. Nothing is deleted without you reviewing it.
- Only one copy of Roomy runs at a time; opening it again brings the existing window forward.
- The Settings window now shows the Roomy version.
- Clearer wording throughout the app.

## 0.1.1

- New graphite look for the app.
- Updated screenshots and demo.

## 0.1.0

- First release: scan, plain-English results, space map, biggest files and share card.
