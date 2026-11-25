using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScanSorter.Config;
using ScanSorter.Licensing;
using ScanSorter.Ocr;
using ScanSorter.Pdf;
using Tesseract;

Environment.SetEnvironmentVariable("OMP_THREAD_LIMIT", "1");
int count = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAILED: " + name);
    count++; Console.WriteLine("PASS: " + name);
}
var transfer = typeof(RegNumberReader).Assembly.GetType("ScanSorter.Ocr.PixBuffer")!
    .GetMethod("FromBitmap", BindingFlags.Static | BindingFlags.NonPublic)!;
foreach (int width in new[] { 1, 3, 4, 17 })
{
    int height = 7;
    var bytes = Enumerable.Range(0, width * height * 3).Select(i => (byte)((i * 37 + 11) % 256)).ToArray();
    BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, bytes, width * 3);
    foreach (double scale in new[] { 1.0, 1.5, 2.0 })
    {
        BitmapSource scaled = scale == 1 ? source : new TransformedBitmap(source, new ScaleTransform(scale, scale));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(scaled));
        using var stream = new MemoryStream(); encoder.Save(stream);
        using var png = Pix.LoadFromMemory(stream.ToArray());
        using var direct = (Pix)transfer.Invoke(null, new object[] { scaled })!;
        Check(png.Width == direct.Width && png.Height == direct.Height, $"Transfer dimensions {width}/{scale}");
        using var oldGray = png.Depth == 32 ? png.ConvertRGBToGray() : png.ConvertTo8(0);
        using var newGray = direct.ConvertRGBToGray();
        var a = oldGray.GetData(); var b = newGray.GetData();
        var oldBytes = new byte[a.WordsPerLine * 4 * oldGray.Height];
        var newBytes = new byte[b.WordsPerLine * 4 * newGray.Height];
        Marshal.Copy(a.Data, oldBytes, 0, oldBytes.Length); Marshal.Copy(b.Data, newBytes, 0, newBytes.Length);
        bool equal = true;
        for (int y = 0; y < oldGray.Height; y++) for (int x = 0; x < oldGray.Width; x++)
            equal &= oldBytes[y * a.WordsPerLine * 4 + (x ^ 3)] == newBytes[y * b.WordsPerLine * 4 + (x ^ 3)];
        Check(equal, $"PNG/direct pixel equality {width}/{scale}");
        Check(png.XRes == direct.XRes && png.YRes == direct.YRes, $"Transfer resolution {width}/{scale}");
    }
}
var resize = typeof(PaddleRecognizer).GetMethod("Resize", BindingFlags.NonPublic | BindingFlags.Static)!;
byte[] Resize(byte[] pixels, int sw, int sh, int dw, int dh) => (byte[])resize.Invoke(null, new object[] { pixels, sw, sh, dw, dh })!;
Check(Resize(new byte[] { 0, 64, 128, 192 }, 2, 2, 1, 1).SequenceEqual(new byte[] {96}), "Area average");
Check(Resize(new byte[] { 0, 30, 240 }, 3, 1, 2, 1).SequenceEqual(new byte[] {15, 135}), "Odd-sized resampling boundaries");
Check(Resize(new byte[] {10, 20}, 2, 1, 4, 1).SequenceEqual(new byte[] {10, 10, 20, 20}), "Repeated pixels during upscale");
Check(WorkerPolicy.Resolve(0, 4, 8L << 30) == 3, "Automatic concurrency leaves a CPU for UI");
Check(WorkerPolicy.Resolve(0, 16, 8L << 30) == 4, "Automatic concurrency capped");
Check(WorkerPolicy.Resolve(8, 16, 1L << 30) == 2, "Memory bounds manual concurrency");
Check(WorkerPolicy.Resolve(0, 1, 200L << 20) == 1, "Low-memory single-core operation");
Check(RegNumberParser.Parse("№ 68/153-н/34-2026-1-829")?.ToString() == "2026-1-829", "Standard number format");
Check(!LicenseManager.Evaluate("", DateTime.UtcNow, new uint[] {1, 2, 3}).IsValid, "Empty license still rejected");
Check(!typeof(ScanSorter.App).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
    .Any(m => m.Name is "LicenseTest" or "BatchTest" or "DialogDemo"), "Release has no debug activation paths");
var alpha = ImageUtil.FromBgra(new byte[] { 0, 0, 0, 0 }, 1, 1);
var white = new byte[4]; alpha.CopyPixels(white, 4, 0);
Check(white.All(v => v == 255), "Transparent PDF background stays white");
if (args.Contains("--ocr"))
{
    Environment.SetEnvironmentVariable("OMP_THREAD_LIMIT", "1");
    using var reader = new RegNumberReader();
    var pixels = Enumerable.Repeat((byte)255, 320 * 440 * 4).ToArray();
    var bitmap = ImageUtil.FromBgra(pixels, 320, 440);
    var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = new MemoryStream(); encoder.Save(stream); var jpeg = stream.ToArray();
    int renders = 0;
    var result = reader.Find(1, _ => { renders++; return jpeg; }, 4);
    Check(result.Best == null && !result.Sure, "Blank page never becomes an automatic number");
    Check(renders == 1, "Rotation fallback reuses last rendered page");
    Check(reader.Find(0, _ => throw new Exception("Unexpected render"), 4).Best == null, "Empty document performs no rendering");
    try { reader.Find(1, _ => jpeg, 0); throw new Exception("Invalid page limit accepted"); }
    catch (ArgumentOutOfRangeException) { Check(true, "Invalid page limit rejected"); }
    var token = new CancellationToken(true); renders = 0;
    try { reader.Find(1, _ => { renders++; return jpeg; }, 4, ct: token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { Check(renders == 0, "Cancellation occurs before rendering"); }
}
if (args.Contains("--ocr"))
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            using var reader = new RegNumberReader();
            foreach (var test in new[]
            {
                (Text: "Зарегистрировано в реестре: № 68/153-н/34-2026-1-829", NoVolume: false, Expected: "2026-1-829", Label: "standard"),
                (Text: "Зарегистрировано в реестре за № 23", NoVolume: true, Expected: "23", Label: "no-volume"),
                (Text: "Зарегистрировано в реестре за № В-37", NoVolume: true, Expected: "В-37", Label: "letter")
            })
            {
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    drawing.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, 1200, 1700));
                    drawing.DrawText(new FormattedText("Тестовый документ", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"),
                        System.Windows.FlowDirection.LeftToRight, new Typeface("Arial"), 28, Brushes.Black, 1), new System.Windows.Point(60, 60));
                    drawing.DrawText(new FormattedText(test.Text, System.Globalization.CultureInfo.GetCultureInfo("ru-RU"),
                        System.Windows.FlowDirection.LeftToRight, new Typeface("Arial"), 22, Brushes.Black, 1), new System.Windows.Point(60, 1250));
                }
                var image = new RenderTargetBitmap(1200, 1700, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
                var encoder = new JpegBitmapEncoder { QualityLevel = 90 }; encoder.Frames.Add(BitmapFrame.Create(image));
                using var bytes = new MemoryStream(); encoder.Save(bytes); var jpeg = bytes.ToArray();
                var found = reader.Find(1, _ => jpeg, 4, test.NoVolume);
                Check(found.Best?.ToString() == test.Expected, $"Synthetic OCR {test.Label}");
            }
        }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    if (failure != null) throw new Exception("Synthetic OCR regression", failure);
}

Console.WriteLine($"Completed {count} checks");
