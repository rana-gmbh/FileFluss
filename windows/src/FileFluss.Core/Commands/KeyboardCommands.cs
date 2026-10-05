// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text;
using System.Text.Json.Serialization;
using FileFluss.Core.Settings;

namespace FileFluss.Core.Commands;

/// <summary>Every user-bindable command, as on macOS (<c>KeyboardCommand</c>).</summary>
public enum KeyboardCommand
{
    // Panel navigation
    ToggleActivePanel,
    ParentDirectory,
    OpenDirectory,
    HistoryBack,
    HistoryForward,
    OpenInOtherPanel,
    FocusPathBar,
    JumpToRoot,

    // File operations
    QuickLook,
    CopyToOtherPanel,
    MoveToOtherPanel,
    Rename,
    NewFolder,
    DeleteToTrash,
    Duplicate,

    // Clipboard
    CopyFiles,
    CutFiles,
    PasteFiles,
    CopyPath,

    // Selection
    SelectAll,
    DeselectAll,
    InvertSelection,
    GetInfo,

    // View / sort
    SortByName,
    SortByDate,
    SortBySize,
    SortByKind,
    ToggleHiddenFiles,

    // Cross-panel & sync
    SwapPanels,
    SyncPanels,
    CompareFolders,
    TargetToSource,
    RefreshActive,
    RefreshAll,

    // Search
    OpenSearch,
    QuickFilter,

    // Misc
    OpenSettings,
    IndexCurrentSource,
    OpenInTerminal,
}

public enum KeyboardSection
{
    PanelNavigation,
    FileOperations,
    Clipboard,
    Selection,
    ViewSort,
    CrossPanelTools,
    Search,
    Misc,
}

/// <summary>"Explorer Style" replaces the macOS "Finder Style"; "Commander Style" follows Total Commander.</summary>
public enum KeyboardPreset
{
    Explorer,
    Commander,
}

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
}

/// <summary>
/// A key plus modifiers. Keys are canonical tokens — a lower-case letter or digit, or one of
/// "enter", "tab", "space", "backspace", "delete", "up", "down", "left", "right", "escape",
/// "home", "end", "pageup", "pagedown", "insert", "plus", "minus", "equals", "slash",
/// "backslash", "period", "comma", "multiply", "f1"…"f12". An empty key means "cleared".
/// </summary>
public sealed record ShortcutBinding(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("modifiers")] ShortcutModifiers Modifiers)
{
    public static readonly ShortcutBinding Cleared = new(string.Empty, ShortcutModifiers.None);

    [JsonIgnore]
    public bool IsCleared => Key.Length == 0;

    /// <summary>"Ctrl+Shift+N", the way Windows menus write shortcuts.</summary>
    public string DisplayString
    {
        get
        {
            if (IsCleared)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            if (Modifiers.HasFlag(ShortcutModifiers.Control))
            {
                builder.Append("Ctrl+");
            }

            if (Modifiers.HasFlag(ShortcutModifiers.Alt))
            {
                builder.Append("Alt+");
            }

            if (Modifiers.HasFlag(ShortcutModifiers.Shift))
            {
                builder.Append("Shift+");
            }

            builder.Append(KeyName(Key));
            return builder.ToString();
        }
    }

    public static string KeyName(string key) => key switch
    {
        "enter" => "Enter",
        "tab" => "Tab",
        "space" => Localization.L("Space"),
        "backspace" => "Backspace",
        "delete" => "Del",
        "up" => "↑",
        "down" => "↓",
        "left" => "←",
        "right" => "→",
        "escape" => "Esc",
        "home" => "Home",
        "end" => "End",
        "pageup" => "PgUp",
        "pagedown" => "PgDn",
        "insert" => "Ins",
        "plus" => "+",
        "minus" => "-",
        "equals" => "=",
        "slash" => "/",
        "backslash" => "\\",
        "period" => ".",
        "comma" => ",",
        "multiply" => "Num *",
        _ when key.Length > 1 && key[0] == 'f' && int.TryParse(key[1..], out _) => key.ToUpperInvariant(),
        _ => key.ToUpperInvariant(),
    };

