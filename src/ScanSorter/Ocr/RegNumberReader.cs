using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScanSorter.Config;
using ScanSorter.Pdf;
using Tesseract;

namespace ScanSorter.Ocr;

/// <summary>Одно чтение номера. Source начинается с «T:» (Tesseract) или «P:» (PaddleOCR).</summary>
public sealed record NumberRead(string Source, RegNumber Number)
{
    public bool IsTesseract => Source.StartsWith("T:", StringComparison.Ordinal);
    public bool IsPaddle => Source.StartsWith("P:", StringComparison.Ordinal);
}

/// <summary>Итог поиска номера в документе.</summary>
public sealed class NumberSearchResult
{
    public RegNumber? Best { get; init; }
    /// <summary>Два независимых движка прочитали один и тот же номер: его можно принять без вопроса оператору.</summary>
    public bool Sure { get; init; }
    /// <summary>Номер страницы (с нуля), на которой найден номер.</summary>
    public int PageIndex { get; init; } = -1;
    public IReadOnlyList<NumberRead> Reads { get; init; } = Array.Empty<NumberRead>();
    public TimeSpan Elapsed { get; init; }
    public OcrTimings Timings { get; init; } = new();

    /// <summary>Варианты для подсказки оператору: сначала те, что прочитали оба движка, затем по числу чтений.</summary>
    public IEnumerable<(RegNumber Number, int Votes)> Candidates =>
        Reads.GroupBy(r => r.Number.Key)
             .Select(g => (Number: g.First().Number, Votes: g.Count(), Both: g.Any(r => r.IsTesseract) && g.Any(r => r.IsPaddle)))
             .OrderByDescending(x => x.Both).ThenByDescending(x => x.Votes)
             .Select(x => (x.Number, x.Votes));
}

