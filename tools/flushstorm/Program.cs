// Positive control for StallWatch: forces many small flushes to one file, like an SQLite database
// with synchronous=FULL. Usage: flushstorm <file> [perSecond=200] [seconds=20]
var path = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "stallwatch-flushstorm.bin");
var perSecond = args.Length > 1 ? int.Parse(args[1]) : 200;
var seconds = args.Length > 2 ? int.Parse(args[2]) : 20;
var block = new byte[4096];
using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.None);
var sw = System.Diagnostics.Stopwatch.StartNew();
long n = 0;
while (sw.Elapsed.TotalSeconds < seconds)
{
    fs.Position = (n % 256) * block.Length;
    fs.Write(block);
    fs.Flush(flushToDisk: true); // FlushFileBuffers
    n++;
    var due = TimeSpan.FromSeconds(n / (double)perSecond);
    if (due > sw.Elapsed) Thread.Sleep(due - sw.Elapsed);
}
Console.WriteLine($"{n} flushes in {sw.Elapsed.TotalSeconds:F1} s to {path}");
