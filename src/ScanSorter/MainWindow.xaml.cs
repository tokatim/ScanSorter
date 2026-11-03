using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScanSorter.Config;
using ScanSorter.Core;
using ScanSorter.Ocr;
using ScanSorter.Pdf;
using ScanSorter.UI;

namespace ScanSorter;

/// <summary>Список типов документов для выпадающих списков (общий для окна и строк таблицы).</summary>
public static class TypeList
{
    public static ObservableCollection<string> Items { get; } = new();
}

public partial class MainWindow : Window
{
    private readonly Settings _settings = Settings.Load();
    private readonly ObservableCollection<BatchItem> _items = new();
    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _loading = true;

    public MainWindow()
    {
        InitializeComponent();
        FilesGrid.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => UpdateCounters();
    }

    // ---------- запуск ----------

    // ---------- лицензия ----------

    private System.Windows.Threading.DispatcherTimer? _licenseTimer;

    private void ApplyLicenseTitle()
    {
        var st = Licensing.LicenseManager.Check();
        Title = st.IsValid ? "Сортировщик сканов — " + st.Message : "Сортировщик сканов";
    }

    /// <summary>Лицензия действует, иначе предлагает активацию. false — активировать не удалось.</summary>
    private bool EnsureLicense()
    {
        var st = Licensing.LicenseManager.Check();
        if (st.IsValid || App.DemoMode) return true;
        var dlg = new ActivationWindow(st.Message) { Owner = this };
        if (dlg.ShowDialog() != true) return false;
        ApplyLicenseTitle();
        return true;
    }

    private void StartLicenseTimer()
    {
        _licenseTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _licenseTimer.Tick += (_, _) =>
        {
            if (Licensing.LicenseManager.Check().IsValid || App.DemoMode) return;
            _cts?.Cancel();
            if (!EnsureLicense()) Application.Current.Shutdown();
        };
        _licenseTimer.Start();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyLicenseTitle();
        StartLicenseTimer();
        foreach (var t in _settings.DocTypes) TypeList.Items.Add(t);
        DocTypeBox.ItemsSource = TypeList.Items;
        DocTypeBox.SelectedItem = TypeList.Items.FirstOrDefault(t => t == _settings.LastDocType) ?? TypeList.Items[0];
        OutputBox.Text = _settings.OutputDir;
        YearBox.Text = _settings.ManualYear?.ToString() ?? "";
        SubfoldersCheck.IsChecked = _settings.IncludeSubfolders;
        NoVolumeCheck.IsChecked = _settings.NoVolume;
        _loading = false;
        UpdateCounters();
        if (App.DemoMode) FillDemo();
    }

    // ---------- настройки ----------

    private string CurrentType => DocTypeBox.SelectedItem as string ?? TypeList.Items[0];