    public static ShortcutBinding Ctrl(string key) => new(key, ShortcutModifiers.Control);

    public static ShortcutBinding CtrlShift(string key) => new(key, ShortcutModifiers.Control | ShortcutModifiers.Shift);

    public static ShortcutBinding CtrlAlt(string key) => new(key, ShortcutModifiers.Control | ShortcutModifiers.Alt);

    public static ShortcutBinding Alt(string key) => new(key, ShortcutModifiers.Alt);

    public static ShortcutBinding Shift(string key) => new(key, ShortcutModifiers.Shift);

    public static ShortcutBinding Plain(string key) => new(key, ShortcutModifiers.None);
}

public static class KeyboardCommandInfo
{
    /// <summary>The macOS display names — localization keys.</summary>
    public static string DisplayName(this KeyboardCommand command) => command switch
    {
        KeyboardCommand.ToggleActivePanel => "Toggle Active Panel",
        KeyboardCommand.ParentDirectory => "Parent Directory",
        KeyboardCommand.OpenDirectory => "Open Folder",
        KeyboardCommand.HistoryBack => "Back",
        KeyboardCommand.HistoryForward => "Forward",
        KeyboardCommand.OpenInOtherPanel => "Open in Other Panel",
        KeyboardCommand.FocusPathBar => "Go to Folder…",
        KeyboardCommand.JumpToRoot => "Jump to Root",
        KeyboardCommand.QuickLook => "Quick Look",
        KeyboardCommand.CopyToOtherPanel => "Copy to Other Panel",
        KeyboardCommand.MoveToOtherPanel => "Move to Other Panel",
        KeyboardCommand.Rename => "Rename",
        KeyboardCommand.NewFolder => "New Folder",
        KeyboardCommand.DeleteToTrash => "Move to Recycle Bin",
        KeyboardCommand.Duplicate => "Duplicate",
        KeyboardCommand.CopyFiles => "Copy",
        KeyboardCommand.CutFiles => "Cut",
        KeyboardCommand.PasteFiles => "Paste",
        KeyboardCommand.CopyPath => "Copy Path",
        KeyboardCommand.SelectAll => "Select All",
        KeyboardCommand.DeselectAll => "Deselect All",
        KeyboardCommand.InvertSelection => "Invert Selection",
        KeyboardCommand.GetInfo => "Properties",
        KeyboardCommand.SortByName => "Sort by Name",
        KeyboardCommand.SortByDate => "Sort by Date",
        KeyboardCommand.SortBySize => "Sort by Size",
        KeyboardCommand.SortByKind => "Sort by Kind",
        KeyboardCommand.ToggleHiddenFiles => "Show Hidden Files",
        KeyboardCommand.SwapPanels => "Swap Panels",
        KeyboardCommand.SyncPanels => "Sync Left and Right Panels",
        KeyboardCommand.CompareFolders => "Compare Folders",
        KeyboardCommand.TargetToSource => "Target → Source",
        KeyboardCommand.RefreshActive => "Refresh Active Panel",
        KeyboardCommand.RefreshAll => "Refresh Both Panels",
        KeyboardCommand.OpenSearch => "Search",
        KeyboardCommand.QuickFilter => "Quick Filter",
        KeyboardCommand.OpenSettings => "Settings",
        KeyboardCommand.IndexCurrentSource => "Index Current Source",
        KeyboardCommand.OpenInTerminal => "Open in Terminal",
        _ => command.ToString(),
    };

