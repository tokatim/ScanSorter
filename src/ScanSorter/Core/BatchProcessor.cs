using System.Collections.Concurrent;
using ScanSorter.Config;
using ScanSorter.Ocr;
using ScanSorter.Pdf;

namespace ScanSorter.Core;

public sealed record RunOptions(string OutputDir, int? ManualYear, bool NoVolume = false);

/// <summary>
/// Пакетная обработка: для каждого PDF читает реестровый номер и копирует файл под именем «год-реестр-номер.pdf»
/// в папку «Тип\Год». Содержимое PDF не меняется (исходник уже в PDF/A-1b). Сомнительные файлы не сохраняются, а помечаются «Нужна проверка».
/// Несколько файлов обрабатываются одновременно (по одному на каждое ядро распознавания).
/// </summary>
public sealed class BatchProcessor
{
    /// <summary>Сколько последних страниц просматривать при поиске номера.</summary>
    public const int PagesToSearch = 4;

    private readonly int _workers;
    public BatchProcessor(int workers) => _workers = Math.Max(1, workers);

    /// <summary>Обрабатывает файлы. Возвращает после последнего или после отмены (файлы в работе дорабатываются).</summary>
    public void Run(IReadOnlyList<BatchItem> items, RunOptions options, IProgress<int> progress, CancellationToken ct)
    {
        Licensing.LicenseManager.Require();
        var queue = new ConcurrentQueue<BatchItem>(items.Where(i => i.Status is not (ItemStatus.Done or ItemStatus.Skipped)));
        int done = items.Count - queue.Count;
        int workers = Math.Min(_workers, Math.Max(1, queue.Count));
        if (done > 0) progress.Report(done);

        // каждому потоку нужен свой движок распознавания (движок не потокобезопасен)
        var readers = new RegNumberReader?[workers];
        Parallel.For(0, workers, i => readers[i] = new RegNumberReader());
        try
        {
            var tasks = Enumerable.Range(0, workers).Select(w => Task.Run(() =>
            {
                var reader = readers[w]!;
                while (!ct.IsCancellationRequested && queue.TryDequeue(out var item))
                {
                    ProcessItem(item, reader, options, ct);
                    progress.Report(Interlocked.Increment(ref done));
                }
            })).ToArray();
            Task.WaitAll(tasks);
        }
        finally
        {
            foreach (var r in readers) r?.Dispose();
        }
    }

    private static void ProcessItem(BatchItem item, RegNumberReader reader, RunOptions options, CancellationToken ct)
    {
        item.Status = ItemStatus.Processing;
        item.Note = "";
        try
        {
            using var pdf = PdfSource.Open(item.SourcePath);
            item.PageCount = pdf.PageCount;
            if (pdf.PageCount == 0) { Fail(item, "в файле нет страниц"); return; }

            var found = reader.Find(pdf.PageCount, i => pdf.RenderJpeg(i), PagesToSearch, options.NoVolume, ct);
            item.Search = found;
            Log.Info($"{item.FileName}: найден={found.Best != null}, уверенно={found.Sure}, {found.Elapsed.TotalSeconds:0.0} с");

            if (found.Best == null) { NeedsReview(item, "номер не найден"); return; }
            if (!found.Sure) { NeedsReview(item, "номер распознан неуверенно"); return; }

            int? year = found.Best.Year ?? options.ManualYear;
            if (year == null) { NeedsReview(item, "в номере нет года (старый формат), укажите год"); return; }
            Save(item, new RegNumber(year, found.Best.Vol, found.Best.Num), options.OutputDir);
        }
        catch (OperationCanceledException) { item.Status = ItemStatus.Pending; }
        catch (Exception ex)
        {
            Log.Error("Обработка " + item.FileName, ex);
            Fail(item, "не удалось прочитать PDF (" + ex.Message + ")");
        }
    }

    // Имена свободных файлов выбираются и занимаются под замком: два потока не должны взять одно и то же имя.
    private static readonly object SaveLock = new();

    /// <summary>
    /// Копирует файл под новым именем в папку «Тип\Год» байт в байт: исходник уже в PDF/A-1b, поэтому содержимое не меняется.
    /// После копирования сверяется контрольная сумма. Исходный файл не трогается.
    /// </summary>
    public static void Save(BatchItem item, RegNumber number, string outputDir)
    {
        int year = number.Year!.Value;
        string baseName = Naming.BaseName(year, number.Vol, number.Num);

        // файл уже лежит в нужной папке под нужным именем (повторный прогон по результатам): ничего не копируем
        var sourceFull = Path.GetFullPath(item.SourcePath);
        var expectedDir = Path.GetFullPath(Naming.FolderFor(outputDir, item.DocType, year));
        if (string.Equals(Path.GetDirectoryName(sourceFull), expectedDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileNameWithoutExtension(sourceFull), baseName, StringComparison.OrdinalIgnoreCase))
        {
            item.TargetPath = sourceFull; item.Status = ItemStatus.Done; item.Note = "уже лежит на месте";
            return;
        }

        string target; bool existed;
        lock (SaveLock)
        {
            existed = Naming.Exists(outputDir, item.DocType, year, number.Vol, number.Num);
            target = Naming.FreePath(outputDir, item.DocType, year, number.Vol, number.Num);
            File.Create(target).Dispose();      // занимаем имя
        }
        try
        {
            File.Copy(item.SourcePath, target, overwrite: true);
            if (!SameContent(item.SourcePath, target)) throw new IOException("копия не совпала с исходным файлом");
        }
        catch
        {
            try { if (File.Exists(target)) File.Delete(target); } catch { }
            throw;
        }

        var notes = new List<string>();
        if (existed) { notes.Add($"повтор номера: сохранён как {Path.GetFileName(target)}"); item.IsDuplicate = true; }
        if (!PdfaMark.IsDeclaredPdfA1b(target)) notes.Add("в файле не найдена отметка PDF/A-1b");
        item.TargetPath = target;
        item.Note = string.Join("; ", notes);
        item.Status = ItemStatus.Done;
        Log.Info($"{item.FileName} -> {Path.GetFileName(target)}");
    }

    private static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var sa = File.OpenRead(a); using var sb = File.OpenRead(b);
        return System.Security.Cryptography.SHA256.HashData(sa).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(sb));
    }

    private static void NeedsReview(BatchItem item, string reason) { item.Note = reason; item.Status = ItemStatus.NeedsReview; }
    private static void Fail(BatchItem item, string reason) { item.Note = reason; item.Status = ItemStatus.Error; }
}
