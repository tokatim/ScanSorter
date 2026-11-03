using System.Text.RegularExpressions;

namespace ScanSorter.Pdf;

/// <summary>Проверяет, объявлен ли исходный файл как PDF/A-1b (отметка pdfaid в метаданных). Файл не изменяется.</summary>
public static class PdfaMark
{
    private static readonly Regex Part = new(@"pdfaid:part\s*(?:>|=)\s*[""']?\s*1\b", RegexOptions.Compiled);
    private static readonly Regex Conformance = new(@"pdfaid:conformance\s*(?:>|=)\s*[""']?\s*[Bb]\b", RegexOptions.Compiled);

    /// <summary>true — отметка PDF/A-1b найдена; false — не найдена (метаданные могут быть сжаты, тогда это лишь предупреждение).</summary>
    public static bool IsDeclaredPdfA1b(string path)
    {
        try
        {
            var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path));
            return Part.IsMatch(text) && Conformance.IsMatch(text);
        }
        catch { return false; }
    }
}
