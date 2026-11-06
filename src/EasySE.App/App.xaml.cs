using System.Windows;

namespace EasySE.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShowEditor(e.Args);
    }
    internal static (MainWindow Window, Task Loaded) ShowEditor(string[] arguments)
    {
        var main = new MainWindow();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler? loaded = null;
        loaded = async (_, _) =>
        {
            main.Loaded -= loaded;
            try { if (arguments.Length > 0) await main.LoadStartupMediaAsync(arguments); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        };
        main.Loaded += loaded;
        main.Show();
        return (main, completion.Task);
    }
}