    private void DocTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DocTypeBox.SelectedItem is not string t) return;
        _settings.LastDocType = t;
        _settings.Save();
    }

    private void EditTypes_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new TypesDialog(TypeList.Items) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var current = CurrentType;
        _loading = true;
        TypeList.Items.Clear();
        foreach (var t in dlg.Types) TypeList.Items.Add(t);
        _settings.DocTypes = dlg.Types.ToList();
        _settings.LastDocType = dlg.Types.Contains(current) ? current : dlg.Types[0];
        _settings.Save();
        DocTypeBox.SelectedItem = _settings.LastDocType;
        // строки, у которых выбранный тип удалили, получают тип по умолчанию
        foreach (var it in _items.Where(i => !TypeList.Items.Contains(i.DocType))) it.DocType = _settings.LastDocType;
        _loading = false;
    }

    private void YearBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape) { CommitYear(); Keyboard.ClearFocus(); e.Handled = true; }
    }

    private void YearBox_LostFocus(object sender, RoutedEventArgs e) => CommitYear();

    private void CommitYear()
    {
        _settings.ManualYear = int.TryParse(YearBox.Text.Trim(), out var y) && y is >= 1990 and <= 2100 ? y : null;
        if (_settings.ManualYear == null) YearBox.Text = "";
        _settings.Save();
    }

    private void NoVolumeCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.NoVolume = NoVolumeCheck.IsChecked == true;
        _settings.Save();
    }

    private void SubfoldersCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.IncludeSubfolders = SubfoldersCheck.IsChecked == true;
        _settings.Save();
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Папка для готовых файлов", InitialDirectory = Directory.Exists(_settings.OutputDir) ? _settings.OutputDir : null };
        if (dlg.ShowDialog(this) != true) return;
        _settings.OutputDir = dlg.FolderName;
        OutputBox.Text = dlg.FolderName;
        _settings.Save();
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_settings.OutputDir);
            Process.Start(new ProcessStartInfo(_settings.OutputDir) { UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus("Не удалось открыть папку: " + ex.Message, true); }
    }

    // ---------- список файлов ----------

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Выберите PDF-файлы", Filter = "PDF-файлы (*.pdf)|*.pdf", Multiselect = true };
        if (dlg.ShowDialog(this) == true) AddPaths(dlg.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Папка с PDF-файлами" };
        if (dlg.ShowDialog(this) == true) AddPaths(new[] { dlg.FolderName });
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_running && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (_running || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        AddPaths((string[])e.Data.GetData(DataFormats.FileDrop));
    }

    /// <summary>Добавляет файлы и папки (папки просматриваются, при желании с вложенными).</summary>
    private void AddPaths(IEnumerable<string> paths)
    {
        if (_running) return;
        var option = _settings.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var found = new List<string>();
        foreach (var p in paths)
        {
            try
            {
                if (Directory.Exists(p)) found.AddRange(Directory.EnumerateFiles(p, "*.pdf", option));
                else if (File.Exists(p) && p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) found.Add(p);
            }
            catch (Exception ex) { Log.Warn("Чтение папки: " + ex.Message); }
        }
        var known = new HashSet<string>(_items.Select(i => i.SourcePath), StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (var f in found.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (!known.Add(f)) continue;
            _items.Add(new BatchItem(f, CurrentType));
            added++;
        }
        SetStatus(added == 0 ? "Новых PDF-файлов не найдено." : $"Добавлено файлов: {added}.", false);
    }

    private BatchItem? Selected => FilesGrid.SelectedItem as BatchItem;

    private static void OpenFile(string path)
    {
        try { if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Открытие файла: " + ex.Message); }
    }

    /// <summary>Двойной щелчок открывает готовый файл, а если его ещё нет, то исходный.</summary>
    private void FilesGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is not { } it) return;
        if (e.OriginalSource is DependencyObject d && FindParent<ComboBox>(d) != null) return;   // двойной щелчок по списку типов не открывает файл
        OpenFile(!string.IsNullOrEmpty(it.TargetPath) && File.Exists(it.TargetPath) ? it.TargetPath : it.SourcePath);
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var p = child; p != null; p = System.Windows.Media.VisualTreeHelper.GetParent(p)) if (p is T t) return t;
        return null;
    }

    private void OpenSource_Click(object sender, RoutedEventArgs e) { if (Selected is { } it) OpenFile(it.SourcePath); }
    private void OpenResult_Click(object sender, RoutedEventArgs e) { if (Selected is { } it && !string.IsNullOrEmpty(it.TargetPath)) OpenFile(it.TargetPath); }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } it) return;
        var path = !string.IsNullOrEmpty(it.TargetPath) && File.Exists(it.TargetPath) ? it.TargetPath : it.SourcePath;
        if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        foreach (var it in FilesGrid.SelectedItems.OfType<BatchItem>().ToList()) _items.Remove(it);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _items.Count == 0) return;
        if (_items.Any(i => i.Status == ItemStatus.Pending) &&
            MessageBox.Show(this, "Очистить весь список? Необработанные файлы будут убраны из списка (сами файлы не удаляются).", "Сортировщик сканов",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _items.Clear();
        Progress.Value = 0; ProgressText.Text = "";
    }

    private void UpdateCounters()
    {
        CountText.Text = $"Файлов: {_items.Count}";
        UpdateReviewButton();
        DropHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- обработка ----------

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        CommitYear();
        if (!EnsureLicense()) return;
        if (_items.Count == 0) { SetStatus("Список пуст: добавьте PDF-файлы.", true); return; }
        var todo = _items.Where(i => i.Status is not (ItemStatus.Done or ItemStatus.Skipped)).ToList();
        if (todo.Count == 0) { SetStatus("Все файлы в списке уже обработаны.", false); return; }
        try { Directory.CreateDirectory(_settings.OutputDir); }
        catch (Exception ex) { MessageBox.Show(this, "Не удалось создать папку вывода:\n" + ex.Message, "Сортировщик сканов", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        foreach (var it in todo) { it.Status = ItemStatus.Pending; it.Note = ""; }
        SetRunning(true);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Progress.Maximum = todo.Count; Progress.Value = 0;
        ProgressText.Text = $"0 из {todo.Count}";
        SetStatus("Запускаю распознавание…", false);

        try
        {
            var processor = new BatchProcessor(_settings.WorkerCount);
            var progress = new Progress<int>(n => { Progress.Value = n; ProgressText.Text = $"{n} из {todo.Count}"; });
            var options = new RunOptions(_settings.OutputDir, _settings.ManualYear, _settings.NoVolume);
            SetStatus("Обрабатываю файлы…", false);
            var sw = Stopwatch.StartNew();
            await Task.Run(() => processor.Run(todo, options, progress, token));
            Log.Info($"Прогон: файлов {todo.Count}, {sw.Elapsed.TotalSeconds:0} с");
        }
        catch (Exception ex)
        {
            Log.Error("Прогон", ex);
            MessageBox.Show(this, "Обработка прервана ошибкой:\n" + ex.Message, "Сортировщик сканов", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        bool cancelled = token.IsCancellationRequested;
        SetRunning(false);

        if (!cancelled) await ReviewProblemsAsync();
        OfferDeleteOriginals();
        FinishReport(cancelled);
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StopButton.IsEnabled = false;
        SetStatus("Останавливаю после текущего файла…", false);
    }

    /// <summary>В конце прогона предлагает открыть окно проверки: все спорные файлы в одном окне.</summary>
    private Task ReviewProblemsAsync()
    {
        var count = _items.Count(i => i.Status == ItemStatus.NeedsReview);
        if (count == 0) return Task.CompletedTask;
        if (MessageBox.Show(this, $"Файлов, где нужна ваша проверка: {count}.\nОткрыть проверку сейчас? Позже её можно открыть кнопкой «Проверка».", "Сортировщик сканов",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            OpenReview();
        UpdateReviewButton();
        return Task.CompletedTask;
    }

    /// <summary>Открывает окно проверки. Возвращает файлы, сохранённые в нём.</summary>
    private IReadOnlyList<BatchItem> OpenReview()
    {
        var problems = _items.Where(i => i.Status == ItemStatus.NeedsReview).ToList();
        if (problems.Count == 0) { SetStatus("Проверять нечего.", false); return Array.Empty<BatchItem>(); }
        var dlg = new ReviewWindow(problems, _settings.ManualYear, _settings.NoVolume, _settings.OutputDir) { Owner = this };
        dlg.ShowDialog();
        UpdateReviewButton();
        return dlg.SavedItems;
    }

    private void Review_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        var saved = OpenReview();
        if (saved.Count == 0) return;
        OfferDeleteOriginals(saved);
        FinishReport(false);
    }

    private void UpdateReviewButton()
    {
        int n = _items.Count(i => i.Status == ItemStatus.NeedsReview);
        ReviewButton.Content = n > 0 ? $"Проверка ({n})" : "Проверка";
        ReviewButton.IsEnabled = !_running && n > 0;
    }

    /// <summary>Спрашивает, убрать ли исходные файлы (в корзину Windows, их можно восстановить).</summary>
    private void OfferDeleteOriginals(IEnumerable<BatchItem>? only = null)
    {
        var done = (only ?? _items).Where(i => i.Status == ItemStatus.Done && File.Exists(i.SourcePath)
            && !string.Equals(Path.GetFullPath(i.SourcePath), Path.GetFullPath(i.TargetPath), StringComparison.OrdinalIgnoreCase)).ToList();
        if (done.Count == 0) return;

        var answer = MessageBox.Show(this,
            $"Сохранено файлов: {done.Count}.\n\nПереместить исходные файлы в корзину Windows?\nОни будут в корзине, их можно восстановить. Файлы со статусами «Нужна проверка», «Пропущен» и «Ошибка» не затрагиваются.",
            "Сортировщик сканов — исходные файлы", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        int moved = 0, failed = 0;
        foreach (var item in done)
        {
            // удаляем исходник только если копия на месте
            if (File.Exists(item.TargetPath) && Report.Recycle(item.SourcePath)) moved++; else failed++;
        }
        SetStatus($"Исходных файлов в корзине: {moved}" + (failed > 0 ? $", не удалось переместить: {failed}" : "") + ".", failed > 0);
    }

    private void FinishReport(bool cancelled)
    {
        int ok = _items.Count(i => i.Status == ItemStatus.Done);
        int review = _items.Count(i => i.Status == ItemStatus.NeedsReview);
        int skipped = _items.Count(i => i.Status == ItemStatus.Skipped);
        int errors = _items.Count(i => i.Status == ItemStatus.Error);
        int duplicates = _items.Count(i => i.IsDuplicate);
        string reportPath = "";
        try { reportPath = Report.Write(_settings.OutputDir, _items); }
        catch (Exception ex) { Log.Warn("Отчёт: " + ex.Message); }
        var summary = $"{(cancelled ? "Остановлено. " : "Готово. ")}Сохранено: {ok}; нужна проверка: {review}; пропущено: {skipped}; ошибок: {errors}."
                      + (duplicates > 0 ? $" Повторяющихся номеров: {duplicates} (сохранены с суффиксом _2, _3)." : "")
                      + (reportPath.Length > 0 ? $" Отчёт: {Path.GetFileName(reportPath)}" : "");
        SetStatus(summary, review + errors + duplicates > 0);
        Log.Info(summary);
    }

    // ---------- состояние ----------

    private void SetRunning(bool running)
    {
        _running = running;
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        AddFilesButton.IsEnabled = AddFolderButton.IsEnabled = RemoveButton.IsEnabled = ClearButton.IsEnabled = !running;
        FilesGrid.IsReadOnly = running;      // список можно листать во время работы
        UpdateReviewButton();
    }

    private void SetStatus(string text, bool warning)
    {
        StatusText.Text = text;
        StatusText.Foreground = warning ? (System.Windows.Media.Brush)FindResource("Warn") : System.Windows.Media.Brushes.Black;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_running)
        {
            if (MessageBox.Show(this, "Идёт обработка. Остановить и закрыть программу?", "Сортировщик сканов", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            { e.Cancel = true; return; }
            _cts?.Cancel();
        }
    }

    // ---------- демо для проверки внешнего вида ----------

    private void FillDemo()
    {
        string[] files = { "Скан 001.pdf", "Скан 002.pdf", "Скан 003.pdf", "Скан 004.pdf", "Скан 005.pdf", "Скан 006.pdf", "Скан 007.pdf" };
        foreach (var f in files) _items.Add(new BatchItem(@"D:\Входящие\" + f, "Завещания") { PageCount = 2 });
        var typeDir = @"D:\Сканы\Завещания\2026\";
        _items[0].TargetPath = typeDir + "2026-1-829.pdf"; _items[0].Status = ItemStatus.Done;
        _items[1].TargetPath = typeDir + "2026-1-830.pdf"; _items[1].Status = ItemStatus.Done;
        _items[2].TargetPath = typeDir + "2026-1-831_2.pdf"; _items[2].Note = "такой номер уже был, сохранён как 2026-1-831_2.pdf"; _items[2].Status = ItemStatus.Done;
        _items[3].Note = "номер распознан неуверенно"; _items[3].Status = ItemStatus.NeedsReview;
        _items[4].Note = "номер не найден"; _items[4].Status = ItemStatus.NeedsReview;
        _items[5].Note = "не удалось прочитать PDF (файл повреждён)"; _items[5].Status = ItemStatus.Error;
        _items[6].DocType = "Сделки";
        Progress.Maximum = 7; Progress.Value = 5; ProgressText.Text = "5 из 7";
        SetStatus("Готово. Сохранено: 3; нужна проверка: 2; пропущено: 0; ошибок: 1.", true);
    }
}
