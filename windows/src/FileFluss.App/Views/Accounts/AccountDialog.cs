// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileFluss.App.Services;
using FileFluss.App.Views.Dialogs;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Providers;
using Microsoft.Win32;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views.Accounts;

/// <summary>
/// Add Cloud Account and Edit Account. The provider grid mirrors the macOS sheet (Cloud
/// Storage Providers / Other Protocols / Cameras &amp; Devices); the sign-in form is generated
/// from the provider's <see cref="ConnectSpec"/>, so every provider gets the same behaviour:
/// Connect stays disabled until required fields are filled, errors appear inline, browser
/// sign-ins can be cancelled, and nothing is saved unless sign-in succeeded.
/// </summary>
public sealed class AccountDialog : Window, IConnectPrompt
{
    private readonly AppServices _services;
    private readonly Guid? _editingId;
    private readonly ContentControl _host = new();
    private readonly Dictionary<string, Func<string>> _readers = [];
    private readonly List<(ConnectField Field, Control Control)> _requiredControls = [];
    private CloudProvider? _provider;
    private ConnectSpec? _spec;
    private ConnectMode? _mode;
    private TextBlock? _status;
    private TextBlock? _error;
    private Button? _connect;
    private Button? _back;
    private ProgressBar? _busy;
    private CancellationTokenSource? _connecting;

