using System.Windows;
using ClaudeVideoEditor.Services;

namespace ClaudeVideoEditor;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        // Developer / CI modes (see README):
        //   --self-test <report.txt>   checks the core logic and exits 0/1
        //   --snapshot <dir>           saves a PNG of every screen and exits
        if (args.Length >= 2 && args[0] == "--self-test")
        {
            Shutdown(await DevTools.SelfTestAsync(args[1]));
            return;
        }

        if (args.Length >= 2 && args[0] == "--snapshot") DevTools.BeginSnapshot();
        var main = new MainWindow();
        MainWindow = main;
        main.Show();

        if (args.Length >= 2 && args[0] == "--snapshot")
        {
            await DevTools.SnapshotAsync(main, args[1]);
            Shutdown(0);
        }
    }
}
