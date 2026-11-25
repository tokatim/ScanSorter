using System.Diagnostics;
namespace ScanSorter.Ocr;

/// <summary>Times pipeline stages without retaining document text or images.</summary>
public sealed class OcrTimings
{
    private readonly Dictionary<string, StageTiming> _stages = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, StageTiming> Stages => _stages;
    public T Measure<T>(string stage, Func<T> action)
    {
        long start = Stopwatch.GetTimestamp();
        try { return action(); }
        finally
        {
            Record(stage, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }
    internal void Record(string stage, double milliseconds)
    {
        _stages.TryGetValue(stage, out var previous);
        _stages[stage] = new StageTiming((previous?.Milliseconds ?? 0) + milliseconds, (previous?.Calls ?? 0) + 1);
    }
    public string ToLogString() => string.Join("; ", _stages.Select(p =>
        FormattableString.Invariant($"{p.Key}={p.Value.Milliseconds:F0}ms/{p.Value.Calls}")));
}
public sealed record StageTiming(double Milliseconds, int Calls);
