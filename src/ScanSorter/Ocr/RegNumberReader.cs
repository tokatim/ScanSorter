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
    private bool _noVolume;       // режим старых документов без тома (задаётся при каждом поиске)

    public RegNumberReader()
    {
        Licensing.LicenseManager.Require();
        _engine = new TesseractEngine(AppPaths.TessData, "rus", EngineMode.LstmOnly);
        _engine.DefaultPageSegMode = PageSegMode.Auto;
        _ = PaddleRecognizer.Instance;       // загружаем нейросеть сразу, а не при первом файле
    }

    private sealed record Line(string Text, int X1, int Y1, int X2, int Y2);

    /// <summary>Ищет номер на последних maxPages страницах (с последней к первой). getPage(i) отдаёт JPEG страницы i.</summary>
    public NumberSearchResult Find(int pageCount, Func<int, byte[]> getPage, int maxPages, bool noVolume = false, CancellationToken ct = default)
    {
        _noVolume = noVolume;
        var sw = Stopwatch.StartNew();
        int first = Math.Max(0, pageCount - maxPages);
        for (int i = pageCount - 1; i >= first; i--)
        {
            ct.ThrowIfCancellationRequested();
            var r = FindOnPage(getPage(i), rotate180: false);
            if (r != null)
                return new NumberSearchResult { Best = r.Value.best, Sure = r.Value.sure, PageIndex = i, Reads = r.Value.reads, Elapsed = sw.Elapsed };
        }
        if (pageCount > 0)      // последняя страница могла лечь вверх ногами
        {
            var r = FindOnPage(getPage(pageCount - 1), rotate180: true);
            if (r != null)
                return new NumberSearchResult { Best = r.Value.best, Sure = r.Value.sure, PageIndex = pageCount - 1, Reads = r.Value.reads, Elapsed = sw.Elapsed };
        }
        return new NumberSearchResult { Elapsed = sw.Elapsed };
    }

    private (RegNumber? best, bool sure, List<NumberRead> reads)? FindOnPage(byte[] jpeg, bool rotate180)
    {
        using var raw = Pix.LoadFromMemory(jpeg);
        Pix pix = raw.Clone();
        if (rotate180) { var r1 = pix.Rotate90(1); pix.Dispose(); pix = r1.Rotate90(1); r1.Dispose(); }
        if (pix.Width < WorkWidth - 200)
        {
            var up = pix.Scale((float)WorkWidth / pix.Width, (float)WorkWidth / pix.Width);
            pix.Dispose(); pix = up;
        }
        using var work = pix;
        int w = work.Width, h = work.Height;

        BitmapSource? lazySource = null;
        BitmapSource Source()
        {
            if (lazySource != null) return lazySource;
            var s0 = ImageUtil.Decode(jpeg);
            if (rotate180) { s0 = new TransformedBitmap(s0, new RotateTransform(180)); s0.Freeze(); }
            return lazySource = s0;
        }

        foreach (var topFraction in new[] { LocateTop, 0.0 })
        {
            var lines = LocateLines(work, (int)(h * topFraction));
            if (lines.Count == 0) continue;

            var withReg = lines.Where(l => RegWord.IsMatch(l.Text)).ToList();
            var line = (withReg.Count > 0 ? withReg : lines).OrderBy(l => l.Y1).Last();
            var reads = ReadLine(Source(), w, h, line);
            if (reads.Count == 0) continue;
            var (best, sure) = Vote(reads);
            return (best, sure, reads);
        }
        return null;
    }

    /// <summary>Находит строки со словами «реестр»/«зарегистр» (OCR уменьшенной копии нижней части страницы).</summary>
    private List<Line> LocateLines(Pix pix, int top)
    {
        using var small = pix.Scale(LocateScale, LocateScale);
        var r = new Tesseract.Rect(0, (int)(top * LocateScale), small.Width, small.Height - (int)(top * LocateScale));
        var result = new List<Line>();
        _engine.SetVariable("tessedit_char_whitelist", "");
        using var page = _engine.Process(small, r, PageSegMode.SingleBlock);
        using var it = page.GetIterator();
        it.Begin();
        do
        {
            var text = it.GetText(PageIteratorLevel.TextLine) ?? "";
            if (!AnchorWord.IsMatch(text)) continue;
            if (it.TryGetBoundingBox(PageIteratorLevel.TextLine, out var b))
                result.Add(new Line(text, (int)(b.X1 / LocateScale), (int)(b.Y1 / LocateScale), (int)(b.X2 / LocateScale), (int)(b.Y2 / LocateScale)));
        } while (it.Next(PageIteratorLevel.TextLine));
        return result;
    }

    private List<NumberRead> ReadLine(BitmapSource src, int w, int h, Line line)
    {
        var reads = new List<NumberRead>();
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
        RunPlans(src, w, x0, y0, x1, y1, plans, reads);

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
            RunPlans(src, w, x0, y0, x1, y1, extra, reads);
        }
        return reads.Where(r => RegNumberParser.IsPlausible(r.Number)).ToList();
    }

    private static RegNumber? ParseText(string text, bool lenient, bool noVolume) =>
        noVolume ? RegNumberParser.ParseNoVolume(text, lenient) : RegNumberParser.Parse(text, lenient);

    private static void ReadWithPaddle(BitmapSource src, int w, int h, Line line, List<NumberRead> reads, bool noVolume)
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
            try { crop = CropGray(src, w, cx, y0, x1 - cx, y1 - y0); }
            catch (Exception ex) { Log.Warn("Нейросеть, вырез: " + ex.Message); continue; }
            foreach (var stretch in new[] { 1.5, 2.0 })
            {
                try
                {
                    var text = PaddleRecognizer.Instance.Recognize(crop.gray, crop.cw, crop.ch, stretch);
                    if (Environment.GetEnvironmentVariable("SS_DEBUG") == "1") Log.Info($"P:{name}/{stretch} [{crop.cw}x{crop.ch}] \"{text}\"");
                    var n = ParseText("№ " + text, true, noVolume);
                    if (n != null) reads.Add(new NumberRead($"P:{name}/{stretch}", n));
                }
                catch (Exception ex) { Log.Warn("Нейросеть: " + ex.Message); }
            }
        }
    }

    private void RunPlans(BitmapSource src, int w, int x0, int y0, int x1, int y1, (float scale, PageSegMode mode, bool digits)[] plans, List<NumberRead> reads)
    {
        foreach (var scaleGroup in plans.GroupBy(p => p.scale))
        {
            using var crop = Crop(src, w, x0, y0, x1 - x0, y1 - y0, scaleGroup.Key);
            foreach (var p in scaleGroup)
            {
                _engine.SetVariable("tessedit_char_whitelist", p.digits ? DigitsWhitelist : "");
                using var page = _engine.Process(crop, p.mode);
                var text = string.Join(" ", (page.GetText() ?? "").Split(new[] { '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries));
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
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(part));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return Pix.LoadFromMemory(ms.ToArray());
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

    public void Dispose() => _engine.Dispose();
}
