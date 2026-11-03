using System.Text;
using System.Windows;
using ScanSorter.Config;
using ScanSorter.Core;
using ScanSorter.Ocr;

namespace ScanSorter;

public partial class App : Application
{
    public static bool DemoMode { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Каждый поток распознавания работает на одном ядре: так несколько файлов обрабатываются одновременно без взаимных помех
        Environment.SetEnvironmentVariable("OMP_THREAD_LIMIT", "1");
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Необработанная ошибка", args.Exception);
            MessageBox.Show("Произошла ошибка: " + args.Exception.Message + "\nПодробности в папке log.", "Сортировщик сканов", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };


        // Лицензия: без неё программа не открывается, а предлагает активацию
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var status = Licensing.LicenseManager.Check();
        if (!DemoMode && !status.IsValid)
        {
            if (new UI.ActivationWindow(status.Message).ShowDialog() != true) { Shutdown(); return; }
        }

        base.OnStartup(e);
        var main = new MainWindow();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }

}
