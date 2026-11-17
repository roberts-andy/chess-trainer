using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChessTrainer.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject.ToString() ?? "Unknown startup error.");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new TrainerApplication();
        });
    }

    internal static void Log(string message)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChessTrainer");
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, "crash.log"), $"{DateTimeOffset.Now:O}\n{message}\n");
    }
}

public sealed partial class TrainerApplication : Application
{
    private MainWindow? window;

    public TrainerApplication()
    {
        UnhandledException += (_, args) =>
        {
            Program.Log(args.Exception.ToString());
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
    }
}
