# StallWatch

Finds what makes Windows stutter and says it plainly — e.g.
"Code.exe is forcing writes to disk ~200 times per second; C: took 1.4 s to answer".

Status: M1 (disk stalls + flush storms) works live and on recorded traces. See the plan for M2–M6.

```
stallwatch watch [--minutes N] [--table]   live, needs an administrator terminal
stallwatch analyze <file.etl> [--table]    offline, on a trace recorded with `record`
stallwatch record <file.etl> [--seconds N] record a trace (admin)
```

Positive control: `dotnet run --project tools/flushstorm -- <file> 200 20` forces 200 flushes/s for 20 s.
