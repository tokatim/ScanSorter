using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScanSorter.Pdf;

public static class ImageUtil
{
    public static BitmapSource Decode(byte[] data)
    {
        using var ms = new MemoryStream(data);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    public static BitmapSource Thumbnail(byte[] data, int width)
    {
        using var ms = new MemoryStream(data);
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.DecodePixelWidth = width;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    /// <summary>Кодирует «сырые» пиксели BGRA (прозрачный фон показываем белым) в JPEG для просмотра и распознавания.</summary>
    public static byte[] EncodeJpeg(byte[] bgra, int width, int height, int quality = 90)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            int a = bgra[i + 3];
            if (a == 255) continue;
            bgra[i] = (byte)((bgra[i] * a + 255 * (255 - a)) / 255);
            bgra[i + 1] = (byte)((bgra[i + 1] * a + 255 * (255 - a)) / 255);
            bgra[i + 2] = (byte)((bgra[i + 2] * a + 255 * (255 - a)) / 255);
            bgra[i + 3] = 255;
        }
        BitmapSource src = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        src = new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);
        var enc = new JpegBitmapEncoder { QualityLevel = quality };
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
