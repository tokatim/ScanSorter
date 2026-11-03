using System.Reflection;

namespace ScanSorter.Config;

/// <summary>
/// Пути программы. Настройки, журнал и словарь распознавания лежат в папке пользователя
/// (%LocalAppData%\ScanSorter), поэтому программа работает из любой папки, в том числе одним файлом exe.
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = EnsureDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScanSorter"));

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LogDir => Path.Combine(Root, "log");

    private static readonly Lazy<string> TessDataDir = new(InstallTessData);
    /// <summary>Папка со словарём распознавания. Словарь встроен в exe и при первом запуске извлекается.</summary>
    public static string TessData => TessDataDir.Value;

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private static string InstallTessData()
    {
        var dir = EnsureDir(Path.Combine(Root, "tessdata"));
        var file = Path.Combine(dir, "rus.traineddata");
        using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("rus.traineddata")
            ?? throw new InvalidOperationException("В программу не встроен словарь распознавания.");
        if (!File.Exists(file) || new FileInfo(file).Length != res.Length)
        {
            var tmp = file + ".tmp";
            using (var fs = File.Create(tmp)) res.CopyTo(fs);
            File.Move(tmp, file, overwrite: true);
        }
        return dir;
    }
}
