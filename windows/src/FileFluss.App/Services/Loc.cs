// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;
using FileFluss.Core;

namespace FileFluss.App.Services;

/// <summary>
/// Indexer over <see cref="Localization.L(string)"/> that tells bindings to re-read when the
/// language changes — so switching language in Settings updates every open window at once,
/// like the macOS <c>LText</c>.
/// </summary>
public sealed class LocalizedStrings : INotifyPropertyChanged
{
    public static readonly LocalizedStrings Instance = new();

    private LocalizedStrings()
    {
        Localization.LanguageChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Localization.L(key);
}

/// <summary><c>Text="{u:Loc 'Date Modified'}"</c> — a live-updating localized string.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = LocalizedStrings.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
