using System.ComponentModel;
using System.Windows.Media;
using ScanSorter.Ocr;

namespace ScanSorter.Core;

public enum ItemStatus { Pending, Processing, Done, NeedsReview, Skipped, Error }

/// <summary>Один файл в списке: исходный PDF, выбранный тип и результат обработки.</summary>
public sealed class BatchItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string SourcePath { get; }
    public string FileName => Path.GetFileName(SourcePath);
    public string Folder => Path.GetDirectoryName(SourcePath) ?? "";

    private string _docType = "";
    public string DocType { get => _docType; set { _docType = value; Raise(nameof(DocType)); } }

    private int _pageCount;
    public int PageCount { get => _pageCount; set { _pageCount = value; Raise(nameof(PageCountText)); } }
    public string PageCountText => _pageCount > 0 ? _pageCount.ToString() : "";

    private ItemStatus _status = ItemStatus.Pending;
    public ItemStatus Status
    {
        get => _status;
        set { _status = value; Raise(nameof(Status)); Raise(nameof(StatusText)); Raise(nameof(StatusBrush)); }
    }

    private string _note = "";
    /// <summary>Пояснение к статусу (почему нужна проверка, текст ошибки, замечание о дубле).</summary>
    public string Note
    {
        get => _note;
        set { _note = value; Raise(nameof(Note)); Raise(nameof(StatusText)); Raise(nameof(StatusBrush)); }
    }

    private string _targetPath = "";
    public string TargetPath
    {
        get => _targetPath;
        set { _targetPath = value; Raise(nameof(TargetPath)); Raise(nameof(TargetText)); }
    }

    /// <summary>Куда сохранён файл в виде «Тип\Год\имя.pdf».</summary>
    public string TargetText
    {
        get
        {
            if (string.IsNullOrEmpty(_targetPath)) return "";
            var yearDir = Path.GetDirectoryName(_targetPath) ?? "";
            var typeDir = Path.GetDirectoryName(yearDir) ?? "";
            return Path.Combine(Path.GetFileName(typeDir), Path.GetFileName(yearDir), Path.GetFileName(_targetPath));
        }
    }

    /// <summary>Файл с таким номером уже был: этот сохранён с суффиксом _2, _3…</summary>
    public bool IsDuplicate { get; set; }

    /// <summary>Результат поиска номера (для разбора в конце прогона).</summary>
    public NumberSearchResult? Search { get; set; }

    public BatchItem(string path, string docType)
    {
        SourcePath = path;
        _docType = docType;
    }

    public string StatusText => Status switch
    {
        ItemStatus.Pending => "Ожидает",
        ItemStatus.Processing => "Обрабатывается…",
        ItemStatus.Done => string.IsNullOrEmpty(Note) ? "Готово" : "Готово: " + Note,
        ItemStatus.NeedsReview => "Нужна проверка: " + Note,
        ItemStatus.Skipped => "Пропущен" + (string.IsNullOrEmpty(Note) ? "" : ": " + Note),
        _ => "Ошибка: " + Note,
    };

    public Brush StatusBrush => Status switch
    {
        ItemStatus.Done => string.IsNullOrEmpty(Note) ? Brushes.SeaGreen : Brushes.DarkOrange,
        ItemStatus.NeedsReview => Brushes.DarkOrange,
        ItemStatus.Error => Brushes.Firebrick,
        ItemStatus.Skipped => Brushes.Gray,
        ItemStatus.Processing => Brushes.RoyalBlue,
        _ => Brushes.Black,
    };
}
