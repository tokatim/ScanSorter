using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScanSorter.Core;
using ScanSorter.Ocr;
using ScanSorter.Pdf;

namespace ScanSorter.UI;

/// <summary>
/// Проверка номеров: все файлы, где нужна проверка, в одном окне. Счётчик «3 / 30», переключение между файлами стрелками,
/// ввод «год → реестр → номер» с автопереходом. В поле «Номер» допустимы цифры, буква и тире (например, В-37).
/// </summary>
public partial class ReviewWindow : Window
{
    private sealed class State
    {
        public string Year = "", Vol = "", Num = "";
        public volatile bool Loaded;
        public List<byte[]> Pages = new();
        public List<int> PageNumbers = new();
        public int Total;
        public string? Error;
        public Task? Loading;
        public List<(RegNumber Number, int Votes)> Candidates = new();
    }

    private readonly IReadOnlyList<BatchItem> _items;
    private readonly State[] _state;
    private readonly bool _noVolume;
    private readonly string _outputDir;
    private readonly Action<BatchItem, RegNumber> _save;
    private int _index = -1;
    private int _page;

    public int SavedCount { get; private set; }
    /// <summary>Файлы, сохранённые в этом окне (для вопроса об исходниках).</summary>
    public List<BatchItem> SavedItems { get; } = new();

    /// <param name="saveOverride">Для проверок: заменяет сохранение файла.</param>
    public ReviewWindow(IReadOnlyList<BatchItem> items, int? manualYear, bool noVolume, string outputDir, Action<BatchItem, RegNumber>? saveOverride = null)
    {
        InitializeComponent();
        _items = items;
        _noVolume = noVolume;
        _outputDir = outputDir;
        _save = saveOverride ?? ((item, number) => BatchProcessor.Save(item, number, _outputDir));
        if (noVolume) VolPanel.Visibility = Visibility.Collapsed;
        if (noVolume) VolColumn.Width = new GridLength(0);

        _state = new State[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            var best = items[i].Search?.Best;
            var st = new State { Year = best?.Year?.ToString() ?? manualYear?.ToString() ?? "" };
            if (best != null)
            {
                if (noVolume) st.Num = best.Vol.Length > 0 ? $"{best.Vol}-{best.Num}" : best.Num.ToString();
                else { st.Vol = best.Vol; st.Num = best.Num.ToString(); }
            }
            st.Candidates = items[i].Search?.Candidates.ToList() ?? new();
            _state[i] = st;
        }

        SetupTyping();
        Loaded += (_, _) => _ = ShowItemAsync(FirstUnresolved(0) ?? 0);
    }

    // ---------- переключение между файлами ----------

    private int? FirstUnresolved(int from)
    {
        for (int i = from; i < _items.Count; i++) if (_items[i].Status == ItemStatus.NeedsReview) return i;
        return null;
    }

    private void StoreFields()
    {
        if (_index < 0) return;
        var st = _state[_index];
        st.Year = YearBox.Text; st.Vol = VolBox.Text; st.Num = NumBox.Text;
    }

    private Task EnsureLoaded(int i)
    {
        if (i < 0 || i >= _items.Count) return Task.CompletedTask;
        var st = _state[i];
        if (st.Loaded) return Task.CompletedTask;
        return st.Loading ??= Task.Run(() =>
        {
            try
            {
                using var pdf = PdfSource.Open(_items[i].SourcePath);
                int total = pdf.PageCount, first = Math.Max(0, total - 8);
                var pages = new List<byte[]>(); var nums = new List<int>();
                for (int p = first; p < total; p++) { pages.Add(pdf.RenderJpeg(p, 85)); nums.Add(p + 1); }
                st.Pages = pages; st.PageNumbers = nums; st.Total = total;
            }
            catch (Exception ex) { st.Error = ex.Message; }
            st.Loaded = true;
        });
    }

