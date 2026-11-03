using System.Reflection;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ScanSorter.Ocr;

/// <summary>
/// Второй, независимый движок распознавания: нейросеть PaddleOCR (PP-OCRv5, распознавание строк) через ONNX Runtime.
/// Работает локально, модель встроена в программу. Ошибается иначе, чем Tesseract, поэтому совпадение двух движков надёжно.
/// </summary>
public sealed class PaddleRecognizer
{
    private static readonly Lazy<PaddleRecognizer> Lazy = new(() => new PaddleRecognizer());
    public static PaddleRecognizer Instance => Lazy.Value;

    private const int Height = 48;
    private readonly InferenceSession _session;
    private readonly string _input;
    private readonly string[] _charset;

    private PaddleRecognizer()
    {
        using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("paddle_rec.onnx")
            ?? throw new InvalidOperationException("В программу не встроена модель распознавания.");
        using var ms = new MemoryStream();
        res.CopyTo(ms);
        var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(ms.ToArray(), options);
        _input = _session.InputMetadata.Keys.First();
        var chars = _session.ModelMetadata.CustomMetadataMap["character"].Split('\n');
        _charset = new[] { "" }.Concat(chars).Append(" ").ToArray();        // индекс 0 — «пусто» (CTC), в конце пробел
    }

    /// <summary>Читает одну строку текста с серого изображения (байты построчно, width × height).</summary>
    /// <param name="stretch">Растяжение по ширине: у модели больше «шагов» на каждую цифру, поэтому цифры, стоящие вплотную (774, 2026), не склеиваются.</param>
    public string Recognize(byte[] gray, int width, int height, double stretch = 1.5)
    {
        if (width < 8 || height < 8) return "";
        int nw = Math.Clamp((int)Math.Round(width * (double)Height / height * stretch), 16, 6000);
        var resized = Resize(gray, width, height, nw, Height);

        var tensor = new DenseTensor<float>(new[] { 1, 3, Height, nw });
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < nw; x++)
            {
                float v = (resized[y * nw + x] / 255f - 0.5f) / 0.5f;
                tensor[0, 0, y, x] = v; tensor[0, 1, y, x] = v; tensor[0, 2, y, x] = v;
            }

        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_input, tensor) });
        var output = results.First().AsTensor<float>();                       // 1 × T × C
        int steps = output.Dimensions[1], classes = output.Dimensions[2];

        var sb = new System.Text.StringBuilder();
        int prev = 0;
        for (int t = 0; t < steps; t++)
        {
            int best = 0; float bestV = float.MinValue;
            for (int c = 0; c < classes; c++)
            {
                float v = output[0, t, c];
                if (v > bestV) { bestV = v; best = c; }
            }
            if (best != prev && best != 0 && best < _charset.Length) sb.Append(_charset[best]);
            prev = best;
        }
        return sb.ToString();
    }

    /// <summary>Масштабирование: при уменьшении усредняем площадь (без «лесенки»), при увеличении билинейно.</summary>
    private static byte[] Resize(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh];
        double sx = (double)sw / dw, sy = (double)sh / dh;
        for (int oy = 0; oy < dh; oy++)
        {
            double fy0 = oy * sy, fy1 = Math.Min(sh, fy0 + sy);
            int y0 = (int)fy0, y1 = Math.Max(y0 + 1, (int)Math.Ceiling(fy1));
            for (int ox = 0; ox < dw; ox++)
            {
                double fx0 = ox * sx, fx1 = Math.Min(sw, fx0 + sx);
                int x0 = (int)fx0, x1 = Math.Max(x0 + 1, (int)Math.Ceiling(fx1));
                int sum = 0, n = 0;
                for (int y = y0; y < Math.Min(y1, sh); y++)
                {
                    int row = y * sw;
                    for (int x = x0; x < Math.Min(x1, sw); x++) { sum += src[row + x]; n++; }
                }
                dst[oy * dw + ox] = (byte)(n == 0 ? 255 : sum / n);
            }
        }
        return dst;
    }
}
