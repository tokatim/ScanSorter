using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace ScanSorter.Core;

/// <summary>Отчёт о прогоне (CSV) и перемещение исходников в корзину.</summary>
public static class Report
{
    /// <summary>Пишет отчёт в папку вывода: исходный файл, результат, статус, примечание. Возвращает путь к отчёту.</summary>
    public static string Write(string outputDir, IEnumerable<BatchItem> items)
    {
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, $"Отчёт сортировки {DateTime.Now:yyyy-MM-dd HH-mm}.csv");
        var sb = new StringBuilder();
        sb.AppendLine("Исходный файл;Результат;Статус;Примечание");
        foreach (var i in items)
            sb.AppendLine(string.Join(";", Csv(i.SourcePath), Csv(i.TargetPath), Csv(i.Status.ToString()), Csv(i.Note)));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    private static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    /// <summary>Исходный файл в корзину Windows (можно восстановить). Возвращает true, если файл перемещён.</summary>
    public static bool Recycle(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return true;
        }
        catch (Exception ex)
        {
            Config.Log.Warn("Корзина: " + ex.Message);
            return false;
        }
    }
}
