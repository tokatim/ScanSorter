using System.Windows;
using ScanSorter.Licensing;

namespace ScanSorter.UI;

/// <summary>Окно активации: показывает код компьютера и принимает ключ, выданный разработчиком.</summary>
public partial class ActivationWindow : Window
{
    public ActivationWindow(string reason)
    {
        InitializeComponent();
        ReasonText.Text = reason;
        CodeBox.Text = MachineId.Code;
        Loaded += (_, _) => KeyBox.Focus();
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(CodeBox.Text); } catch { /* буфер занят другой программой */ }
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try { if (Clipboard.ContainsText()) KeyBox.Text = Clipboard.GetText(); } catch { }
    }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        var st = LicenseManager.Install(KeyBox.Text);
        if (!st.IsValid) { ErrorText.Text = st.Message; return; }
        DialogResult = true;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
