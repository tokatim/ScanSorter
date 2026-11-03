using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;

namespace ScanSorter.Pdf;

/// <summary>Открытый PDF: число страниц и показ нужной страницы как JPEG (для распознавания и просмотра).</summary>
public sealed class PdfSource : IDisposable
{
    // Лист A4 (595 pt) в 3,7 раза даёт ширину около 2200 пикселей, как у обычного скана.
    private const double RenderScale = 3.7;

    private readonly IDocReader _reader;
    public int PageCount { get; }

    private PdfSource(IDocReader reader)
    {
        _reader = reader;
        PageCount = reader.GetPageCount();
    }

    public static PdfSource Open(string path) =>
        new(DocLib.Instance.GetDocReader(path, new PageDimensions(RenderScale)));

    public byte[] RenderJpeg(int pageIndex, int quality = 90)
    {
        using var page = _reader.GetPageReader(pageIndex);
        int w = page.GetPageWidth(), h = page.GetPageHeight();
        return ImageUtil.EncodeJpeg(page.GetImage(), w, h, quality);
    }

    public void Dispose() => _reader.Dispose();
}
