using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CrucifyExtract.Services;
using Microsoft.Win32;

namespace CrucifyExtract;

public partial class MainWindow : Window
{
    private const string AppTitle = "Crucify.Extract";
    private const string DefaultDropSubtext = "RAR  \u00b7  ZIP  \u00b7  7Z  \u00b7  TAR  \u00b7  GZ      or click to browse";

    private readonly Settings _settings;
    private readonly List<string> _pending;
    private string? _addonsDir;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _dragHighlight;

    public ObservableCollection<QueueItem> Items { get; } = new();

    public MainWindow(Settings settings, IEnumerable<string>? pending = null)
    {
        InitializeComponent();

        _settings = settings;
        _pending = pending?.ToList() ?? new List<string>();

        DataContext = this;
        OverwriteToggle.IsChecked = _settings.Overwrite;
        UpdateQueueState();
    }

    // ============================================================ startup

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AdminBadge.Visibility = IsElevated() ? Visibility.Visible : Visibility.Collapsed;

        if (GameLocator.ResolveAddons(_settings.GamePath) is { } saved && _settings.GamePath != null)
            ApplyGame(_settings.GamePath, saved);
        else
            await DetectAsync();

        if (_pending.Count > 0) await AddPathsAsync(_pending);
    }

    private async Task DetectAsync()
    {
        SetPill("Scanning", "#FBBF24", "#2A2418");
        GamePathText.Text = "Scanning your drives\u2026";
        TargetText.Text = "Looking for Interface\\AddOns";
        SetStatus("Searching for your WoW Forever install\u2026", Tone.Work);

        var progress = new Progress<string>(message =>
        {
            if (!_busy) GamePathText.Text = message;
        });

        var found = await Task.Run(() => GameLocator.Detect(progress));

        if (found.Count > 0)
        {
            ApplyGame(found[0].Root, found[0].AddonsPath);
            if (found.Count > 1)
                SetStatus($"Found {found.Count} clients \u2014 using {Path.GetFileName(found[0].Root)}.", Tone.Ok);
            return;
        }

        _addonsDir = null;
        SetPill("Not set", "#FBBF24", "#2A2418");
        GamePathText.Text = "WoW Forever not found \u2014 click Browse";
        TargetText.Text = "Pick the folder that contains Wow.exe";
        SetStatus("Couldn't auto-detect the game folder. Use Browse to set it.", Tone.Warn);
        BlinkGameCard();
    }

    private void ApplyGame(string root, string addonsDir)
    {
        _addonsDir = addonsDir;

        if (!string.Equals(_settings.GamePath, root, StringComparison.OrdinalIgnoreCase))
        {
            _settings.GamePath = root;
            _settings.Save();
        }

        GamePathText.Text = root;
        TargetText.Text = addonsDir;
        SetPill("Detected", "#34D399", "#14261D");
        SetStatus("Game folder ready \u2014 drop your archives.", Tone.Ok);
    }

    // ============================================================ window chrome

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var dark = 1;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            var rounded = 2;
            DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
        }
        catch
        {
            // Older Windows builds simply keep square corners.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // ignored
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_busy) _cts?.Cancel();

        _settings.Overwrite = OverwriteToggle.IsChecked == true;
        _settings.Save();
    }

    // ============================================================ drag & drop

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (_busy) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        SetDragHighlight(true);
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!DropZone.IsMouseOver) SetDragHighlight(false);
        }), DispatcherPriority.Input);
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        SetDragHighlight(false);
        if (_busy) return;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            await AddPathsAsync(paths);
    }

    private void SetDragHighlight(bool on)
    {
        if (_dragHighlight == on) return;
        _dragHighlight = on;

        DashRing.Stroke = Palette.Brush(on ? "#FF2E63" : "#382E4C");
        DropZone.Background = Palette.Brush(on ? "#130C1A" : "#0A0810");
        DropZone.BorderBrush = Palette.Brush(on ? "#4A2038" : "#1D1727");
        DropSubtext.Text = on ? "Release to queue them up" : DefaultDropSubtext;

        var duration = TimeSpan.FromMilliseconds(on ? 180 : 320);
        DropGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty,
            new DoubleAnimation(on ? 28 : 0, duration));
        DropGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
            new DoubleAnimation(on ? 0.38 : 0, duration));

        if (on)
        {
            var pulse = new DoubleAnimation(1, 1.012, TimeSpan.FromMilliseconds(600))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Root.RenderTransformOrigin = new Point(0.5, 0.5);
            Root.RenderTransform = new ScaleTransform(1, 1);
            ((ScaleTransform)Root.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            ((ScaleTransform)Root.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        }
        else if (Root.RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = 1;
            scale.ScaleY = 1;
        }
    }

    // ============================================================ adding work

    private async void DropZone_Click(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;

        var dialog = new OpenFileDialog
        {
            Title = "Add add-on archives",
            Multiselect = true,
            Filter = "Archives (*.rar;*.zip;*.7z;*.tar;*.gz;*.tgz)|*.rar;*.zip;*.7z;*.tar;*.gz;*.tgz;*.bz2;*.xz|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) == true) await AddPathsAsync(dialog.FileNames);
    }

    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        if (_busy) return;

        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (list.Count == 0) return;

        SetStatus("Checking what you dropped\u2026", Tone.Work);

        var entries = await Task.Run(() => PathExpander.Expand(list));

        var added = 0;
        foreach (var entry in entries)
        {
            if (Items.Any(i => i.SourcePath.Equals(entry.Path, StringComparison.OrdinalIgnoreCase))) continue;
            Items.Add(BuildItem(entry));
            added++;
        }

        UpdateQueueState();

        if (added > 0)
            SetStatus($"{added} item{(added == 1 ? "" : "s")} queued \u2014 hit INSTALL ADD-ONS.", Tone.Ok);
        else if (entries.Count > 0)
            SetStatus("Those are already in the queue.", Tone.Warn);
        else
            SetStatus("Nothing usable in that drop \u2014 use RAR, ZIP, 7Z, TAR or GZ.", Tone.Warn);
    }

    private static QueueItem BuildItem(DropEntry entry)
    {
        if (entry.IsFolder)
        {
            var folder = Path.GetFileName(entry.Path.TrimEnd('\\', '/'));
            var tocs = CountToc(entry.Path);
            var folderDetail = tocs > 1 ? $"Folder \u00b7 {tocs} add-ons" : "Folder \u00b7 add-on";
            return new QueueItem(entry.Path, true, string.IsNullOrWhiteSpace(folder) ? "Add-on" : folder, folderDetail);
        }

        var name = Path.GetFileName(entry.Path);
        var kind = Path.GetExtension(entry.Path).TrimStart('.').ToUpperInvariant();

        string detail;
        try
        {
            detail = $"{kind} \u00b7 {FormatSize(new FileInfo(entry.Path).Length)}";
        }
        catch
        {
            detail = kind;
        }

        return new QueueItem(entry.Path, false, name, detail);
    }

    private static int CountToc(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.toc", SearchOption.AllDirectories).Count();
        }
        catch
        {
            return 1;
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    // ============================================================ install

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            RequestCancel();
            return;
        }

        if (string.IsNullOrEmpty(_addonsDir))
        {
            SetStatus("Set your WoW Forever folder first \u2014 hit Browse.", Tone.Warn);
            BlinkGameCard();
            return;
        }

        var queue = Items.Where(i => i.State == ItemState.Queued).ToList();
        if (queue.Count == 0)
        {
            SetStatus("Nothing to install \u2014 drop an archive first.", Tone.Warn);
            return;
        }

        _busy = true;
        _cts = new CancellationTokenSource();
        SetBusyUi(true);
        ProgressStrip.Visibility = Visibility.Visible;
        OverallBar.Value = 0;
        OverallText.Text = "0%";

        var installed = 0;
        var failed = 0;
        var accessDenied = false;
        var target = _addonsDir;
        var overwrite = _settings.Overwrite;

        try
        {
            for (var i = 0; i < queue.Count; i++)
            {
                if (_cts.IsCancellationRequested) break;

                var item = queue[i];
                item.Begin("Extracting\u2026");
                SetStatus($"Installing {item.Name}\u2026", Tone.Work);

                var progress = new Progress<double>(value => item.Progress = value);

                try
                {
                    var result = await Task.Run(() =>
                        Installer.Install(item.SourcePath, item.IsFolder, target, overwrite, progress, _cts.Token));

                    item.Complete(result);
                    if (result.Ok) installed++;
                    else failed++;
                }
                catch (OperationCanceledException)
                {
                    item.Cancel();
                    break;
                }
                catch (UnauthorizedAccessException)
                {
                    accessDenied = true;
                    item.Fail("Access denied \u2014 needs admin");
                    failed++;
                }
                catch (Exception ex)
                {
                    item.Fail(ex.Message);
                    failed++;
                }

                OverallBar.Value = (i + 1) / (double)queue.Count;
                OverallText.Text = $"{(int)(OverallBar.Value * 100)}%";
            }
        }
        finally
        {
            _busy = false;
            _cts?.Dispose();
            _cts = null;
            SetBusyUi(false);
            ProgressStrip.Visibility = Visibility.Collapsed;
            UpdateQueueState();
        }

        if (accessDenied)
        {
            SetStatus("Windows blocked the write \u2014 admin permission needed.", Tone.Warn);
            PromptElevate();
            return;
        }

        if (failed == 0 && installed > 0)
            SetStatus($"Done \u2014 {installed} archive{(installed == 1 ? "" : "s")} installed. Restart WoW to see them.", Tone.Ok);
        else if (installed > 0)
            SetStatus($"Finished: {installed} installed, {failed} failed.", Tone.Warn);
        else
            SetStatus("Nothing was installed \u2014 check the queue for details.", Tone.Error);
    }

    private void RequestCancel()
    {
        _cts?.Cancel();
        InstallButton.IsEnabled = false;
        SetStatus("Cancelling\u2026", Tone.Warn);
    }

    private void SetBusyUi(bool busy)
    {
        InstallButton.Content = busy ? "CANCEL" : "INSTALL ADD-ONS";
        InstallButton.IsEnabled = true;
        BrowseButton.IsEnabled = !busy;
        RescanButton.IsEnabled = !busy;
        DropZone.IsEnabled = !busy;
        DropZone.Cursor = busy ? Cursors.Arrow : Cursors.Hand;
        OverwriteToggle.IsEnabled = !busy;
        ClearButton.IsEnabled = !busy;
    }

    private void PromptElevate()
    {
        foreach (var item in Items.Where(i => i.State == ItemState.Failed)) item.Reset();

        var answer = MessageBox.Show(this,
            "Windows blocked writing into your AddOns folder (it lives in a protected location).\n\n" +
            "Restart Crucify.Extract as Administrator? Your queue comes with you.",
            "Administrator needed", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return;

        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;

            var queueFile = Path.Combine(Path.GetTempPath(), "crucify-queue.txt");
            File.WriteAllLines(queueFile, Items.Select(i => i.SourcePath));

            Process.Start(new ProcessStartInfo(exe, $"--queue \"{queueFile}\"")
            {
                UseShellExecute = true,
                Verb = "runas"
            });

            Application.Current.Shutdown();
        }
        catch
        {
            MessageBox.Show(this, "Elevation was cancelled.", AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // ============================================================ misc actions

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _settings.GamePath = null;
        _settings.Save();
        await DetectAsync();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var dialog = new OpenFolderDialog
        {
            Title = "Select your WoW Forever folder (the one with Wow.exe)",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true) return;

        var addons = GameLocator.ResolveAddons(dialog.FolderName);
        if (addons == null)
        {
            MessageBox.Show(this,
                "Couldn't find a WoW Forever client there.\n\nPick the folder that contains Wow.exe, Interface and Data.",
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var root = dialog.FolderName;
        var trimmed = Path.GetFileName(root.TrimEnd('\\'));
        if (trimmed.Equals("AddOns", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("Interface", StringComparison.OrdinalIgnoreCase))
        {
            root = GameRootFrom(root) ?? root;
        }

        ApplyGame(root, addons);
    }

    private static string? GameRootFrom(string path)
    {
        var dir = new DirectoryInfo(path);
        for (var i = 0; i < 3 && dir?.Parent != null; i++)
        {
            dir = dir.Parent;
            if (File.Exists(Path.Combine(dir.FullName, "Wow.exe"))) return dir.FullName;
        }
        return null;
    }

    private void OpenAddons_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_addonsDir))
        {
            SetStatus("No AddOns folder yet \u2014 set your game folder first.", Tone.Warn);
            BlinkGameCard();
            return;
        }

        try
        {
            Directory.CreateDirectory(_addonsDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_addonsDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't open the folder: " + ex.Message, Tone.Error);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Items.Clear();
        UpdateQueueState();
        SetStatus("Queue cleared.", Tone.Neutral);
    }

    // ============================================================ status helpers

    private enum Tone
    {
        Neutral,
        Ok,
        Work,
        Warn,
        Error
    }

    private void SetStatus(string message, Tone tone)
    {
        StatusMessage.Text = message;
        FooterDot.Fill = Palette.Brush(tone switch
        {
            Tone.Ok => "#34D399",
            Tone.Work => "#FF2E63",
            Tone.Warn => "#FBBF24",
            Tone.Error => "#FB7185",
            _ => "#8C84A3"
        });
    }

    private void SetPill(string text, string foreground, string background)
    {
        StatusPill.Background = Palette.Brush(background);
        StatusDot.Fill = Palette.Brush(foreground);
        StatusPillText.Foreground = Palette.Brush(foreground);
        StatusPillText.Text = text;
    }

    private void UpdateQueueState()
    {
        QueueCountText.Text = Items.Count == 1 ? "1 item" : $"{Items.Count} items";
        ClearButton.Visibility = Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BlinkGameCard()
    {
        var blink = new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(110))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3)
        };
        GameCard.BeginAnimation(OpacityProperty, blink);
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
