// Measures how long a drive takes to answer a flush: write 4 KB, FlushFileBuffers, time it, repeat.
// The same test on several drives shows whether slow flushes come from one drive or from the load on it.
// Usage: flushlatency <folder> [seconds=10] [perSecond=50]   (one flush every 1/perSecond s; 0 = back to back)
using System.Diagnostics;
using System.Globalization;

var folder = args.Length > 0 ? args[0] : Path.GetTempPath();
var seconds = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 10;
var perSecond = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 50;
var path = Path.Combine(folder, $"flushlatency-{Environment.ProcessId}.tmp");
var block = new byte[4096];
Random.Shared.NextBytes(block);
var times = new List<double>();
try
{
    using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.None);
    var total = Stopwatch.StartNew();
    var each = Stopwatch.StartNew();
    var interval = perSecond > 0 ? TimeSpan.FromSeconds(1 / perSecond) : TimeSpan.Zero;
    var next = TimeSpan.Zero;
    while (total.Elapsed.TotalSeconds < seconds)
    {
        if (file.Position > 64 << 20) file.Position = 0; // keep the file small
        file.Write(block);
        each.Restart();
        file.Flush(flushToDisk: true); // FlushFileBuffers: the drive must commit its cache
        times.Add(each.Elapsed.TotalMilliseconds);
        next += interval;
        var wait = next - total.Elapsed;
        if (wait > TimeSpan.Zero) Thread.Sleep(wait);
    }
}
finally { File.Delete(path); }

times.Sort();
double P(double q) => times[Math.Min(times.Count - 1, (int)(times.Count * q))];
var slow = times.Count(t => t >= 50);
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"{folder,-6} {times.Count,5} flushes  median {P(0.5),6:F2} ms  p95 {P(0.95),7:F2}  p99 {P(0.99),7:F2}  max {times[^1],8:F1} ms  >=50 ms: {slow}"));
