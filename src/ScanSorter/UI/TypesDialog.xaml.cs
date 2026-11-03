using System.Windows;
using System.Windows.Input;
using ScanSorter.Core;

namespace ScanSorter.UI;

public partial class TypesDialog : Window
{
    public List<string> Types { get; private set; }

    public TypesDialog(IEnumerable<string> types)
    {
        InitializeComponent();
        Types = types.ToList();
        Refresh(null);
    }

    private void Refresh(int? select)
    {
        TypesList.Items.Clear();
        foreach (var t in Types) TypesList.Items.Add(t);
        if (select is int i && i >= 0 && i < Types.Count) TypesList.SelectedIndex = i;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (NewBox.Text.Trim().Length == 0) return;
        var name = Naming.SafeName(NewBox.Text.Trim());
        if (Types.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
        { MessageBox.Show(this, "Такой тип уже есть.", "Типы документов"); return; }
        Types.Add(name);
        NewBox.Clear();
        Refresh(Types.Count - 1);
    }

    private void NewBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Add_Click(sender, e); e.Handled = true; }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        int i = TypesList.SelectedIndex;
        if (i < 0) return;
        if (Types.Count == 1) { MessageBox.Show(this, "Должен остаться хотя бы один тип.", "Типы документов"); return; }
        Types.RemoveAt(i);
        Refresh(Math.Min(i, Types.Count - 1));
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        int i = TypesList.SelectedIndex;
        if (i < 0) return;
        var dlg = new InputDialog("Переименовать тип", "Новое название (оно же имя папки):", Types[i]) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var name = Naming.SafeName(dlg.Value.Trim());
        if (Types.Where((_, k) => k != i).Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
        { MessageBox.Show(this, "Такой тип уже есть.", "Типы документов"); return; }
        Types[i] = name;
        Refresh(i);
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int delta)
    {
        int i = TypesList.SelectedIndex, j = i + delta;
        if (i < 0 || j < 0 || j >= Types.Count) return;
        (Types[i], Types[j]) = (Types[j], Types[i]);
        Refresh(j);
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