    public static KeyboardSection Section(this KeyboardCommand command) => command switch
    {
        KeyboardCommand.ToggleActivePanel or KeyboardCommand.ParentDirectory or KeyboardCommand.OpenDirectory or
            KeyboardCommand.HistoryBack or KeyboardCommand.HistoryForward or KeyboardCommand.OpenInOtherPanel or
            KeyboardCommand.TargetToSource or KeyboardCommand.FocusPathBar or KeyboardCommand.JumpToRoot => KeyboardSection.PanelNavigation,
        KeyboardCommand.QuickLook or KeyboardCommand.CopyToOtherPanel or KeyboardCommand.MoveToOtherPanel or
            KeyboardCommand.Rename or KeyboardCommand.NewFolder or KeyboardCommand.DeleteToTrash or KeyboardCommand.Duplicate => KeyboardSection.FileOperations,
        KeyboardCommand.CopyFiles or KeyboardCommand.CutFiles or KeyboardCommand.PasteFiles or KeyboardCommand.CopyPath => KeyboardSection.Clipboard,
        KeyboardCommand.SelectAll or KeyboardCommand.DeselectAll or KeyboardCommand.InvertSelection or KeyboardCommand.GetInfo => KeyboardSection.Selection,
        KeyboardCommand.SortByName or KeyboardCommand.SortByDate or KeyboardCommand.SortBySize or KeyboardCommand.SortByKind or
            KeyboardCommand.ToggleHiddenFiles => KeyboardSection.ViewSort,
        KeyboardCommand.SwapPanels or KeyboardCommand.SyncPanels or KeyboardCommand.CompareFolders or
            KeyboardCommand.RefreshActive or KeyboardCommand.RefreshAll => KeyboardSection.CrossPanelTools,
        KeyboardCommand.OpenSearch or KeyboardCommand.QuickFilter => KeyboardSection.Search,
        _ => KeyboardSection.Misc,
    };

    public static string DisplayName(this KeyboardSection section) => section switch
    {
        KeyboardSection.PanelNavigation => "Panel Navigation",
        KeyboardSection.FileOperations => "File Operations",
        KeyboardSection.Clipboard => "Clipboard",
        KeyboardSection.Selection => "Selection",
        KeyboardSection.ViewSort => "View & Sort",
        KeyboardSection.CrossPanelTools => "Cross-panel & Tools",
        KeyboardSection.Search => "Search",
        _ => "Misc",
    };

    public static string DisplayName(this KeyboardPreset preset) => preset switch
    {
        KeyboardPreset.Commander => "Commander Style",
        _ => "Windows / Explorer Style",
    };

    /// <summary>
    /// Windows conventions first (Explorer's F2 rename, Alt+↑ parent, Alt+Enter properties,
    /// Ctrl+Shift+C copy as path), with the dual-panel F5/F6 kept because they are what a
    /// two-pane file manager is for.
    /// </summary>
    public static readonly IReadOnlyDictionary<KeyboardCommand, ShortcutBinding> ExplorerDefaults = new Dictionary<KeyboardCommand, ShortcutBinding>
    {
        [KeyboardCommand.ToggleActivePanel] = ShortcutBinding.Plain("tab"),
        [KeyboardCommand.ParentDirectory] = ShortcutBinding.Alt("up"),
        [KeyboardCommand.OpenDirectory] = ShortcutBinding.Plain("enter"),
        [KeyboardCommand.HistoryBack] = ShortcutBinding.Alt("left"),
        [KeyboardCommand.HistoryForward] = ShortcutBinding.Alt("right"),
        [KeyboardCommand.OpenInOtherPanel] = ShortcutBinding.CtrlAlt("right"),
        [KeyboardCommand.FocusPathBar] = ShortcutBinding.Ctrl("l"),
        [KeyboardCommand.JumpToRoot] = ShortcutBinding.Ctrl("backslash"),
        [KeyboardCommand.QuickLook] = ShortcutBinding.Plain("space"),
        [KeyboardCommand.CopyToOtherPanel] = ShortcutBinding.Plain("f5"),
        [KeyboardCommand.MoveToOtherPanel] = ShortcutBinding.Plain("f6"),
        [KeyboardCommand.Rename] = ShortcutBinding.Plain("f2"),
        [KeyboardCommand.NewFolder] = ShortcutBinding.CtrlShift("n"),
        [KeyboardCommand.DeleteToTrash] = ShortcutBinding.Plain("delete"),
        [KeyboardCommand.Duplicate] = ShortcutBinding.CtrlShift("d"),
        [KeyboardCommand.CopyFiles] = ShortcutBinding.Ctrl("c"),
        [KeyboardCommand.CutFiles] = ShortcutBinding.Ctrl("x"),
        [KeyboardCommand.PasteFiles] = ShortcutBinding.Ctrl("v"),
        [KeyboardCommand.CopyPath] = ShortcutBinding.CtrlShift("c"),
        [KeyboardCommand.SelectAll] = ShortcutBinding.Ctrl("a"),
        [KeyboardCommand.DeselectAll] = ShortcutBinding.CtrlShift("a"),
        [KeyboardCommand.InvertSelection] = ShortcutBinding.CtrlShift("i"),
        [KeyboardCommand.GetInfo] = ShortcutBinding.Alt("enter"),
        [KeyboardCommand.SortByName] = ShortcutBinding.Ctrl("1"),
        [KeyboardCommand.SortByDate] = ShortcutBinding.Ctrl("2"),
        [KeyboardCommand.SortBySize] = ShortcutBinding.Ctrl("3"),
        [KeyboardCommand.SortByKind] = ShortcutBinding.Ctrl("4"),
        [KeyboardCommand.ToggleHiddenFiles] = ShortcutBinding.Ctrl("h"),
        [KeyboardCommand.SwapPanels] = ShortcutBinding.Ctrl("u"),
        [KeyboardCommand.SyncPanels] = ShortcutBinding.CtrlShift("s"),
        [KeyboardCommand.CompareFolders] = ShortcutBinding.CtrlAlt("c"),
        [KeyboardCommand.TargetToSource] = ShortcutBinding.Ctrl("equals"),
        [KeyboardCommand.RefreshActive] = ShortcutBinding.Ctrl("r"),
        [KeyboardCommand.RefreshAll] = ShortcutBinding.CtrlShift("r"),
        [KeyboardCommand.OpenSearch] = ShortcutBinding.Ctrl("f"),
        [KeyboardCommand.QuickFilter] = ShortcutBinding.CtrlShift("f"),
        [KeyboardCommand.OpenSettings] = ShortcutBinding.Ctrl("comma"),
        [KeyboardCommand.IndexCurrentSource] = ShortcutBinding.CtrlAlt("i"),
        [KeyboardCommand.OpenInTerminal] = ShortcutBinding.CtrlAlt("t"),
    };

