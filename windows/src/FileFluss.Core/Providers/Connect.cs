// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Providers;

/// <summary>What kind of input a connect-form field is.</summary>
public enum ConnectFieldKind
{
    Text,
    Email,
    Url,
    Password,

    /// <summary>A whole number (a port). Rendered narrow.</summary>
    Number,
    Checkbox,

    /// <summary>A drop-down of <see cref="ConnectField.Choices"/>; may allow free text (<see cref="ConnectField.AllowsCustomValue"/>).</summary>
    Choice,

    /// <summary>A one-time / 2FA code. Usually optional.</summary>
    OneTimeCode,

    /// <summary>
    /// "Choose file…": the value is the chosen file's text contents (an SSH private key).
    /// Read once and stored with the credentials, so moving the file later is harmless.
    /// </summary>
    FileContents,

    /// <summary>Static explanatory text; <see cref="ConnectField.Label"/> is the text. Has no value.</summary>
    Info,

    /// <summary>A hyperlink; <see cref="ConnectField.Label"/> is the caption, <see cref="ConnectField.Url"/> the target. Has no value.</summary>
    Link,

    /// <summary>Like <see cref="Info"/>, but drawn as a warning.</summary>
    Warning,
}

public sealed record ConnectChoice(string Value, string Label);

/// <summary>
/// One row of the Add/Edit Account form. <see cref="Label"/>, <see cref="Placeholder"/> and
/// <see cref="Help"/> are localization keys — the macOS English text — and the UI runs them
/// through <see cref="Localization.L(string)"/>.
/// </summary>
public sealed record ConnectField(string Key, ConnectFieldKind Kind, string Label)
{
    public string? Placeholder { get; init; }

    public string? DefaultValue { get; init; }

    /// <summary>Connect stays disabled until every required field has a value.</summary>
    public bool Required { get; init; } = true;

    /// <summary>Small caption under the field.</summary>
    public string? Help { get; init; }

    public IReadOnlyList<ConnectChoice> Choices { get; init; } = [];

    public bool AllowsCustomValue { get; init; }

    /// <summary>Target of a <see cref="ConnectFieldKind.Link"/>.</summary>
    public string? Url { get; init; }

    /// <summary>Open-file dialog filter for <see cref="ConnectFieldKind.FileContents"/>, e.g. "Key files|*.pem;*.key;id_*|All files|*.*".</summary>
    public string? FileFilter { get; init; }

    public static ConnectField Info(string text) => new(string.Empty, ConnectFieldKind.Info, text) { Required = false };

    public static ConnectField Warning(string text) => new(string.Empty, ConnectFieldKind.Warning, text) { Required = false };

    public static ConnectField Link(string caption, string url) => new(string.Empty, ConnectFieldKind.Link, caption) { Url = url, Required = false };

    public bool HasValue => Kind is not (ConnectFieldKind.Info or ConnectFieldKind.Link or ConnectFieldKind.Warning);
}

/// <summary>
/// One way of signing in. Most providers have a single mode; NextCloud offers "Browser" and
/// "App Password", pCloud "Email & Password" and "Access Token", SFTP "Password" and
/// "Private Key". The form shows a segmented switch when there is more than one.
/// </summary>
public sealed record ConnectMode(string Id, string Title, IReadOnlyList<ConnectField> Fields)
{
    /// <summary>Connect hands off to the system browser (OAuth); the form says "Waiting for sign-in in browser…".</summary>
    public bool OpensBrowser { get; init; }
}

/// <summary>The Add/Edit Account form for one provider.</summary>
public sealed record ConnectSpec(IReadOnlyList<ConnectMode> Modes)
{
    public ConnectSpec(params ConnectField[] fields)
        : this([new ConnectMode("default", string.Empty, fields)])
    {
    }

    /// <summary>A browser-only (OAuth) provider: one explanatory line and a Connect button.</summary>
    public static ConnectSpec Browser(params ConnectField[] fields)
        => new([new ConnectMode("browser", string.Empty, fields) { OpensBrowser = true }]);

