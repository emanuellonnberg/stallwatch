# StallWatch

Finds what makes Windows stutter and says it plainly — e.g.
"Code.exe is forcing writes to disk ~200 times per second; C: took 1.4 s to answer".

Status: disk stalls + flush storms (M1), resource leaks (M2) and app frame freezes with disk evidence (M3)
work live and on recorded traces. Input delay, history/report and tray app are next.

```
stallwatch watch [--app X.exe] [--minutes N] [--table]   live, needs an administrator terminal
stallwatch analyze <file.etl> [--app X.exe] [--table]    offline, on a trace recorded with `record`
stallwatch leaks [--minutes N]                           leaks only, no admin needed
stallwatch record <file.etl> [--seconds N] record a trace (admin)
```

Positive controls:
- `dotnet run --project tools/flushstorm -- <file> 200 20` forces 200 flushes/s for 20 s.
- `tools/framestall` presents ~120 fps in a D3D11 window and stops for 500 ms every 10 s
  (`framestall [stallMs] [everySeconds] [seconds]`); watch it with `--app framestall.exe`.