    private async Task ShowItemAsync(int i)
    {
        StoreFields();
        _index = i;
        var item = _items[i];
        var st = _state[i];

        CounterText.Text = $"{i + 1} / {_items.Count}";
        FileNameText.Text = item.FileName;
        FileNameText.ToolTip = item.SourcePath;
        PrevItemButton.IsEnabled = i > 0;
        NextItemButton.IsEnabled = i < _items.Count - 1;
        UpdateInfo();

        YearBox.Text = st.Year; VolBox.Text = st.Vol; NumBox.Text = st.Num;
        ErrorText.Text = "";
        bool editable = item.Status != ItemStatus.Done;
        foreach (var tb in new[] { YearBox, VolBox, NumBox }) tb.IsReadOnly = !editable;
        SaveButton.IsEnabled = editable;

        if (editable)         // курсор сразу в первое пустое поле, не дожидаясь загрузки страниц
        {
            var target = string.IsNullOrEmpty(YearBox.Text) ? YearBox
                       : (!_noVolume && string.IsNullOrEmpty(VolBox.Text)) ? VolBox
                       : NumBox;
            target.Focus(); target.SelectAll();
        }

        CandidatesList.Items.Clear();
        foreach (var c in st.Candidates) CandidatesList.Items.Add($"{c.Number}   (совпало чтений: {c.Votes})");
        CandidatesHeader.Visibility = CandidatesList.Visibility = st.Candidates.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdatePreview();

        if (!st.Loaded)
        {
            PageImage.Source = null;
            PageLabel.Text = "Загрузка страниц…";
            await EnsureLoaded(i);
            if (_index != i) return;                    // пока грузилось, перешли к другому файлу
        }
        if (st.Error != null || st.Pages.Count == 0)
        {
            PageImage.Source = null;
            PageLabel.Text = "Не удалось открыть файл";
            MessageText.Text = (item.FileName + ": не удалось открыть (" + st.Error + ").");
            return;
        }

        int want = item.Search is { PageIndex: >= 0 } s ? st.PageNumbers.IndexOf(s.PageIndex + 1) : -1;
        _page = want >= 0 ? want : st.Pages.Count - 1;
        ShowPage();

        _ = EnsureLoaded(i + 1);                        // следующий файл подготавливаем заранее
    }

    private void UpdateInfo()
    {
        var item = _items[_index];
        MessageText.Text = item.Status switch
        {
            ItemStatus.NeedsReview => item.Note + ".",
            ItemStatus.Done => "Сохранён: " + item.TargetText + (item.Note.Length > 0 ? " (" + item.Note + ")" : ""),
            ItemStatus.Skipped => "Пропущен. Можно вернуться и сохранить.",
            _ => item.StatusText,
        };
        MessageText.Foreground = item.Status == ItemStatus.Done ? (System.Windows.Media.Brush)FindResource("Ok") : System.Windows.Media.Brushes.Black;
        int left = _items.Count(i => i.Status == ItemStatus.NeedsReview);
        LeftText.Text = $"Осталось проверить: {left}";
    }