    /// <summary>Total Commander's keys where Windows doesn't already own them.</summary>
    public static readonly IReadOnlyDictionary<KeyboardCommand, ShortcutBinding> CommanderDefaults = new Dictionary<KeyboardCommand, ShortcutBinding>
    {
        [KeyboardCommand.ToggleActivePanel] = ShortcutBinding.Plain("tab"),
        [KeyboardCommand.ParentDirectory] = ShortcutBinding.Plain("backspace"),
        [KeyboardCommand.OpenDirectory] = ShortcutBinding.Plain("enter"),
        [KeyboardCommand.HistoryBack] = ShortcutBinding.Alt("left"),
        [KeyboardCommand.HistoryForward] = ShortcutBinding.Alt("right"),
        [KeyboardCommand.OpenInOtherPanel] = ShortcutBinding.Ctrl("right"),
        [KeyboardCommand.FocusPathBar] = ShortcutBinding.Ctrl("l"),
        [KeyboardCommand.JumpToRoot] = ShortcutBinding.Ctrl("backslash"),
        [KeyboardCommand.QuickLook] = ShortcutBinding.Plain("f3"),
        [KeyboardCommand.CopyToOtherPanel] = ShortcutBinding.Plain("f5"),
        [KeyboardCommand.MoveToOtherPanel] = ShortcutBinding.Plain("f6"),
        [KeyboardCommand.Rename] = ShortcutBinding.Shift("f6"),
        [KeyboardCommand.NewFolder] = ShortcutBinding.Plain("f7"),
        [KeyboardCommand.DeleteToTrash] = ShortcutBinding.Plain("f8"),
        [KeyboardCommand.Duplicate] = ShortcutBinding.Shift("f5"),
        [KeyboardCommand.CopyFiles] = ShortcutBinding.Ctrl("c"),
        [KeyboardCommand.CutFiles] = ShortcutBinding.Ctrl("x"),
        [KeyboardCommand.PasteFiles] = ShortcutBinding.Ctrl("v"),
        [KeyboardCommand.CopyPath] = ShortcutBinding.Ctrl("p"),
        [KeyboardCommand.SelectAll] = ShortcutBinding.Ctrl("a"),
        [KeyboardCommand.DeselectAll] = ShortcutBinding.CtrlShift("a"),
        [KeyboardCommand.InvertSelection] = ShortcutBinding.Plain("multiply"),
        [KeyboardCommand.GetInfo] = ShortcutBinding.Alt("enter"),
        [KeyboardCommand.SortByName] = ShortcutBinding.Ctrl("f3"),
        [KeyboardCommand.SortByDate] = ShortcutBinding.Ctrl("f5"),
        [KeyboardCommand.SortBySize] = ShortcutBinding.Ctrl("f6"),
        [KeyboardCommand.SortByKind] = ShortcutBinding.Ctrl("f4"),
        [KeyboardCommand.ToggleHiddenFiles] = ShortcutBinding.Ctrl("h"),
        [KeyboardCommand.SwapPanels] = ShortcutBinding.Ctrl("u"),
        [KeyboardCommand.SyncPanels] = ShortcutBinding.CtrlShift("s"),
        [KeyboardCommand.CompareFolders] = ShortcutBinding.Shift("f2"),
        [KeyboardCommand.TargetToSource] = ShortcutBinding.Ctrl("i"),
        [KeyboardCommand.RefreshActive] = ShortcutBinding.Ctrl("r"),
        [KeyboardCommand.RefreshAll] = ShortcutBinding.Plain("f2"),
        [KeyboardCommand.OpenSearch] = ShortcutBinding.Alt("f7"),
        [KeyboardCommand.QuickFilter] = ShortcutBinding.Ctrl("s"),
        [KeyboardCommand.OpenSettings] = ShortcutBinding.Ctrl("comma"),
        [KeyboardCommand.IndexCurrentSource] = ShortcutBinding.CtrlAlt("i"),
        [KeyboardCommand.OpenInTerminal] = ShortcutBinding.CtrlAlt("t"),
    };

