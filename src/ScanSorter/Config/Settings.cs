using System.Text.Json;

namespace ScanSorter.Config;

/// <summary>Настройки оператора (запоминаются между запусками, settings.json).</summary>
public sealed class Settings
{
    public string OutputDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Сканы");
    public List<string> DocTypes { get; set; } = new() { "Завещания", "Сделки", "Прочее" };
    public string LastDocType { get; set; } = "Завещания";
    public int? ManualYear { get; set; }
    public bool IncludeSubfolders { get; set; } = true;
    /// <summary>Старые документы без номера тома: читается только номер, имя «год-номер».</summary>
    public bool NoVolume { get; set; }
    /// <summary>Сколько файлов обрабатывать одновременно (0 = автоматически по числу ядер).</summary>
    public int Workers { get; set; }

    public int WorkerCount => Workers > 0 ? Math.Min(Workers, 8) : Math.Clamp(Environment.ProcessorCount - 1, 1, 4);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile), Options) ?? new Settings();
        }
        catch (Exception ex) { Log.Warn("Не удалось прочитать settings.json: " + ex.Message); }
        return new Settings();
    }

    public void Save()
    {
        try { File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Options)); }
        catch (Exception ex) { Log.Warn("Не удалось сохранить settings.json: " + ex.Message); }
    }
}
