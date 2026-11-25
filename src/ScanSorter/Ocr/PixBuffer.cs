using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tesseract;

namespace ScanSorter.Ocr;

/// <summary>Transfers pixels directly to Leptonica, without PNG encoding or temporary files.</summary>
internal static class PixBuffer
{
    internal static Pix FromBitmap(BitmapSource source)
    {
        BitmapSource pixels = source.Format == PixelFormats.Bgr32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
        int width = pixels.PixelWidth, height = pixels.PixelHeight;
        int stride = checked(width * 4);
        var buffer = new byte[checked(stride * height)];
        pixels.CopyPixels(buffer, stride, 0);
        // Leptonica stores R,G,B in bits 31..8 of each uint32. Windows x64 is little-endian.
        for (int i = 0; i < buffer.Length; i += 4)
        {
            byte blue = buffer[i], green = buffer[i + 1], red = buffer[i + 2];
            buffer[i] = 0; buffer[i + 1] = blue; buffer[i + 2] = green; buffer[i + 3] = red;
        }
        var pix = Pix.Create(width, height, 32);
        try
        {
            var data = pix.GetData();
            if (data.WordsPerLine != width) throw new InvalidOperationException("Unexpected pixel stride");
            Marshal.Copy(buffer, 0, data.Data, buffer.Length);
            pix.XRes = Math.Max(1, (int)Math.Round(source.DpiX));
            pix.YRes = Math.Max(1, (int)Math.Round(source.DpiY));
            return pix;
        }
        catch { pix.Dispose(); throw; }
    }
}