    private void PrevItem_Click(object sender, RoutedEventArgs e) { if (_index > 0) _ = ShowItemAsync(_index - 1); }
    private void NextItem_Click(object sender, RoutedEventArgs e) { if (_index < _items.Count - 1) _ = ShowItemAsync(_index + 1); }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Key == Key.Right) { NextItem_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.Left) { PrevItem_Click(sender, e); e.Handled = true; }
        }
    }

    // ---------- страницы ----------

    private void ShowPage()
    {
        var st = _state[_index];
        var bmp = ImageUtil.Decode(st.Pages[_page]);
        PageImage.Source = bmp;
        PageLabel.Text = $"Страница {st.PageNumbers[_page]} из {st.Total}";
        Dispatcher.BeginInvoke(new Action(() =>
        {
            double fit = Math.Max(0.2, (ImageScroll.ViewportWidth - 20) / bmp.PixelWidth);   // по ширине окна
            ZoomSlider.Value = Math.Min(1.5, fit);
            ImageScroll.ScrollToBottom();                                                   // номер стоит внизу страницы
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ImageScale == null) return;
        ImageScale.ScaleX = ImageScale.ScaleY = e.NewValue;
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e) { if (_index >= 0 && _page > 0) { _page--; ShowPage(); } }
    private void NextPage_Click(object sender, RoutedEventArgs e) { if (_index >= 0 && _page < _state[_index].Pages.Count - 1) { _page++; ShowPage(); } }

    // ---------- ввод ----------

    /// <summary>
    /// Ввод без мыши: год (4 цифры) → реестр (1 символ) → номер. Переход выполняется сам; Backspace в пустом поле возвращает назад.
    /// В «Номер» допустимы цифры, буквы и тире.
    /// </summary>
    private void SetupTyping()
    {
        YearBox.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        NumBox.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(c => char.IsLetterOrDigit(c) || c == '-');
        VolBox.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsLetterOrDigit);

        YearBox.TextChanged += (_, _) => { if (YearBox.IsKeyboardFocused && YearBox.Text.Length == 4) GoTo(_noVolume ? NumBox : VolBox); };
        VolBox.TextChanged += (_, _) => { if (VolBox.IsKeyboardFocused && VolBox.Text.Length == 1) GoTo(NumBox); };
        foreach (var tb in new[] { YearBox, VolBox, NumBox }) tb.TextChanged += (_, _) => UpdatePreview();

        VolBox.PreviewKeyDown += (_, e) => { if (e.Key == Key.Back && VolBox.Text.Length == 0) { GoTo(YearBox); e.Handled = true; } };
        NumBox.PreviewKeyDown += (_, e) => { if (e.Key == Key.Back && NumBox.Text.Length == 0) { GoTo(_noVolume ? YearBox : VolBox); e.Handled = true; } };
    }

    private static void GoTo(TextBox box) { box.Focus(); box.SelectAll(); }
    private void Box_GotFocus(object sender, RoutedEventArgs e) => (sender as TextBox)?.SelectAll();

    /// <summary>Номер из полей: (том, номер) либо null, если введено неверно.</summary>
    private (string vol, int num)? ReadNumber()
    {
        if (!RegNumberParser.TryParseNumberField(NumBox.Text, out var vol, out var num)) return null;
        if (!_noVolume && vol.Length == 0) vol = VolBox.Text.Trim();
        return (vol, num);
    }

    private void UpdatePreview()
    {
        if (PreviewName == null) return;
        var n = ReadNumber();
        var year = YearBox.Text.Length > 0 ? YearBox.Text : "…";
        PreviewName.Text = n == null ? "Файл: …"
            : "Файл: " + (n.Value.vol.Length == 0 ? $"{year}-{n.Value.num}" : $"{year}-{n.Value.vol}-{n.Value.num}") + ".pdf";
        ErrorText.Text = "";
    }

    private void Candidate_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_index < 0 || CandidatesList.SelectedIndex < 0 || _items[_index].Status == ItemStatus.Done) return;
        var c = _state[_index].Candidates[CandidatesList.SelectedIndex].Number;
        if (c.Year.HasValue) YearBox.Text = c.Year.Value.ToString();
        if (_noVolume) NumBox.Text = c.Vol.Length > 0 ? $"{c.Vol}-{c.Num}" : c.Num.ToString();
        else { VolBox.Text = c.Vol; NumBox.Text = c.Num.ToString(); }
    }

    // ---------- действия ----------

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_index < 0) return;
        var item = _items[_index];
        if (item.Status == ItemStatus.Done) return;

        if (!int.TryParse(YearBox.Text.Trim(), out var year) || year < 1990 || year > DateTime.Now.Year + 1)
        { ErrorText.Text = "Год должен быть числом от 1990 до " + (DateTime.Now.Year + 1) + "."; YearBox.Focus(); return; }
        var parsed = ReadNumber();
        if (parsed == null)
        { ErrorText.Text = "Номер: цифры, при необходимости буква и тире (например, 37 или В-37)."; NumBox.Focus(); return; }
        var (vol, num) = parsed.Value;
        if (vol.Length > 3 || vol.Any(ch => !char.IsLetterOrDigit(ch)))
        { ErrorText.Text = "Реестр: от 1 до 3 символов (цифры или буквы)."; VolBox.Focus(); return; }
        if (!_noVolume && vol.Length == 0)
        { ErrorText.Text = "Укажите реестр."; VolBox.Focus(); return; }

        try { _save(item, new RegNumber(year, vol, num)); }
        catch (Exception ex) { ErrorText.Text = "Не удалось сохранить: " + ex.Message; return; }
        SavedCount++;
        SavedItems.Add(item);
        Advance();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (_index < 0) return;
        var item = _items[_index];
        if (item.Status == ItemStatus.NeedsReview) { item.Status = ItemStatus.Skipped; item.Note = "пропущен оператором"; }
        Advance();
    }

    /// <summary>К следующему непроверенному файлу; если таких нет, окно закрывается.</summary>
    private void Advance()
    {
        UpdateInfo();
        var next = FirstUnresolved(_index + 1) ?? FirstUnresolved(0);
        if (next == null) { DialogResult = true; return; }
        _ = ShowItemAsync(next.Value);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = SavedCount > 0;
}