    /// <summary>
    /// True when the account can be signed in again from the panel's "sign in again" banner
    /// without the Settings sheet — every mode is browser-only with nothing to type.
    /// </summary>
    public bool SupportsInlineReauthentication
        => Modes.Count == 1 && Modes[0].OpensBrowser && Modes[0].Fields.All(f => !f.HasValue);
}

/// <summary>What the user entered, keyed by <see cref="ConnectField.Key"/>.</summary>
public sealed class ConnectValues
{
    private readonly Dictionary<string, string> _values;

    public ConnectValues(string mode, IReadOnlyDictionary<string, string> values)
    {
        Mode = mode;
        _values = new Dictionary<string, string>(values, StringComparer.Ordinal);
    }

    public static ConnectValues Empty(string mode = "default") => new(mode, new Dictionary<string, string>());

    /// <summary>The <see cref="ConnectMode.Id"/> the user chose.</summary>
    public string Mode { get; }

    /// <summary>Trimmed value, or empty. Passwords are returned untrimmed.</summary>
    public string this[string key] => Get(key);

    public string Get(string key, string fallback = "")
        => _values.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : fallback;

    public string GetTrimmed(string key, string fallback = "") => Get(key, fallback).Trim();

    public string? GetOptional(string key)
    {
        var value = GetTrimmed(key);
        return value.Length == 0 ? null : value;
    }

    public bool GetBool(string key, bool fallback = false)
        => _values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;

    public int GetInt(string key, int fallback)
        => int.TryParse(GetTrimmed(key), out var parsed) ? parsed : fallback;

    public IReadOnlyDictionary<string, string> AsDictionary() => _values;
}

/// <summary>
/// What a successful sign-in produced. The app names the account
/// "<c>{Provider} ({AccountLabel})</c>" unless <see cref="DisplayName"/> is set.
/// </summary>
public sealed record ConnectResult(string? AccountLabel)
{
    /// <summary>Overrides the composed name (e.g. an S3-compatible account the user named "Backblaze").</summary>
    public string? DisplayName { get; init; }

    /// <summary>The folder panels open at (SFTP remote path, S3 bucket/prefix). Null keeps "/".</summary>
    public string? RootPath { get; init; }
}

/// <summary>
/// Lets a provider talk to the user in the middle of signing in, without knowing anything
/// about windows: ask for a 2FA code, pick one of several drives, show a status line or a QR
/// code to scan. Implemented by the Add Account dialog; tests use a scripted fake.
/// </summary>
public interface IConnectPrompt
{
    /// <summary>A status line under the form, e.g. "Waiting for sign-in in browser…". Null clears it.</summary>
    void ShowStatus(string? message);

    /// <summary>Asks for a one-time code. Null when the user cancels.</summary>
    Task<string?> RequestCodeAsync(string title, string message, CancellationToken cancellationToken);

    /// <summary>Asks the user to pick one option. The chosen index, or null when cancelled.</summary>
    Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> options, CancellationToken cancellationToken);

    /// <summary>A yes/no question (e.g. "Trust this certificate?").</summary>
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken);

    /// <summary>Shows a PNG (a login QR code) with a caption until <paramref name="until"/> completes.</summary>
    Task ShowImageAsync(byte[] png, string caption, Task until, CancellationToken cancellationToken);
}

/// <summary>A prompt that answers nothing — for reconnecting stored accounts, where nobody is there to ask.</summary>
public sealed class SilentConnectPrompt : IConnectPrompt
{
    public static readonly SilentConnectPrompt Instance = new();

    public void ShowStatus(string? message)
    {
    }

    public Task<string?> RequestCodeAsync(string title, string message, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> options, CancellationToken cancellationToken)
        => Task.FromResult<int?>(options.Count > 0 ? 0 : null);

    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task ShowImageAsync(byte[] png, string caption, Task until, CancellationToken cancellationToken) => until;
}
