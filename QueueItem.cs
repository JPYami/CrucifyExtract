using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using CrucifyExtract.Services;

namespace CrucifyExtract;

public enum ItemState
{
    Queued,
    Working,
    Done,
    Replaced,
    Skipped,
    Failed
}

public static class Palette
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = new();

    public static SolidColorBrush Brush(string hex)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(hex, out var cached)) return cached;

            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Cache[hex] = brush;
            return brush;
        }
    }
}

public sealed class QueueItem : INotifyPropertyChanged
{
    private string _name;
    private string _detail;
    private string _statusText = "Queued";
    private Brush _statusBrush = Palette.Brush("#6E6883");
    private double _progress;
    private Visibility _barVisibility = Visibility.Collapsed;
    private ItemState _state = ItemState.Queued;

    public QueueItem(string sourcePath, bool isFolder, string name, string detail)
    {
        SourcePath = sourcePath;
        IsFolder = isFolder;
        _name = name;
        _detail = detail;
    }

    public string SourcePath { get; }
    public bool IsFolder { get; }
    public ItemState State => _state;

    public string Name
    {
        get => _name;
        private set => Set(ref _name, value);
    }

    public string Detail
    {
        get => _detail;
        private set => Set(ref _detail, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        private set => Set(ref _statusBrush, value);
    }

    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, Math.Clamp(value, 0, 1));
    }

    public Visibility BarVisibility
    {
        get => _barVisibility;
        private set => Set(ref _barVisibility, value);
    }

    public void Begin(string message)
    {
        _state = ItemState.Working;
        StatusText = message;
        StatusBrush = Palette.Brush("#FF2E63");
        BarVisibility = Visibility.Visible;
        Progress = 0;
    }

    public void Complete(InstallResult result)
    {
        BarVisibility = Visibility.Collapsed;

        if (result.Ok)
        {
            _state = result.Replaced ? ItemState.Replaced : ItemState.Done;
            StatusText = result.Replaced ? "Replaced" : "Installed";
            StatusBrush = Palette.Brush(result.Replaced ? "#A78BFA" : "#34D399");
            Progress = 1;

            var titles = result.Packages
                .Select(p => p.Title ?? p.Name)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct()
                .ToList();

            if (titles.Count > 0)
            {
                var shown = string.Join(" \u00b7 ", titles.Take(3));
                if (titles.Count > 3) shown += $" +{titles.Count - 3}";
                Detail = shown;
            }
            return;
        }

        if (result.Skipped > 0)
        {
            _state = ItemState.Skipped;
            StatusText = "Skipped";
            StatusBrush = Palette.Brush("#FBBF24");
            Progress = 0;
            return;
        }

        Fail(result.Error ?? "Failed");
    }

    public void Fail(string message)
    {
        _state = ItemState.Failed;
        StatusText = Shorten(message);
        StatusBrush = Palette.Brush("#FB7185");
        BarVisibility = Visibility.Collapsed;
        Progress = 0;
    }

    public void Cancel()
    {
        _state = ItemState.Queued;
        StatusText = "Cancelled";
        StatusBrush = Palette.Brush("#6E6883");
        BarVisibility = Visibility.Collapsed;
        Progress = 0;
    }

    public void Reset()
    {
        _state = ItemState.Queued;
        StatusText = "Queued";
        StatusBrush = Palette.Brush("#6E6883");
        BarVisibility = Visibility.Collapsed;
        Progress = 0;
    }

    private static string Shorten(string message)
    {
        var single = message.Replace("\r", " ").Replace("\n", " ").Trim();
        return single.Length <= 60 ? single : single[..57] + "\u2026";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