    public static IReadOnlyDictionary<KeyboardCommand, ShortcutBinding> Defaults(KeyboardPreset preset)
        => preset == KeyboardPreset.Commander ? CommanderDefaults : ExplorerDefaults;
}

/// <summary>Effective bindings: preset defaults overridden by the user's customisations.</summary>
public sealed class KeyboardMap
{
    private readonly AppSettings _settings;

    public KeyboardMap(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>The binding in effect, or null when the user cleared it.</summary>
    public ShortcutBinding? BindingFor(KeyboardCommand command)
    {
        if (_settings.CustomBindings.TryGetValue(command.ToString(), out var custom))
        {
            return custom.IsCleared ? null : custom;
        }

        return KeyboardCommandInfo.Defaults(_settings.KeyboardPreset).GetValueOrDefault(command);
    }

    public bool IsCustomised(KeyboardCommand command) => _settings.CustomBindings.ContainsKey(command.ToString());

    public void SetBinding(KeyboardCommand command, ShortcutBinding? binding)
    {
        var copy = new Dictionary<string, ShortcutBinding>(_settings.CustomBindings)
        {
            [command.ToString()] = binding ?? ShortcutBinding.Cleared,
        };
        _settings.CustomBindings = copy;
    }

    public void ResetToPresetDefault(KeyboardCommand command)
    {
        var copy = new Dictionary<string, ShortcutBinding>(_settings.CustomBindings);
        copy.Remove(command.ToString());
        _settings.CustomBindings = copy;
    }

    public void ResetAll() => _settings.CustomBindings = [];

    /// <summary>Other commands already using <paramref name="candidate"/>.</summary>
    public IReadOnlyList<KeyboardCommand> Conflicts(ShortcutBinding candidate, KeyboardCommand ignoring)
        => Enum.GetValues<KeyboardCommand>().Where(c => c != ignoring && BindingFor(c) == candidate).ToList();

    /// <summary>The command bound to <paramref name="pressed"/>, if any.</summary>
    public KeyboardCommand? CommandFor(ShortcutBinding pressed)
    {
        foreach (var command in Enum.GetValues<KeyboardCommand>())
        {
            if (BindingFor(command) == pressed)
            {
                return command;
            }
        }

        return null;
    }
}