    private AccountDialog(AppServices services, Guid? editingId)
    {
        _services = services;
        _editingId = editingId;
        Title = editingId is null ? L("Add Cloud Account") : L("Edit Account");
        Width = 640;
        Height = 720;
        MinWidth = 520;
        MinHeight = 420;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new ScrollViewer { Content = _host, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 16, 24, 20) };
        Closing += (_, _) => _connecting?.Cancel();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }

    /// <summary>Shows the provider grid, then the form. True when an account was added.</summary>
    public static bool Add(Window? owner, AppServices services, CloudProviderType? preselect = null)
    {
        var dialog = new AccountDialog(services, null) { Owner = MessageDialog.OwnerOrMain(owner) };
        if (preselect is { } type)
        {
            dialog.ShowForm(type);
        }
        else
        {
            dialog.ShowPicker();
        }

        return dialog.ShowDialog() == true;
    }

    /// <summary>The form for an existing account, prefilled with its non-secret values.</summary>
    public static bool Edit(Window? owner, AppServices services, Guid accountId)
    {
        if (services.Accounts.Find(accountId) is not { } account)
        {
            return false;
        }

        var dialog = new AccountDialog(services, accountId) { Owner = MessageDialog.OwnerOrMain(owner) };
        dialog.ShowForm(account.ProviderType);
        return dialog.ShowDialog() == true;
    }

    // MARK: - Provider grid

    private void ShowPicker()
    {
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = L("Add Cloud Account"), FontSize = 22, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        root.Children.Add(new TextBlock { Text = L("Select a cloud provider to connect:"), Style = (Style)FindResource("Caption"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 8) });
        var sections = new List<(TextBlock Heading, WrapPanel Grid)>();

        foreach (var (category, title) in new[]
                 {
                     (ProviderCategory.CloudStorage, "Cloud Storage Providers"),
                     (ProviderCategory.OtherProtocols, "Other Protocols"),
                     (ProviderCategory.Devices, "Cameras & Devices"),
                 })
        {
            var types = Enum.GetValues<CloudProviderType>()
                .Where(t => t.Category() == category && CloudProviderRegistry.IsAvailable(t))
                .OrderBy(t => t.DisplayName(), StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (types.Count == 0)
            {
                continue;
            }

            var heading = new TextBlock { Text = L(title), FontWeight = FontWeights.SemiBold, Margin = new Thickness(TileSpacing / 2, 18, 0, 6), Style = (Style)FindResource("Caption") };
            var grid = new WrapPanel { ItemWidth = TileWidth + TileSpacing, ItemHeight = TileHeight + TileSpacing, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var type in types)
            {
                grid.Children.Add(Tile(type));
            }

            sections.Add((heading, grid));
            root.Children.Add(heading);
            root.Children.Add(grid);
        }

        // Every section uses the same columns, as many whole tiles as fit, centred as one block:
        // tiles line up across sections, and resizing the window reflows them without stretching.
        void Arrange()
        {
            var available = Math.Max(0, root.ActualWidth);
            // At most six across: a maximised window shows a compact block, not one endless row.
            var columns = Math.Clamp((int)(available / (TileWidth + TileSpacing)), 1, 6);
            var width = columns * (TileWidth + TileSpacing);
            foreach (var (heading, grid) in sections)
            {
                grid.Width = width;
                heading.Width = width;
                heading.HorizontalAlignment = HorizontalAlignment.Center;
            }
        }

        root.SizeChanged += (_, _) => Arrange();

        var cancel = new Button { Content = L("Cancel"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0), MinWidth = 100, IsCancel = true };
        cancel.Click += (_, _) => Close();
        root.Children.Add(cancel);
        _host.Content = root;
    }

    private const double TileWidth = 124;
    private const double TileHeight = 112;
    private const double TileSpacing = 10;
    private const double LogoBox = 44;

    /// <summary>One provider: the logo in a fixed square, the name on up to two lines below — every tile the same size.</summary>
    private Button Tile(CloudProviderType type)
    {
        var layout = new Grid { Width = TileWidth - 16 };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(LogoBox) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        FrameworkElement logo = IconCache.LogoTile(type, LogoBox, TileWidth - 28) as FrameworkElement ?? new TextBlock
        {
            Text = Glyphs.Cloud,
            FontFamily = new FontFamily(Glyphs.FontFamily),
            FontSize = 30,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        layout.Children.Add(logo);

        var name = new TextBlock
        {
            Text = type.DisplayName(),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 36,
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = type.DisplayName(),
        };
        Grid.SetRow(name, 2);
        layout.Children.Add(name);

        var tile = new Button
        {
            Content = layout,
            Width = TileWidth,
            Height = TileHeight,
            Padding = new Thickness(8, 12, 8, 8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        System.Windows.Automation.AutomationProperties.SetName(tile, type.DisplayName());
        tile.Click += (_, _) => ShowForm(type);
        return tile;
    }

    // MARK: - Sign-in form

    private void ShowForm(CloudProviderType type)
    {
        _provider?.Dispose();
        _provider = null;
        try
        {
            _provider = _services.Accounts.CreateProvider(type, _editingId ?? Guid.NewGuid());
        }
        catch (NotSupportedException)
        {
            MessageDialog.Error(this, Title, L("This account type isn't available in this version."));
            return;
        }
        catch (Exception e)
        {
            // The account's stored settings couldn't be read back.
            MessageDialog.Error(this, Title, e.Message);
            return;
        }

        _spec = _provider.ConnectSpec;
        var prefill = _editingId is null ? new Dictionary<string, string>() : _provider.GetEditableConnectValues();
        _mode = prefill.TryGetValue(ConnectSpec.ModeKey, out var modeId) && _spec.Modes.FirstOrDefault(m => m.Id == modeId) is { } saved
            ? saved
            : _spec.Modes[0];
        BuildForm(type, prefill);
    }

    private void BuildForm(CloudProviderType type, IReadOnlyDictionary<string, string> prefill)
    {
        var root = new StackPanel { MaxWidth = 480 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 14) };
        if (IconCache.Logo(type) is { } logo)
        {
            header.Children.Add(new Image { Source = logo, Width = 28, Height = 28, Margin = new Thickness(0, 0, 10, 0) });
        }

        header.Children.Add(new TextBlock { Text = L("Sign in to {0}", type.DisplayName()), FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(header);

        if (_spec!.Modes.Count > 1)
        {
            var switcher = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
            foreach (var mode in _spec.Modes)
            {
                var option = new RadioButton { Content = L(mode.Title), IsChecked = mode == _mode, Margin = new Thickness(8, 0, 8, 0), GroupName = "mode" };
                option.Checked += (_, _) =>
                {
                    if (_mode != mode)
                    {
                        var current = CurrentValues().AsDictionary();
                        _mode = mode;
                        BuildForm(type, current);
                    }
                };
                switcher.Children.Add(option);
            }

            root.Children.Add(switcher);
        }

        _readers.Clear();
        _requiredControls.Clear();
        foreach (var field in _mode!.Fields)
        {
            root.Children.Add(BuildField(field, prefill));
        }

        _status = new TextBlock { Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        _error = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        _error.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        _busy = new ProgressBar { IsIndeterminate = true, Height = 3, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        root.Children.Add(_status);
        root.Children.Add(_error);
        root.Children.Add(_busy);

        var buttons = new DockPanel { Margin = new Thickness(0, 20, 0, 0), LastChildFill = false };
        _back = new Button { Content = _editingId is null ? L("Back") : L("Cancel"), MinWidth = 96 };
        _back.Click += (_, _) =>
        {
            _connecting?.Cancel();
            if (_editingId is null)
            {
                ShowPicker();
            }
            else
            {
                Close();
            }
        };
        DockPanel.SetDock(_back, Dock.Left);
        buttons.Children.Add(_back);

        _connect = new Button { Content = _editingId is null ? L("Connect") : L("Save"), MinWidth = 110, IsDefault = true };
        if (TryFindResource("AccentButtonStyle") is Style accent)
        {
            _connect.Style = accent;
        }

        _connect.Click += async (_, _) => await ConnectAsync();
        DockPanel.SetDock(_connect, Dock.Right);
        buttons.Children.Add(_connect);
        root.Children.Add(buttons);

        _host.Content = root;
        UpdateConnectEnabled();
        Dispatcher.BeginInvoke(() => FirstInput(root)?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private static Control? FirstInput(Panel root)
        => root.Children.OfType<FrameworkElement>()
            .SelectMany(c => c is Panel p ? p.Children.OfType<FrameworkElement>() : [c])
            .OfType<Control>()
            .FirstOrDefault(c => c is TextBox or PasswordBox);

    private FrameworkElement BuildField(ConnectField field, IReadOnlyDictionary<string, string> prefill)
    {
        var initial = prefill.GetValueOrDefault(field.Key) ?? field.DefaultValue ?? string.Empty;
        var margin = new Thickness(0, 0, 0, 10);
        switch (field.Kind)
        {
            case ConnectFieldKind.Info:
                return new TextBlock { Text = L(field.Label), TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Caption"), TextTrimming = TextTrimming.None, Margin = margin };
            case ConnectFieldKind.Warning:
                var warning = new TextBlock { Text = L(field.Label), TextWrapping = TextWrapping.Wrap, Margin = margin, FontWeight = FontWeights.SemiBold };
                warning.Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0x7A, 0x00));
                return warning;
            case ConnectFieldKind.Link:
                var link = new TextBlock { Margin = margin };
                var hyperlink = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(L(field.Label)));
                hyperlink.Click += (_, _) => BrowserLauncher.Open(field.Url!);
                link.Inlines.Add(hyperlink);
                return link;
            case ConnectFieldKind.Checkbox:
                var check = new CheckBox { Content = L(field.Label), IsChecked = bool.TryParse(initial, out var on) && on, Margin = margin, ToolTip = field.Help is null ? null : L(field.Help) };
                _readers[field.Key] = () => (check.IsChecked == true).ToString();
                return check;
        }

        var stack = new StackPanel { Margin = margin };
        stack.Children.Add(new TextBlock { Text = L(field.Label) + (field.Required ? string.Empty : " " + L("(optional)")), Margin = new Thickness(0, 0, 0, 4) });
        Control input;
        switch (field.Kind)
        {
            case ConnectFieldKind.Password:
                var password = new PasswordBox { Password = initial };
                password.PasswordChanged += (_, _) => UpdateConnectEnabled();
                _readers[field.Key] = () => password.Password;
                input = password;
                break;
            case ConnectFieldKind.Choice:
                var combo = new ComboBox { IsEditable = field.AllowsCustomValue, DisplayMemberPath = "Label", SelectedValuePath = "Value", ItemsSource = field.Choices.Select(c => new ConnectChoice(c.Value, L(c.Label))).ToList() };
                if (field.AllowsCustomValue)
                {
                    combo.Text = initial;
                }

                combo.SelectedValue = initial;
                combo.SelectionChanged += (_, _) => UpdateConnectEnabled();
                combo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdateConnectEnabled()));
                _readers[field.Key] = () => combo.SelectedValue as string ?? (field.AllowsCustomValue ? combo.Text : string.Empty);
                input = combo;
                break;
            case ConnectFieldKind.FileContents:
                var row = new DockPanel();
                var name = new TextBlock { Text = L("No file chosen"), VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("Caption") };
                var contents = initial;
                var choose = new Button { Content = L("Choose File…"), Margin = new Thickness(0, 0, 10, 0) };
                choose.Click += (_, _) =>
                {
                    var dialog = new OpenFileDialog { Filter = field.FileFilter ?? L("All files") + "|*.*" };
                    var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
                    if (Directory.Exists(ssh))
                    {
                        dialog.InitialDirectory = ssh;
                    }

                    if (dialog.ShowDialog(this) == true)
                    {
                        try
                        {
                            contents = File.ReadAllText(dialog.FileName);
                            name.Text = Path.GetFileName(dialog.FileName);
                            UpdateConnectEnabled();
                        }
                        catch (IOException e)
                        {
                            ShowError(L("Could not read key file: {0}", e.Message));
                        }
                    }
                };
                DockPanel.SetDock(choose, Dock.Left);
                row.Children.Add(choose);
                row.Children.Add(name);
                stack.Children.Add(row);
                _readers[field.Key] = () => contents;
                if (field.Help is not null)
                {
                    stack.Children.Add(new TextBlock { Text = L(field.Help), TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Caption"), TextTrimming = TextTrimming.None, Margin = new Thickness(0, 4, 0, 0) });
                }

                if (field.Required)
                {
                    _requiredControls.Add((field, choose));
                }

                return stack;
            default:
                var text = new TextBox { Text = initial };
                if (field.Kind == ConnectFieldKind.Number)
                {
                    text.HorizontalAlignment = HorizontalAlignment.Left;
                    text.Width = 100;
                }

                if (field.Placeholder is not null)
                {
                    text.ToolTip = L(field.Placeholder);
                }

                text.TextChanged += (_, _) => UpdateConnectEnabled();
                _readers[field.Key] = () => text.Text;
                input = text;
                break;
        }

        System.Windows.Automation.AutomationProperties.SetName(input, L(field.Label));
        stack.Children.Add(input);
        if (field.Placeholder is not null && field.Kind is not (ConnectFieldKind.Password or ConnectFieldKind.Choice))
        {
            stack.Children.Add(new TextBlock { Text = L(field.Placeholder), Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 3, 0, 0) });
        }

        if (field.Help is not null)
        {
            stack.Children.Add(new TextBlock { Text = L(field.Help), TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Caption"), TextTrimming = TextTrimming.None, Margin = new Thickness(0, 3, 0, 0) });
        }

        if (field.Kind == ConnectFieldKind.Url && input is TextBox address)
        {
            // macOS 1.6: a password sent to a plain-HTTP address beyond the local network
            // crosses it unencrypted, and nothing said so.
            var plainHttp = new TextBlock
            {
                Text = "⚠ " + L("This address uses plain HTTP, so your password would cross the network unencrypted. Use https:// unless you trust every network in between."),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0x7A, 0x00)),
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(plainHttp, "PlainHttpWarning");
            void Update() => plainHttp.Visibility = CredentialScope.ExposesPassword(address.Text) ? Visibility.Visible : Visibility.Collapsed;
            address.TextChanged += (_, _) => Update();
            Update();
            stack.Children.Add(plainHttp);
        }

        if (field.Required)
        {
            _requiredControls.Add((field, input));
        }

        return stack;
    }

    private ConnectValues CurrentValues()
        => new(_mode?.Id ?? "default", _readers.ToDictionary(r => r.Key, r => r.Value()));

    private void UpdateConnectEnabled()
    {
        if (_connect is null || _mode is null)
        {
            return;
        }

        var values = CurrentValues();
        var missing = _mode.Fields.Any(f => f.Required && f.HasValue && f.Kind != ConnectFieldKind.Checkbox &&
                                            string.IsNullOrWhiteSpace(values.Get(f.Key)));
        _connect.IsEnabled = !missing && _connecting is null;
    }

    private async Task ConnectAsync()
    {
        // A second click (or Enter) while signing in must not start a second sign-in.
        if (_provider is null || _mode is null || _connecting is not null || _closed)
        {
            return;
        }

        ShowError(null);
        var connecting = _connecting = new CancellationTokenSource();
        SetBusy(true);
        if (_mode.OpensBrowser)
        {
            ShowStatus(L("Waiting for sign-in in browser…"));
            if (_back is not null)
            {
                _back.Content = L("Cancel Sign-In");
            }
        }

        var provider = _provider;
        try
        {
            var values = CurrentValues();
            var result = await provider.ConnectAsync(values, this, connecting.Token);

            // Closed (or cancelled) while the sign-in finished anyway: the dialog has already
            // disposed this provider and nobody is waiting for the account any more.
            if (_closed || connecting.IsCancellationRequested)
            {
                return;
            }

            _provider = null;
            if (_editingId is { } id)
            {
                if (!_services.Accounts.Update(id, provider, result))
                {
                    // Removed while its form was open.
                    provider.Dispose();
                    Close();
                    return;
                }
            }
            else
            {
                _services.Accounts.Add(provider, result);
            }

            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            ShowStatus(null);
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.Cancelled)
        {
            // The user closed the browser flow (e.g. the Google folder picker): stay on the form quietly.
            ShowStatus(null);
        }
        catch (Exception e)
        {
            ShowStatus(null);
            ShowError(e.Message);
            RenewProvider(provider);
        }
        finally
        {
            _connecting = null;
            if (!_closed)
            {
                SetBusy(false);
                if (_back is not null)
                {
                    _back.Content = _editingId is null ? L("Back") : L("Cancel");
                }
            }
        }
    }

    /// <summary>A failed sign-in can leave the provider half-configured; the next try starts fresh.</summary>
    private void RenewProvider(CloudProvider failed)
    {
        if (_closed || !ReferenceEquals(_provider, failed))
        {
            return;
        }

        failed.Dispose();
        _provider = null;
        try
        {
            _provider = _services.Accounts.CreateProvider(failed.ProviderType, _editingId ?? Guid.NewGuid());
        }
        catch (Exception e)
        {
            ShowError(e.Message);
        }
    }

    private void SetBusy(bool busy)
    {
        if (_busy is not null)
        {
            _busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_connect is not null)
        {
            _connect.IsEnabled = !busy;
        }

        if (!busy)
        {
            UpdateConnectEnabled();
        }
    }

    private void ShowError(string? message)
    {
        if (_error is null)
        {
            return;
        }

        _error.Text = message ?? string.Empty;
        _error.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
        _provider?.Dispose();
        _provider = null;
    }

    private bool _closed;

    // MARK: - IConnectPrompt

    /// <summary>Called from the provider's thread too: queued, never waited for (a wait could deadlock a closing app).</summary>
    public void ShowStatus(string? message)
    {
        void Apply()
        {
            if (_status is null)
            {
                return;
            }

            _status.Text = message ?? string.Empty;
            _status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        if (Dispatcher.CheckAccess())
        {
            Apply();
        }
        else if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(Apply);
        }
    }

    public Task<string?> RequestCodeAsync(string title, string message, CancellationToken cancellationToken)
        => Dispatcher.InvokeAsync(() => MessageDialog.Prompt(this, title, message, string.Empty, L("Continue"))).Task;

    public Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> options, CancellationToken cancellationToken)
        => Dispatcher.InvokeAsync(() => ChoiceDialog.Ask(this, title, message, options)).Task;

    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken)
        => Dispatcher.InvokeAsync(() => MessageDialog.Confirm(this, title, message, L("Continue"), icon: DialogIcon.Question)).Task;

    public async Task ShowImageAsync(byte[] png, string caption, Task until, CancellationToken cancellationToken)
    {
        Window? window = null;
        await Dispatcher.InvokeAsync(() =>
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(png);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            window = new Window
            {
                // A closed dialog can't own a window (WPF throws).
                Owner = _closed ? null : this,
                Title = Title,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new Thickness(24),
                    Children =
                    {
                        new Image { Source = image, Width = 240, Height = 240 },
                        new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap, MaxWidth = 280, Margin = new Thickness(0, 12, 0, 0), TextAlignment = TextAlignment.Center },
                    },
                },
            };
            window.Show();
        });
        try
        {
            await until.WaitAsync(cancellationToken);
        }
        finally
        {
            await Dispatcher.InvokeAsync(() => window?.Close());
        }
    }
}
