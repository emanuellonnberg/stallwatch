# StallWatch

Finds what makes Windows stutter and says it plainly — e.g.
"Code.exe is forcing writes to disk ~200 times per second; C: took 1.4 s to answer".

Status: disk stalls + flush storms (M1), resource leaks (M2), app frame freezes with disk evidence (M3),
input delivery delay (M4), accessibility clients that make Chromium apps stall input (`a11y`), a saved
history with an HTML report (M5) and the tray app (M6) work.

Freezes and stutter come with a likely cause when one stands out: CPU, memory/paging, GPU load or full video
memory (always sampled, cheap), and, in **game mode** (the foreground app renders at 30+ fps), drivers holding a CPU
(DPC/ISR tracing) and the network (pings to the router and 1.1.1.1; lost or slow replies become their own finding).

Two ways to run it, same engine:
- **On demand**: `stallwatch watch` in an administrator terminal while you reproduce the problem; Ctrl+C to stop.
- **Resident**: `StallWatchTray.exe` (src/StallWatch.Tray; `.\install.ps1` builds it and the CLI into `%LOCALAPPDATA%\Programs\StallWatch`) puts a dot in the tray: green watching, red = found something
  in the last 2 minutes, yellow = not elevated (input + leaks only), grey = paused. Alerts are limited to one per kind
  per 5 minutes and one per minute overall; everything is saved. Double-click it (or a pop-up) for the findings window: a timeline per day where repeats fold into one row, details of the selected finding, and the programs behind most findings. Menu: Show findings, Open HTML report, Pause, Start with Windows
  (a logon task with highest privileges, so no UAC prompt at each login), Exit.

```
stallwatch watch [--app X.exe] [--minutes N] [--table]   live, needs an administrator terminal
stallwatch analyze <file.etl> [--app X.exe] [--table]    offline, on a trace recorded with `record`
stallwatch input [--minutes N] [--log f.csv]             mouse/keyboard delivery delay, no admin needed
stallwatch leaks [--minutes N]                           leaks only, no admin needed
stallwatch a11y [--seconds N]                            which programs read other windows' UI (names need admin)
stallwatch report [--days N] [--out f.html] [--no-open]  HTML report of saved findings
stallwatch record <file.etl> [--seconds N]               record a trace (admin)
```

`watch`, `input`, `leaks` and `a11y` save findings to `%LOCALAPPDATA%\StallWatch\stallwatch.db` (SQLite, written
in batches every 5 s with WAL and `synchronous=NORMAL`, so StallWatch never causes a flush storm itself).
`--no-store` turns that off; `STALLWATCH_DATA` points it at another folder.

Positive controls:
- `dotnet run --project tools/flushstorm -- <file> 200 20` forces 200 flushes/s for 20 s.
- `tools/framestall` presents ~120 fps in a D3D11 window and stops for 500 ms every 10 s
  (`framestall [stallMs] [everySeconds] [seconds]`); watch it with `--app framestall.exe`.
- `tools/slowhook [delayMs] [everyNth] [seconds]` installs a slow low-level mouse/keyboard hook (input delay).
- `tools/a11yclient [seconds]` reads the UI of every window that raises focus events (an accessibility client).
- `tools/wineventprobe [count]` times `NotifyWinEvent`, i.e. what WinEvent listeners cost every UI app.
- `tools/inputprobe` installs its own hooks: a spike tool, never a measurement.

## License

MIT, see [LICENSE](LICENSE).
