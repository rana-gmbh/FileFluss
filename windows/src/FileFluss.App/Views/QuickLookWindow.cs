// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileFluss.App.Services;
using FileFluss.App.ViewModels;
using FileFluss.App.Views.Preview;
using FileFluss.Core.Formatting;
using FileFluss.Core.Models;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views;

/// <summary>
/// Quick Look: Space previews the selected file and follows the selection as the arrow keys
/// move it, like the macOS panel. Images, text and media are shown directly; anything else
/// goes through Windows' preview handlers (PDF, Office…), with a large icon and the file's
/// facts as the last resort. Cloud files are fetched into the open cache first.
/// </summary>
public sealed class QuickLookWindow : Window
{
    private static QuickLookWindow? _current;
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "markdown", "log", "csv", "tsv", "json", "xml", "yml", "yaml", "ini", "cfg", "conf", "toml", "cs", "swift",
        "js", "ts", "py", "rb", "go", "rs", "java", "kt", "c", "cpp", "h", "hpp", "css", "scss", "html", "htm", "sh", "ps1", "bat",
        "cmd", "sql", "gitignore", "editorconfig", "srt", "vtt", "plist", "strings", "resx", "csproj", "sln", "props", "lua", "php",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "ico", "webp", "heic", "heif", "jxr", "wdp",
    };

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "m4v", "mov", "wmv", "avi", "mkv", "mp3", "m4a", "wav", "wma", "aac", "flac",
    };

    private readonly MainViewModel _main;
    private readonly PanelViewModel _panel;
    private readonly ContentControl _content = new();
    private readonly TextBlock _caption = new() { Margin = new Thickness(14, 8, 14, 10), TextTrimming = TextTrimming.CharacterEllipsis };
    private CancellationTokenSource? _loading;
    private PreviewHandlerHost? _handlerHost;
    private bool _closed;

    private QuickLookWindow(MainViewModel main, PanelViewModel panel)
    {
        _main = main;
        _panel = panel;
        Width = 820;
        Height = 620;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var root = new DockPanel();
        DockPanel.SetDock(_caption, Dock.Bottom);
        root.Children.Add(_caption);
        root.Children.Add(_content);
        Content = root;

        panel.PropertyChanged += OnPanelChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            panel.PropertyChanged -= OnPanelChanged;
            _loading?.Cancel();
            if (_content.Content is MediaElement media)
            {
                media.Close();
            }

            _content.Content = null;
            _current = null;
        };

        // While its window still exists: a preview handler is unloaded and released (and its
        // file let go) instead of being parked with the hosted window until a finalizer runs.
        Closing += (_, _) =>
        {
            _handlerHost?.Dispose();
            _handlerHost = null;
        };
        // Closed by Esc or by the Quick Look shortcut itself — whatever it is bound to (F3 in
        // the Commander preset, or the user's own key), not Space alone (macOS 1.6).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || IsQuickLookShortcut(main, e))
            {
                e.Handled = true;
                Close();
            }
        };
    }

    private static bool IsQuickLookShortcut(MainViewModel main, KeyEventArgs e)
        => MainWindow.ShortcutFromKey(e) is { } pressed && main.Services.Keyboard.CommandFor(pressed) == Core.Commands.KeyboardCommand.QuickLook;

    /// <summary>
    /// Closes the preview if one is open. The preview doesn't take focus — the arrow keys keep
    /// moving the list's selection — so Esc pressed in the list has to reach it from there.
    /// </summary>
    public static bool CloseIfOpen()
    {
        if (_current is not { } open)
        {
            return false;
        }

        open.Close();
        return true;
    }

    public static void Toggle(Window owner, MainViewModel main, PanelViewModel panel)
    {
        if (_current is not null)
        {
            _current.Close();
            return;
        }

        if (panel.Selection.FirstOrDefault() is not { IsDirectory: false })
        {
            return;
        }

        _current = new QuickLookWindow(main, panel) { Owner = owner };
        _current.Show();
        _current.ShowSelection();
    }

    private void OnPanelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PanelViewModel.Selection))
        {
            ShowSelection();
        }
    }

    /// <summary>Previews never take FileFluss down: whatever a file or its handler does wrong ends in the facts view.</summary>
    private async void ShowSelection()
    {
        if (_closed || _panel.Selection.FirstOrDefault() is not { } row || row.IsDirectory)
        {
            return;
        }

        try
        {
            await ShowAsync(row);
        }
        catch (Exception e)
        {
            Core.Diagnostics.SupportLogger.Shared.Log($"Quick Look of {row.Name} failed: {e.GetType().Name}: {e.Message}", "QuickLook", Core.Diagnostics.LogLevel.Notice);
            if (!_closed)
            {
                ShowFacts(row, null);
            }
        }
    }

    private async Task ShowAsync(FileRowViewModel row)
    {
        _loading?.Cancel();
        var cts = _loading = new CancellationTokenSource();
        var item = row.Item;
        Title = item.Name;
        _caption.Text = $"{item.Name}  ·  {ByteFormatter.Format(item.Size)}  ·  {row.DateText}";
        string? path = item.Path;
        if (_panel.Archive is { } archive)
        {
            // Nothing inside an archive exists on disk yet: just this entry is unpacked.
            ShowFacts(row, L("Loading preview…"));
            path = await _main.PreviewArchiveEntryAsync(archive, item);
            if (cts.IsCancellationRequested || _closed)
            {
                return;
            }
        }
        else if (!_panel.IsLocal)
        {
            ShowFacts(row, L("Loading preview…"));

            // The token stops the download once Quick Look moves on — unless an "open" of the
            // same file is waiting for it too (MainViewModel shares the download).
            path = await _main.OpenCloudFileAsync(_panel, item, launch: false, cts.Token);
            if (cts.IsCancellationRequested || _closed)
            {
                return;
            }
        }

        if (path is null)
        {
            ShowFacts(row, null);
            return;
        }

        // Everything that touches the file (whether it's there, decoding a photo, reading a text
        // file) runs off the UI thread: on a share or a slow disk each of these used to stall
        // the arrow keys. Only the result is put on screen, and only if it's still wanted.
        var box = DecodeBox();
        var preview = await Task.Run(() => Prepare(path, box));
        if (cts.IsCancellationRequested || _closed)
        {
            return;
        }

        try
        {
            switch (preview)
            {
                case ImagePreview image:
                    _content.Content = new Image { Source = image.Bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(8) };
                    break;
                case TextPreview text:
                    ShowText(text.Text);
                    break;
                case MediaPreview:
                    ShowMedia(path);
                    break;
                case HandlerPreview:
                    // Preview handlers are COM objects living on this (STA) thread.
                    _handlerHost ??= new PreviewHandlerHost();
                    _content.Content = _handlerHost;
                    _handlerHost.Show(path);
                    break;
                default:
                    ShowFacts(row, null);
                    break;
            }
        }
        catch (Exception e) when (IsUnpreviewable(e))
        {
            ShowFacts(row, null);
        }
    }

    /// <summary>Decoders and players reject damaged or exotic files in all of these ways.</summary>
    private static bool IsUnpreviewable(Exception e)
        => e is IOException or NotSupportedException or UnauthorizedAccessException or FileFormatException or
                ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or OverflowException;

    private abstract record Preview;

    private sealed record ImagePreview(BitmapSource Bitmap) : Preview;

    private sealed record TextPreview(string Text) : Preview;

    private sealed record MediaPreview : Preview;

    private sealed record HandlerPreview : Preview;

    private sealed record FactsPreview : Preview;

    /// <summary>
    /// The largest size, in device pixels, a photo is decoded to: the window as it is (at least
    /// 1600 × 1200, so making it bigger afterwards stays sharp) at the screen's scale. A 40 MP
    /// photo decoded in full took seconds and hundreds of MB to show in an 800-pixel window.
    /// </summary>
    private Size DecodeBox()
    {
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var width = Math.Max(ActualWidth > 0 ? ActualWidth : Width, 1600);
        var height = Math.Max(ActualHeight > 0 ? ActualHeight : Height, 1200);
        return new Size(width * scale, height * scale);
    }

    /// <summary>What to show for <paramref name="path"/>, worked out on a background thread.</summary>
    private static Preview Prepare(string path, Size box)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new FactsPreview();
            }

            var extension = Path.GetExtension(path).TrimStart('.');
            if (ImageExtensions.Contains(extension))
            {
                return new ImagePreview(DecodeImage(path, box));
            }

            if (MediaExtensions.Contains(extension))
            {
                return new MediaPreview();
            }

            if (TextExtensions.Contains(extension) || (extension.Length == 0 && LooksLikeText(path)))
            {
                return new TextPreview(ReadText(path));
            }

            return PreviewHandlerHost.CanPreview(path) ? new HandlerPreview() : new FactsPreview();
        }
        catch (Exception e) when (IsUnpreviewable(e))
        {
            return new FactsPreview();
        }
    }

    /// <summary>A frozen bitmap no larger than <paramref name="box"/>, so it can cross to the UI thread.</summary>
    internal static BitmapSource DecodeImage(string path, Size box)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        // The header alone says how big the picture is; the pixels are decoded once, at the size
        // they'll be shown.
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        int pixelWidth = frame.PixelWidth, pixelHeight = frame.PixelHeight;
        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        var fit = Math.Min(box.Width / Math.Max(1, pixelWidth), box.Height / Math.Max(1, pixelHeight));
        if (fit < 1)
        {
            // One side only, so the aspect ratio is kept.
            image.DecodePixelWidth = Math.Max(1, (int)Math.Round(pixelWidth * fit));
        }

        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string ReadText(string path)
    {
        const int limit = 512 * 1024;
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[limit];
        var read = reader.ReadBlock(buffer, 0, limit);
        return new string(buffer, 0, read);
    }

    private void ShowMedia(string path)
    {
        var media = new MediaElement { Source = new Uri(path), LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform };
        _content.Content = media;
        media.Play();
    }

    private void ShowText(string text)
    {
        _content.Content = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(4),
        };
    }

    private void ShowFacts(FileRowViewModel row, string? note)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new Image { Source = IconCache.For(row.Item, _panel.IsLocal, 256), Width = 128, Height = 128 });
        stack.Children.Add(new TextBlock { Text = row.Name, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4), HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 600, TextAlignment = TextAlignment.Center });
        stack.Children.Add(new TextBlock { Text = $"{row.KindText} · {row.SizeText}", HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.75 });
        stack.Children.Add(new TextBlock { Text = row.DateText, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.75 });
        if (note is not null)
        {
            stack.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 160, Height = 3, Margin = new Thickness(0, 16, 0, 0) });
        }

        _content.Content = stack;
    }

    private static bool LooksLikeText(string path)
    {
        var buffer = new byte[4096];
        using var stream = File.OpenRead(path);
        var read = stream.Read(buffer, 0, buffer.Length);
        return read > 0 && !buffer.AsSpan(0, read).Contains((byte)0);
    }
}
