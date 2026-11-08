using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CrucifyExtract.Services;

namespace CrucifyExtract;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args;

        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            SelfTest.Run(args);
            Shutdown();
            return;
        }

        if (args.Any(a => a.Equals("--detect", StringComparison.OrdinalIgnoreCase)))
        {
            SelfTest.Detect(args);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) => LogError(eventArgs.ExceptionObject as Exception);

        var settings = Settings.Load();
        var pending = ReadPendingQueue(args);

        var window = new MainWindow(settings, pending);
        MainWindow = window;
        window.Show();

        var shot = ArgValue(args, "--shot");
        if (!string.IsNullOrWhiteSpace(shot)) CaptureAfterLayout(window, shot);
    }

    /// <summary>Renders the window's own visual tree to a PNG, used for UI review.</summary>
    private static void CaptureAfterLayout(Window window, string path)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                var root = (FrameworkElement)window.Content;
                root.UpdateLayout();

                var width = Math.Max(1, (int)Math.Ceiling(root.ActualWidth));
                var height = Math.Max(1, (int)Math.Ceiling(root.ActualHeight));

                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                using (var stream = File.Create(path)) encoder.Save(stream);
                Console.Out.WriteLine("screenshot: " + Path.GetFullPath(path));
                Console.Out.Flush();
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine("screenshot failed: " + ex);
                Console.Out.Flush();
            }

            Current.Shutdown();
        };
        timer.Start();
    }

    private static string? ArgValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static List<string> ReadPendingQueue(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--queue", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length) return new List<string>();

        var file = args[index + 1];
        try
        {
            if (!File.Exists(file)) return new List<string>();
            var lines = File.ReadAllLines(file).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            File.Delete(file);
            return lines;
        }
        catch
        {
            return new List<string>();
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        MessageBox.Show(
            "Something went wrong:\n\n" + e.Exception.Message,
            "Crucify.Extract",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static void LogError(Exception? error)
    {
        if (error == null) return;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Crucify.Extract");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:u}] {error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // ignored
        }
    }
}
