# HANDOFF — Purge for Windows build

**Status: PAUSED 18 Nov 2026 ~13:28 CET at Mykola's request (low on credits).
Waves 1–5 are complete and committed locally. Nothing has been pushed.
Wave 6 (Phase 4) was cancelled before writing anything — the tree is clean
at `a1d4ba5`, Phase 4 is NOT started. The next agent continues from here.**

Base: fork `mykolavoronin/roomy` at pushed commit `c95bffa7` (C#/WPF, .NET 10,
Windows 10/11 only). Remote is still `https://github.com/mykolavoronin/roomy` —
the repo has NOT been renamed on GitHub yet (that is Mykola's action).

## Completed waves (local commits, verified statically only)

- **Wave 1 — Rename (`f90b50a`)**: `src/Roomy/` → `src/PurgeWindows/`, tests
  renamed, `installer/purge-windows.iss`, namespaces, assembly metadata
  (Company/Authors "Mykola Voronin", Copyright "© 2026 Mykola Voronin"),
  identity sites, settings migration (copies old `%LOCALAPPDATA%\Roomy\`
  settings, never deletes), URI scheme `purge-windows://` registered (no route
  handling yet), strings regen stable (303 strings, 76 untranslated, no Russian
  invented), workflows renamed, `pages.yml` deleted, README rewritten with
  exact attribution, LICENSE keeps all three lines (krezio, Jithin Sabu,
  Mykola Voronin), CHANGELOG history kept. New toast CLSID; Inno AppId GUID
  kept. Env vars `PURGEWINDOWS_LANG`, `PURGEWINDOWS_TEST_CRASH`. Icon files
  renamed, same artwork — new icon deferred. `site/` untouched. Only
  intentional "Roomy" remnant: legacy migration path in Preferences.cs.
- **Wave 2 — Phase 0 hang fix (`64c5287`)**: C1 — `FOF_WANTNUKEWARNING`
  removed, flags `FOF_ALLOWUNDO|FOF_NOCONFIRMATION|FOF_SILENT|FOF_NOERRORUI|
  FOF_NOCONFIRMMKDIR`, hwnd set, pre-flight check + `RecycleBinBudget`,
  batches capped 200 files/256 MiB. C5/D-21 — `SHEmptyRecycleBin` +
  `BinEmptied` deleted; recycle entry ReportOnly. C2 — real CancellationToken
  end-to-end, Stop button, escapable close (5s bounded wait). C3/C4 —
  streaming via `FileSystemEnumerable`, heartbeat ≥250ms, §3.5 event contract.
  C6 — index slicing, zero `.Skip(` in FileOps. C7 — `ShellOperationResult`
  honored. C8 — `CleanWatchdog.cs` + `CleanLog.cs` (256 KiB rotation).
  Devcaches owner tools: only Go has real owner commands (`go clean -cache`,
  `go clean -modcache`); Gradle only non-destructive `--stop`; Maven/Cargo
  return null (no global owner command — inventing invocations judged unsafe).
  `IFileOperation` NOT implemented (COM interop unverifiable without Windows;
  documented follow-up in `Native.cs`). 19 strings added (322 total). 18
  hang-regression tests + progress/recycle tests.
- **Wave 3 — Phase 1 foundation + Round 3 merge (`945427b`)**: `SafetyLevel.cs`,
  `DeletionSafetyPolicy.cs` (12-step `Evaluate()`, live re-check),
  `CategoryCatalog.cs` + `explanations.windows.json` + `ExplanationResolver.cs`,
  `ScanQueue.cs` + `ScanCoalescer.cs`, `CleanFailure.cs` + `CleanOutcome.cs`,
  history/ledger stores, `VolumeCapacity.cs` (64 MiB noise floor; `MaxCapacity`
  unit believed megabytes — UNVERIFIED), `ConfirmGate.cs`,
  `Themes/PurgeTokens.xaml` (46 colors × light/dark), `tools/token-lint.mjs` +
  one `check.yml` edit. Catalog: **111 entries — 39 Safe / 59 CheckFirst /
  13 ReportOnly**, no `mode` field, 25 unverified R3 items out, never-delete
  out. Round 3 merged: 5 categories with correct tiers (diagnostics
  ReportOnly; winlogs/outlook/messaging/captures CheckFirst; `.ost` warnings
  kept; `.pst` never enumerated); new Teams partial (`Logs` +
  `UserAvatarIcons` kept, `tfl`/`tfw` dropped as opaque/unverified); npm/go
  tool-resolved paths kept. `RemovalMode` reduced to
  `{ReviewToBin, EverythingToBin}`. 372 strings, 145 untranslated. Carry-overs:
  `CanSurface` always true; token-lint grandfathers 5 legacy XAML files;
  `BackgroundMonitor`/`App.xaml.cs` still iterate `Scanner.Steps` directly
  (deliberate, later wave).
- **Wave 4 — Two-tier enforcement + ETA/stall + progress UI (`8d18dbb`)**:
  engine-level Check-First confirmation requirement (`CheckFirstConfirmation`,
  constructible only via confirmation dialog; `ValidateRun` refuses before any
  event/touch; `OwnerToolConsent` only from a consenting confirmation;
  Safe-only runs use `SafeTierOnly=true` with no dialog per Purge §9.3).
  `ProgressEstimator.cs` per §4.3/§4.4 (EWMA α=0.15, estimate gates,
  quantization, hysteresis, never fake numbers; 8 hand-traced worked examples
  asserted in tests). `StallDetector.cs` (10s/30s/120s thresholds, two clocks,
  heartbeat-only never escalates). Clean view: live figure + time line (2s
  fade-in) + stall line + per-part rows + Stop + 1.2s dwell; scan view: step/
  elapsed status, wired `PathProgress`, found-so-far counts, stall line, scan
  Stop button added, `UsageBar` removed (step fraction never presented as
  time); SpaceMapVM elapsed + factual-only stall line. `MeasureProgressEvent`
  + `ProgressOperation` (10 operations, names reserved for later waves). 27
  new strings (399 total). 4 new test files. Open items carried forward:
  `ScanProgressEvent` §4.6 common fields (assigned to Wave 6), space-map
  current-folder line (needs `SpaceScanner.cs`, Wave 7), scheduled-clean
  caller must set `SafeTierOnly`, full Reduce Motion wiring + `UsageBar` 0.3s
  timing (`Controls.cs`).
- **Wave 5 — Phase 3 environment detection + gating (`a1d4ba5`)**: 12 probes
  P-01–P-12 behind a fakeable data-source seam (all read-only, per-probe
  timeouts, 15s total budget per W5 §5.4; WSC primary + WMI
  `root\SecurityCenter2` fallback for AV, never on empty; WMI/WSC via
  late-bound COM + reflection since `System.Management` needs a package
  reference the app csproj can't take; only process execution is the `wsl.exe
  --list --quiet` allowlist; only two registry opens, both `writable: false`).
  Immutable snapshot with TTL caching (volatile 5 min, stable 60 min; Unknown
  never cached). Combination mod scoring with D-13 brand gating ("ReviOS" in
  exactly one UI string, only for score ≥4 with ≥1 strong marker; "Atlas"
  nowhere; overrides never create brand evidence); every marker traces to W5
  §5.3, none invented. `CategoryGate` implementing E-01–E-26 (subtractive
  only; Unknown never hides/downgrades/disables — overrides W5 P-06/P-10
  failure defaults; no rule currently produces `Downgraded`, mechanism exists
  and is tested). Async startup wiring in MainVM (never blocks first paint,
  never gates scanning; re-gates from retained raw results — known limitation:
  re-gating rebuilds `CategoryVM`s so selections are lost if it runs while
  items are selected). `EnvironmentVM` disclosure surface (summary, evidence,
  gating decisions, markers, override, Re-check) — shell wave only needs to
  bind it. One persisted mod-verdict override (`ModVerdictOverride` in
  Preferences). 107 new English-only strings (506 total). 6 test files (~120
  tests, all fakes). Partial: P-08 `HasPlaceholders` always null (needs a file
  scan no probe does), so **E-17 never fires**. Open: WinRT
  `PackageManager.FindPackages()`, late-bound COM, SCM P/Invoke layouts,
  `wsl.exe` decoding, and assumed mappings (DeviceGuard values, MediaType
  4=SSD/3=HDD, chassis laptop set, WSC signature values, package family
  prefixes, S1 locations, S4/S5 markers) all need live Windows verification.
  `EnvironmentVM` itself untested (no WPF reference in test project; its
  delegates are tested).

## Next: Wave 6 — Phase 4 core vertical slice (NOT STARTED)

Purge shell rebuild (sidebar + content, token-clean XAML), Overview page
(real totals, tier split, last-scan summary, bound Wave 5 disclosure panel),
App Caches + Dev Tools pages (full scan→select→confirm→clean→results slice,
catalog-driven, Wave 4 enforcement preserved with single construction sites
verified by grep), confirm sheets in Purge's pattern, celebration overlay
(real totals only, Reduce Motion respected for new animations), three new
stores (last-scan, exclusion enforced at engine level, override per plan
semantics with the safer reading on ambiguity), OFL font acquisition (full
Onest + Nunito from google/fonts or official repo only, OFL texts shipped,
system-font fallback if download fails after retries), About short-form
attribution (plan §9 exact wording), `purge-windows://` route handling,
`ScanProgressEvent` §4.6 common fields (Wave 4 open item), strings ownership,
store/route/navigation/contract tests.

## Remaining waves (in order)

- Wave 7: Phases 5+6+7 (program-aware, Large Files, space map).
- Wave 8: Phases 8+9 (uninstaller, orphans).
- Wave 9: Phases 10+11 (tray/scheduled/onboarding/keyboard,
  settings/about/history).
- Wave 10: Phase 12 recovery (prerequisite for Wave 11).
- Wave 11: Phase 13 registry (allowlist, backup-first, Apply enabled behind
  gates — see D-29 below).
- Wave 12: Phases 14+15 (more categories; elevation helper, watcher,
  hardening).
- Wave 13: final static verification + report.

## Binding decisions (SUPERSEDE the plan wherever they conflict)

1. **NO Basic/Advanced modes. NO modes infrastructure. NO hidden categories.**
   All categories visible in TWO TIERS: "Safe to Delete" and "Not Safe to
   Delete" (Check First). Check-First items REQUIRE a confirmation dialog.
   Tier rule: Safe iff Safe tier + plain Recycle Bin deletion + per-user + no
   elevation + no only-copy/sync risk + not a repair action; everything else
   Check-First. Former "Advanced-only" items (orphans, Large Files/
   Duplicates, registry) are Check-First: visible, confirmation-gated,
   nothing preselected where the plan says so.
2. **D-21: Recycle Bin emptying removed entirely** (done in Wave 2).
3. **D-29 (decided): registry Apply ENABLED** behind confirmation + verified
   backup gates (restore point + `.reg` export; HKLM requires a restore point,
   no exceptions), allowlist-only, no heuristic sweeping.
4. **D-30 (decided): Company/Authors "Mykola Voronin", Copyright
   "© 2026 Mykola Voronin".**
5. **All 30 decisions D-01–D-30 approved as recommended** in the plan
   (D-01 activation-sweep scheduled cleans, no Task Scheduler in v1; D-02
   on-demand elevated helper, never a SYSTEM service; D-24 slug
   `purge-windows`, exe `PurgeWindows.exe`, namespace `PurgeWindows`).
6. Quality bar: it must LOOK good (Purge visual fidelity) and WORK (safety
   discipline: Recycle Bin by default, re-check before acting, no silent
   destructive actions, no fake numbers).

## Source documents (on the original VM; ask Mykola for copies if elsewhere)

- Plan: `~/workspace/research_notes/purge-port-reference/PORT-PLAN.md`
  (1,299 lines) + `parts/W1–W6` + `W2-devcaches-addendum.md`. NOTE: the plan
  was written with modes — its modes sections (§7, D-19/D-20, "Advanced"
  placements) are SUPERSEDED by the two-tier revision above.
- Extraction: `~/workspace/research_notes/purge-port-reference/EXTRACTION.md`
  + `notes/01–04`.
- Purge source reference (read-only, commit 6e5c3d30):
  `~/workspace/purge-reference/` (sparse checkout, docs/ excluded).
- Round 3 research: `~/workspace/research_notes/disk-cleanup-round3-20261118/`
  (55 categories; 25 unverified items stay unimplemented).
- Original 42-category research:
  `~/workspace/research_notes/disk-cleanup-safe-vs-check-first-20261117-1931/`.

## Verification limits — read before claiming anything

This VM is Linux with no .NET/WPF toolchain. NOTHING here has been compiled,
run, or rendered. Every wave was verified statically only (grep, XAML
well-formedness, strings regeneration, hand-traced tests). NEVER claim a
build passes. The first real compile check is the GitHub nightly workflow
after a push. Standing unverified/deferred items: `MaxCapacity` registry
unit; IFileOperation STA implementation; new app icon (old Roomy artwork
under a new filename — do NOT reuse Purge's icon/trademark); new screenshots
(`docs/shots/*.png`, `docs/roomy-demo.gif` show old UI, unreferenced); Wave 5
assumed hardware/WMI mappings; Intel/AMD shader paths secondary/
community-reported; devcaches diagnostic questions to Mykola still
unanswered (hang fix implemented from source evidence regardless).

## Working rules

- Strings: regenerate via `node tools/strings.mjs`. NEVER invent Russian
  translations — new strings stay English-only until a human translates them
  (506 strings, 145+ untranslated at handoff).
- Do NOT modify `~/workspace/purge-reference/` (read-only reference).
- Do NOT push without Mykola's explicit go-ahead AND a fresh GitHub
  authorization (the previous gh device-flow credential was removed after the
  18 Nov push). Never store credentials.
- Merge discipline: disjoint file sets per phase, re-verify after each merge.
  If a phase cannot be completed soundly, complete what is sound, record the
  remainder precisely, and continue — never fake completion.

## Push readiness (Mykola's actions, still outstanding)

Rename repo to `purge-windows` in Settings; update description/website; turn
Pages off + empty custom domain; delete stale `Roomy-portable.exe` after
first renamed publish; remove his `purge-app` fork himself;
`git remote set-url` on local clones; never create a new repo named `roomy`
(kills redirects); check branch protection/secrets/integrations. Then a fresh
gh device-flow authorization for the push.