/// <summary>
/// Поиск реестрового номера двумя независимыми движками. Строка «Зарегистрировано в реестре…» находится Tesseract,
/// затем её читают оба движка (Tesseract в нескольких режимах и нейросеть PaddleOCR). Автоматически принимается только номер,
/// который прочитали оба движка. Номер ищется с последней страницы к первой.
/// </summary>
public sealed class RegNumberReader : IDisposable
{
    private static readonly Regex AnchorWord = new(@"реестр|регистр", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RegWord = new(@"егистр", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const string DigitsWhitelist = "0123456789-/.";
    private const int WorkWidth = 2200;       // страницы уже этой ширины увеличиваем (мелкие исходники)
    private const float LocateScale = 0.6f;   // строку «в реестре» ищем на уменьшенной копии: быстрее
    private const double LocateTop = 0.45;    // сначала смотрим нижние 55 % страницы, затем всю

    private readonly TesseractEngine _engine;
    private readonly TesseractEngine _locatorEngine;
    private OcrTimings _timings = new();
    private bool _noVolume;       // режим старых документов без тома (задаётся при каждом поиске)

    public RegNumberReader()
    {
        Licensing.LicenseManager.Require();
        _engine = new TesseractEngine(AppPaths.TessData, "rus", EngineMode.LstmOnly);
        _engine.DefaultPageSegMode = PageSegMode.Auto;
        TesseractEngine? locator = null;
        try
        {
            locator = new TesseractEngine(AppPaths.FastTessData, "rus", EngineMode.LstmOnly);
            _ = PaddleRecognizer.Instance;
            _locatorEngine = locator;
        }
        catch { locator?.Dispose(); _engine.Dispose(); throw; }
    }

    private sealed record Line(string Text, int X1, int Y1, int X2, int Y2, int? NumberX = null);

    /// <summary>Ищет номер на последних maxPages страницах (с последней к первой). getPage(i) отдаёт JPEG страницы i.</summary>
    public NumberSearchResult Find(int pageCount, Func<int, byte[]> getPage, int maxPages, bool noVolume = false, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPages);
        ArgumentNullException.ThrowIfNull(getPage);
        _noVolume = noVolume;
        _timings = new OcrTimings();
        var sw = Stopwatch.StartNew();
        byte[]? lastPage = null;
        int first = Math.Max(0, pageCount - maxPages);
        for (int i = pageCount - 1; i >= first; i--)
        {
            ct.ThrowIfCancellationRequested();
            var jpeg = _timings.Measure("pdf-render", () => getPage(i));
            if (i == pageCount - 1) lastPage = jpeg;
            var r = FindOnPage(jpeg, rotate180: false);
            if (r != null)
                return new NumberSearchResult { Best = r.Value.best, Sure = r.Value.sure, PageIndex = i, Reads = r.Value.reads, Elapsed = sw.Elapsed, Timings = _timings };
        }
        if (pageCount > 0)      // последняя страница могла лечь вверх ногами
        {
            var r = FindOnPage(lastPage!, rotate180: true);
            if (r != null)
                return new NumberSearchResult { Best = r.Value.best, Sure = r.Value.sure, PageIndex = pageCount - 1, Reads = r.Value.reads, Elapsed = sw.Elapsed, Timings = _timings };
        }
        return new NumberSearchResult { Elapsed = sw.Elapsed, Timings = _timings };
    }

    private (RegNumber? best, bool sure, List<NumberRead> reads)? FindOnPage(byte[] jpeg, bool rotate180)
    {
        using var raw = _timings.Measure("pix-decode", () => Pix.LoadFromMemory(jpeg));
        BitmapSource? lazySource = null;
        BitmapSource Source()
        {
            if (lazySource != null) return lazySource;
            var s0 = _timings.Measure("bitmap-decode", () => ImageUtil.Decode(jpeg));
            if (rotate180) { s0 = new TransformedBitmap(s0, new RotateTransform(180)); s0.Freeze(); }
            return lazySource = s0;
        }

        using var intermediate = rotate180 ? raw.Rotate90(1) : raw.Clone();
        using var oriented = rotate180 ? intermediate.Rotate90(1) : intermediate.Clone();
        return FindInPixels(oriented, Source);
    }

    private (RegNumber? best, bool sure, List<NumberRead> reads)? FindInPixels(Pix raw, Func<BitmapSource> source)
    {
        using var work = raw.Width < WorkWidth - 200 ? raw.Scale((float)WorkWidth / raw.Width, (float)WorkWidth / raw.Width) : raw.Clone();
        int w = work.Width, h = work.Height;
        using var small = _timings.Measure("locate-scale", () => work.Scale(LocateScale, LocateScale));
        foreach (var topFraction in new[] { LocateTop, 0.0 })
        {
            var fastLines = _timings.Measure("locate-fast-model", () => LocateLines(small, (int)(h * topFraction), _locatorEngine));
            if (fastLines.Count > 0)
            {
                var registered = fastLines.Where(l => RegWord.IsMatch(l.Text)).ToList();
                var fastLine = (registered.Count > 0 ? registered : fastLines).OrderBy(l => l.Y1).Last();
                // The locator contributes no numeric vote. A small crop must be read identically
                // twice by each verification engine; a mismatch immediately uses the original path.
                var targeted = ReadTarget(source(), w, h, fastLine);
                if (targeted != null) return (targeted.Value.best, true, targeted.Value.reads);
            }
            var lines = _timings.Measure("locate", () => LocateLines(small, (int)(h * topFraction), _engine));
            if (lines.Count == 0) continue;

            var withReg = lines.Where(l => RegWord.IsMatch(l.Text)).ToList();
            var line = (withReg.Count > 0 ? withReg : lines).OrderBy(l => l.Y1).Last();
            var reads = ReadLine(source(), w, h, line);
            if (reads.Count == 0) continue;
            var (best, sure) = Vote(reads);
            return (best, sure, reads);
        }
        return null;
    }

    /// <summary>Находит строки со словами «реестр»/«зарегистр» (OCR уменьшенной копии нижней части страницы).</summary>
    private List<Line> LocateLines(Pix small, int top, TesseractEngine engine)
    {
        var r = new Tesseract.Rect(0, (int)(top * LocateScale), small.Width, small.Height - (int)(top * LocateScale));
        var result = new List<Line>();
        engine.SetVariable("tessedit_char_whitelist", "");
        using var page = engine.Process(small, r, PageSegMode.SingleBlock);
        var registryEnds = new List<(int X, int Y)>();
        if (ReferenceEquals(engine, _locatorEngine))
        {
            using var words = page.GetIterator();
            words.Begin();
            do
            {
                if ((words.GetText(PageIteratorLevel.Word) ?? "").Contains("реестр", StringComparison.OrdinalIgnoreCase)
                    && words.TryGetBoundingBox(PageIteratorLevel.Word, out var wordBox))
                    registryEnds.Add(((int)(wordBox.X2 / LocateScale), (int)((wordBox.Y1 + wordBox.Y2) / (2 * LocateScale))));
            } while (words.Next(PageIteratorLevel.Word));
        }
        using var it = page.GetIterator();
        it.Begin();
        do
        {
            var text = it.GetText(PageIteratorLevel.TextLine) ?? "";
            if (!AnchorWord.IsMatch(text)) continue;
            if (it.TryGetBoundingBox(PageIteratorLevel.TextLine, out var b))
            {
                int y1 = (int)(b.Y1 / LocateScale), y2 = (int)(b.Y2 / LocateScale), x2 = (int)(b.X2 / LocateScale);
                int? numberX = registryEnds.Where(x => x.Y >= y1 && x.Y <= y2 && x.X < x2 - 5)
                    .Select(x => (int?)x.X).Max();
                result.Add(new Line(text, (int)(b.X1 / LocateScale), y1, x2, y2, numberX));
            }
        } while (it.Next(PageIteratorLevel.TextLine));
        return result;
    }

    private (RegNumber best, List<NumberRead> reads)? ReadTarget(BitmapSource source, int w, int h, Line line)
    {
        if (!line.NumberX.HasValue) return null;
        int lineH = line.Y2 - line.Y1;
        int x0 = Math.Max(0, line.NumberX.Value - (int)(w * 0.005));
        int x1 = Math.Min(w, line.X2 + (int)(w * 0.03));
        int y0 = Math.Max(0, line.Y1 - (int)(lineH * 0.25));
        int y1 = Math.Min(h, line.Y2 + (int)(lineH * 0.25));
        if (x1 <= x0 || y1 <= y0) return null;
        var reads = new List<NumberRead>();
        RegNumber? best = null;
        try
        {
            var gray = _timings.Measure("paddle-crop", () => CropGray(source, w, x0, y0, x1 - x0, y1 - y0));
            foreach (var (scale, mode) in new[] { (1.5f, PageSegMode.SingleLine), (2.0f, PageSegMode.SingleBlock) })
            {
                using var crop = _timings.Measure("tesseract-crop", () => Crop(source, w, x0, y0, x1 - x0, y1 - y0, scale));
                _engine.SetVariable("tessedit_char_whitelist", "");
                string text = _timings.Measure("tesseract", () =>
                {
                    using var page = _engine.Process(crop, mode);
                    return page.GetText() ?? "";
                });
                var t = ParseText("№ " + text, false, _noVolume);
                if (t == null || !RegNumberParser.IsPlausible(t) || (best != null && t.Key != best.Key)) return null;
                best ??= t;
                reads.Add(new NumberRead($"T:target/{scale}", t));
                var neural = _timings.Measure("paddle", () => PaddleRecognizer.Instance.Recognize(gray.gray, gray.width, gray.height, scale));
                var p = ParseText("№ " + neural, true, _noVolume);
                if (p == null || !RegNumberParser.IsPlausible(p) || p.Key != best.Key) return null;
                reads.Add(new NumberRead($"P:target/{scale}", p));
            }
            return best == null ? null : (best, reads);
        }
        finally { _engine.SetVariable("tessedit_char_whitelist", ""); }
    }

    private List<NumberRead> ReadLine(BitmapSource src, int w, int h, Line line)
    {
        var reads = new List<NumberRead>();
        var crops = new Dictionary<float, Pix>();
        try
        {
            var first = ParseText(line.Text, false, _noVolume);
            if (first != null) reads.Add(new NumberRead("T:line", first));

            int lineH = line.Y2 - line.Y1;
            int padY = (int)(lineH * 0.35);
            int x0 = Math.Max(0, line.X1 - (int)(w * 0.01));
            int y0 = Math.Max(0, line.Y1 - padY);
            int x1 = Math.Min(w, line.X2 + (int)(w * 0.10));   // номер часто «вылезает» правее найденной рамки строки
            int y1 = Math.Min(h, line.Y2 + padY);

            // второй движок: нейросеть читает строку целиком и две её правые части (там номер)
            ReadWithPaddle(src, w, h, line, reads, _noVolume);

            // первый движок: Tesseract в нескольких режимах
            var plans = new (float scale, PageSegMode mode, bool digits)[]
            {
                (1.5f, PageSegMode.SingleBlock, false),
                (1.5f, PageSegMode.SingleLine, true),
                (2.0f, PageSegMode.SingleLine, false),
            };
            RunPlans(src, w, x0, y0, x1, y1, plans, reads, crops);

            // Результат неуверенный: ещё несколько чтений Tesseract другими способами
            var plausible = reads.Where(r => RegNumberParser.IsPlausible(r.Number)).ToList();
            if (plausible.Count == 0 || !Vote(plausible).sure)
            {
                var extra = new (float scale, PageSegMode mode, bool digits)[]
                {
                    (2.0f, PageSegMode.SingleBlock, false),
                    (2.5f, PageSegMode.SingleLine, false),
                    (2.0f, PageSegMode.SingleBlock, true),
                    (1.25f, PageSegMode.SingleBlock, false),
                };
                RunPlans(src, w, x0, y0, x1, y1, extra, reads, crops);
            }
            return reads.Where(r => RegNumberParser.IsPlausible(r.Number)).ToList();
        }
        finally { foreach (var crop in crops.Values) crop.Dispose(); }
    }

    private static RegNumber? ParseText(string text, bool lenient, bool noVolume) =>
        noVolume ? RegNumberParser.ParseNoVolume(text, lenient) : RegNumberParser.Parse(text, lenient);

    private void ReadWithPaddle(BitmapSource src, int w, int h, Line line, List<NumberRead> reads, bool noVolume)
    {
        // для нейросети отступ по высоте малый: в вырез не должны попадать соседние строки (иначе в текст попадает «…рублей»)
        int lineH = line.Y2 - line.Y1;
        int padY = (int)(lineH * 0.12);
        int x0 = Math.Max(0, line.X1 - (int)(w * 0.01));
        int x1 = Math.Min(w, line.X2 + (int)(w * 0.03));       // справа почти без запаса: дальше может быть чужой текст
        int y0 = Math.Max(0, line.Y1 - padY), y1 = Math.Min(h, line.Y2 + padY);
        int lineW = x1 - x0;
        // целая строка, правые 55 % и правые 35 % (номер стоит в конце строки): так читается по-разному, и ошибки не повторяются
        foreach (var (name, from) in new[] { ("line", 0.0), ("right55", 0.45), ("right35", 0.65) })
        {
            int cx = x0 + (int)(lineW * from);
            (byte[] gray, int cw, int ch) crop;
            try { crop = _timings.Measure("paddle-crop", () => CropGray(src, w, cx, y0, x1 - cx, y1 - y0)); }
            catch (Exception ex) { Log.Warn("Нейросеть, вырез: " + ex.Message); continue; }
            foreach (var stretch in new[] { 1.5, 2.0 })
            {
                try
                {
                    var text = _timings.Measure("paddle", () => PaddleRecognizer.Instance.Recognize(crop.gray, crop.cw, crop.ch, stretch));
                    var n = ParseText("№ " + text, true, noVolume);
                    if (n != null) reads.Add(new NumberRead($"P:{name}/{stretch}", n));
                }
                catch (Exception ex) { Log.Warn("Нейросеть: " + ex.Message); }
            }
        }
    }

    private void RunPlans(BitmapSource src, int w, int x0, int y0, int x1, int y1, (float scale, PageSegMode mode, bool digits)[] plans, List<NumberRead> reads, Dictionary<float, Pix> crops)
    {
        foreach (var scaleGroup in plans.GroupBy(p => p.scale))
        {
            if (!crops.TryGetValue(scaleGroup.Key, out var crop))
            {
                crop = _timings.Measure("tesseract-crop", () => Crop(src, w, x0, y0, x1 - x0, y1 - y0, scaleGroup.Key));
                crops.Add(scaleGroup.Key, crop);
            }
            foreach (var p in scaleGroup)
            {
                _engine.SetVariable("tessedit_char_whitelist", p.digits ? DigitsWhitelist : "");
                var text = _timings.Measure("tesseract", () =>
                {
                    using var page = _engine.Process(crop, p.mode);
                    return string.Join(" ", (page.GetText() ?? "").Split(new[] { '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries));
                });
                var n = ParseText("№ " + text, false, _noVolume);
                if (n != null) reads.Add(new NumberRead($"T:{p.scale}/{p.mode}{(p.digits ? "/цифры" : "")}", n));
            }
        }
        _engine.SetVariable("tessedit_char_whitelist", "");
    }

    private static Int32Rect ToSourceRect(BitmapSource src, int workWidth, int x, int y, int cw, int ch)
    {
        double k = (double)src.PixelWidth / workWidth;
        var rect = new Int32Rect(Math.Max(0, (int)(x * k)), Math.Max(0, (int)(y * k)), Math.Max(1, (int)(cw * k)), Math.Max(1, (int)(ch * k)));
        return new Int32Rect(rect.X, rect.Y, Math.Max(1, Math.Min(rect.Width, src.PixelWidth - rect.X)), Math.Max(1, Math.Min(rect.Height, src.PixelHeight - rect.Y)));
    }

    /// <summary>Вырезает прямоугольник (в координатах рабочей картинки шириной workWidth) с увеличением и отдаёт как Pix.</summary>
    private static Pix Crop(BitmapSource src, int workWidth, int x, int y, int cw, int ch, float scale)
    {
        BitmapSource part = new CroppedBitmap(src, ToSourceRect(src, workWidth, x, y, cw, ch));
        part = new TransformedBitmap(part, new ScaleTransform(scale, scale));
        return PixBuffer.FromBitmap(part);
    }

    /// <summary>Тот же вырез, но серыми байтами (для нейросети).</summary>
    private static (byte[] gray, int width, int height) CropGray(BitmapSource src, int workWidth, int x, int y, int cw, int ch)
    {
        BitmapSource part = new CroppedBitmap(src, ToSourceRect(src, workWidth, x, y, cw, ch));
        part = new FormatConvertedBitmap(part, PixelFormats.Gray8, null, 0);
        int width = part.PixelWidth, height = part.PixelHeight;
        var bytes = new byte[width * height];
        part.CopyPixels(bytes, width, 0);
        return (bytes, width, height);
    }

    /// <summary>Чтение a «урезано» относительно b: те же год и реестр, цифры номера a — часть цифр b без одной цифры.</summary>
    private static bool IsDegraded(RegNumber a, RegNumber b)
    {
        if (a.Year != b.Year || !string.Equals(a.Vol, b.Vol, StringComparison.OrdinalIgnoreCase)) return false;
        string x = a.Num.ToString(), y = b.Num.ToString();
        if (y.Length <= x.Length || y.Length - x.Length > 1) return false;     // «урезанным» считаем чтение с потерей одной цифры
        int i = 0;
        foreach (char c in y) if (i < x.Length && c == x[i]) i++;
        return i == x.Length;
    }

    /// <summary>
    /// Двойная проверка. «Уверенно» только если выполнено всё: номер прочитан обоими движками (минимум одно чтение
    /// Tesseract и одно чтение нейросети), «урезанные» чтения не считаются несогласием, а согласных чтений не меньше,
    /// чем 2 + 2 × (число несогласных).
    /// </summary>
    private static (RegNumber best, bool sure) Vote(List<NumberRead> reads)
    {
        var full = reads.Where(r => !reads.Any(o => IsDegraded(r.Number, o.Number))).ToList();
        var groups = full.GroupBy(r => r.Number.Key)
                         .OrderByDescending(g => g.Any(r => r.IsTesseract) && g.Any(r => r.IsPaddle))
                         .ThenByDescending(g => g.Count()).ToList();
        var top = groups[0];
        int t = top.Count(r => r.IsTesseract), p = top.Count(r => r.IsPaddle);
        int others = full.Count - top.Count();
        bool sure = t >= 1 && p >= 1 && t + p >= 2 * others + 2;
        return (top.First().Number, sure);
    }

    public void Dispose() { _locatorEngine.Dispose(); _engine.Dispose(); }
}
