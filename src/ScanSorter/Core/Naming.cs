namespace ScanSorter.Core;

/// <summary>Правила имён и папок: &lt;папка вывода&gt;\&lt;тип&gt;\&lt;год&gt;\&lt;год-том-номер&gt;.pdf, при повторе суффикс _2, _3…</summary>
public static class Naming
{
    public static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "Прочее" : cleaned;
    }

    public static string FolderFor(string outputDir, string docType, int year) =>
        Path.Combine(outputDir, SafeName(docType), year.ToString());

    /// <summary>Имя без расширения: «год-реестр-номер», а для старых документов без тома «год-номер».</summary>
    public static string BaseName(int year, string vol, int num) =>
        string.IsNullOrEmpty(vol) ? $"{year}-{num}" : $"{year}-{SafeName(vol)}-{num}";

    /// <summary>Файл с таким номером уже есть в папке (без учёта суффиксов).</summary>
    public static bool Exists(string outputDir, string docType, int year, string vol, int num) =>
        File.Exists(Path.Combine(FolderFor(outputDir, docType, year), BaseName(year, vol, num) + ".pdf"));

    /// <summary>Свободное имя файла: …\2026-1-829.pdf, затем …\2026-1-829_2.pdf и т. д.</summary>
    public static string FreePath(string outputDir, string docType, int year, string vol, int num)
    {
        var folder = FolderFor(outputDir, docType, year);
        Directory.CreateDirectory(folder);
        var baseName = BaseName(year, vol, num);
        var path = Path.Combine(folder, baseName + ".pdf");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{baseName}_{i}.pdf");
        return path;
    }
}
