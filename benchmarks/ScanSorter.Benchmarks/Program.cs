using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Media.Imaging;
using ScanSorter.Ocr;
using ScanSorter.Pdf;

var arguments = args.Chunk(2).ToDictionary(x => x[0], x => x.Length == 2 ? x[1] : throw new ArgumentException("Expected --option value"));
string Required(string key) => arguments.TryGetValue(key, out var value) ? value : throw new ArgumentException($"Missing {key}");
string input = Path.GetFullPath(Required("--input"));
string output = Path.GetFullPath(Required("--output"));
int workers = arguments.TryGetValue("--workers", out var w) ? Math.Clamp(int.Parse(w), 1, 8) : 1;
bool noVolume = arguments.TryGetValue("--no-volume", out var nv) && bool.Parse(nv);
string[] files = arguments.TryGetValue("--selection", out var selection)
    ? File.ReadAllLines(selection).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Path.Combine(input, x)).ToArray()
    : Directory.GetFiles(input, "*.pdf").OrderBy(x => x).ToArray();
if (files.Length == 0) throw new ArgumentException("No PDF files selected");
Environment.SetEnvironmentVariable("OMP_THREAD_LIMIT", "1");
var sourceQueue = new ConcurrentQueue<(int index, string file)>(files.Select((file, index) => (index, file)));
var results = new ConcurrentBag<Measurement>();
var process = Process.GetCurrentProcess();
var cpuStart = process.TotalProcessorTime;
var batch = Stopwatch.StartNew();
int completed = 0;
var tasks = Enumerable.Range(0, Math.Min(workers, files.Length)).Select(_ => Task.Run(() =>
{
    using var reader = new RegNumberReader();
    while (sourceQueue.TryDequeue(out var item))
    {
        var fileClock = Stopwatch.StartNew();
        using var pdf = PdfSource.Open(item.file);
        var found = reader.Find(pdf.PageCount, i => pdf.RenderJpeg(i), 4, noVolume);
        results.Add(new Measurement(item.index, Path.GetFileName(item.file), pdf.PageCount,
            found.Best?.ToString(), found.Sure, found.PageIndex, fileClock.Elapsed.TotalMilliseconds,
            found.Timings.Stages, found.Reads.Count));
        Console.WriteLine($"Completed {Interlocked.Increment(ref completed)}/{files.Length}");
    }
})).ToArray();
await Task.WhenAll(tasks);
batch.Stop(); process.Refresh();
var ordered = results.OrderBy(x => x.Index).ToArray();
var report = new { Workers = workers, Files = ordered.Length,
    ElapsedMilliseconds = batch.Elapsed.TotalMilliseconds,
    CpuMilliseconds = (process.TotalProcessorTime - cpuStart).TotalMilliseconds,
    PeakWorkingSetBytes = process.PeakWorkingSet64,
    TotalAllocatedBytes = GC.GetTotalAllocatedBytes(),
    Automatic = ordered.Count(x => x.Sure), Records = ordered };
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Processed {ordered.Length} files in {batch.Elapsed.TotalSeconds:F2}s; automatic: {report.Automatic}");

record Measurement(int Index, string File, int Pages, string? Number, bool Sure, int PageIndex,
    double Milliseconds, IReadOnlyDictionary<string, StageTiming> Stages, int Reads);
