# What this fork changes

A fork of [smasherprog/EqTool](https://github.com/smasherprog/EqTool) that makes
PigParse run properly on Linux under Wine. Upstream ships a `Linux` build
configuration, but nobody there runs Linux, so it is not tested or supported;
everything here is the result of getting it working and measuring why.

Branch: `fix/proton-overlay-stability`. Upstream `main` is unmodified.

**None of this changes behaviour on Windows.** The Linux-only pieces are inside
`#if LINUX` or the `Linux` build configuration.

---

## If you just want to run it

The environment matters more than this build does. A stock upstream build works
almost as well once the prefix is right; what this fork adds on top is mostly
the progress bar rendering. The prefix needs:

- a plain Wine prefix with **real .NET Framework 4.8** (`winetricks dotnet48`) —
  wine-mono's WPF leaks around 50 MB/min
- `winetricks corefonts` — real WPF aborts at startup with no usable font
- `DisableHWAcceleration` in the prefix registry — WPF's D3D9 path renders every
  window black under Wine
- Wine's **X11** driver, not Wayland — the Wayland driver has no system tray, and
  the tray menu is the only way into the application

---

## Bug fixes — proposed upstream

These are not Linux-specific. They are plain bugs that happen to be much easier
to hit under Wine, where timings differ. Each is open as its own pull request.

### Deadlock between TriggerTimerManager and the UI thread
`EQTool/Services/TriggerTimerManager.cs`

`LogParser` runs on a `System.Timers.Timer`, so trigger handling is on a thread
pool thread. `HandleTimerMatch` took `sync` and then called `DispatchUI`, which
blocks on the UI thread — while the UI thread's 250 ms `DispatcherTimer` was
waiting for `sync`. Neither could proceed and the application stopped
responding. The window widens with the number of active timers, so a busy raid
is the worst case.

Fixed by deciding under the lock and acting after releasing it, which is what
`Tick()` already did for its own output.

### Tray Exit could wedge the UI thread
`EQTool/Services/P99LoginMiddlemand/LoginMiddlemand.cs`

`OnExit` runs on the UI thread and called `thread.Join()` with no timeout. The
listener only re-checks `Running` after `connection_read()` returns, and on
error it replaces its own `Connection` — so the object `StopListening` disposed
need not be the one it is parked on, and the `Join` could never return. The tray
menu stayed drawn but nothing in it responded. The listener was also a
foreground thread.

Fixed with a two second timeout and `IsBackground`.

### Inventory upload mutated shared request headers
`EQTool/Services/InventoryWatcherService.cs`

`PostInventory` assigned to the shared `HttpClient`'s
`DefaultRequestHeaders.Authorization` before each post, while running as
fire-and-forget from burst-y `FileSystemWatcher` events. Two overlapping posts
could throw from the headers collection. Now set per request on an
`HttpRequestMessage`, as `UIFileSyncService` already does.

### RemoveTimerBarRow could remove the same row twice
`EQTool/UI/EventOverlay.xaml.cs`

Removing a row shifts everything below it up by one, with no check that the row
was still present. Both the restart path and the queued `Storyboard.Completed`
callback can remove the same row, shifting every row underneath one too far.
`RemoveChainRow` and `RemoveMessageRow` already guarded this way.

---

## Linux-only changes — will stay in this fork

### Progress bars render as disconnected blocks
`EQTool/App.xaml` — an implicit `ProgressBar` style

The OS-themed `ProgressBar` (Aero's animated glossy chunk overlay) draws as a
row of separate coloured squares under Wine rather than one smooth fill. This
replaces the template with two plain rectangles. It applies application-wide, so
it fixes the Triggers window and the overlay together.

This is cosmetic and specific to Wine's theming, so it is not something upstream
would want — it would change how the control looks on Windows.

### Forced software rendering
`EQTool/App.xaml.cs` (`#if LINUX`)

WPF's hardware path goes through Direct3D9, which fails pixel format negotiation
under Wine (`err:d3d:context_choose_pixel_format`) and renders every window
black. WPF's own registry switch does the same job prefix-wide, so this is
redundant on a properly configured prefix — but it keeps the build working in
one nobody has set up. `EQTOOL_SOFTWARE_RENDER=0` disables it.

### 64-bit Linux build
`EQTool/EQTool.csproj` — `Prefer32Bit=false` on `Linux|AnyCPU` only

A 32-bit process is capped near 2 GB of address space. While running on
wine-mono that ceiling was reached in well under an hour and the runtime aborted
mid-allocation. The underlying leak was wine-mono's, and is gone on real .NET,
so this is no longer load-bearing — but 64-bit is the right default. The Windows
configurations are untouched.

### Auto-update disabled
`EQTool/Services/UpdateService.cs` — `UpdateCheckDisabled = true`

Upstream's updater pulls the latest release and overwrites the install, which
would silently replace this build with a stock one on the next restart. Flip the
constant to restore normal behaviour.

### Build id in the tray menu
`EQTool/App.xaml.cs` + `.github/workflows/build-windows.yml`

The version entry read `Linux1.0.0.0` for every build ever made, so a running
instance could not be identified. CI now stamps the short commit onto the
assembly and the tray shows `Linux1.0.0.0 (0d6a363c)`. A local build reports
`(local)`.

### CI workflow
`.github/workflows/build-windows.yml`

Builds the `Linux` configuration on `windows-latest` with MSBuild and uploads an
`EQTool-Linux-build` artifact. There is no .NET SDK on the development machine,
so this is the compile check.

---

## Defensive changes — no known bug

Hygiene added while chasing a hang that turned out to be something else. Kept
because the behaviour they prevent is real, but no measurement ever showed them
mattering.

- **`HttpClient` timeouts.** Every client used the 100 s default; a stalled
  server could park a thread on a blocking `.Result` for over a minute and a
  half. All now have explicit timeouts. `UpdateService` gets its own client with
  a long one, since `Timeout` covers reading the body and a shared short timeout
  would abort a release download partway.
  *`App.xaml.cs`, `LoggingService.cs`, `UIFileSyncService.cs`,
  `InventoryWatcherService.cs`, `SettingsPlayer.xaml.cs`, `UpdateService.cs`*

- **Exception reporting no longer blocks.** `LogUnhandledException` awaited
  instead of blocking on `.Result`.
  *`App.xaml.cs`*

- **Player tracker timer cannot overlap itself.** Its 20 s `AutoReset` timer
  re-fired regardless of whether the previous tick's blocking HTTP calls had
  finished. It now stops for the duration of each tick.
  *`PlayerTrackerService.cs`*

---

## Things tried and reverted

Recorded so nobody repeats them.

**A rewrite of the overlay's timer bars** replaced `Storyboard` animation with a
`DispatcherTimer`, the `ProgressBar` with a `Border`/`Grid`, and the countdown
text's `DropShadowEffect` with four offset `TextBlock`s — on the theory that
continuous composition and pixel shaders were destabilising the renderer. None
of it was necessary. The crashes it targeted were 32-bit address space
exhaustion and the deadlock above, and the memory growth was wine-mono leaking
natively. On real .NET the stock overlay animates smoothly and stays stable, so
it was reverted (`0d6a363c`) — 154 lines of divergence removed. Only the
row-removal guard was kept.

**Temporary leak instrumentation** sampled every collection the application owns
against RSS every 30 seconds. It answered the question by a negative result —
everything flat while RSS climbed 856 MB — which placed the leak outside this
code. Removed once that was established (`1718976d`).
