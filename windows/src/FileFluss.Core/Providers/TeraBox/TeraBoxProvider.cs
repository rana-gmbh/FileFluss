// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.TeraBox;

/// <summary>
/// TeraBox Open Platform — port of the macOS <c>TeraBoxProvider</c>. Sign-in is Device Code
/// mode: Connect fetches a QR code, the user scans it in the TeraBox app, and the provider
/// polls for the token. The Open Platform sandboxes third-party apps to their own folder
/// ("/From: Other Applications/…"), so only files created through this integration are
/// visible — not the user's whole drive.
/// </summary>
[CloudProvider(CloudProviderType.TeraBox)]
public sealed class TeraBoxProvider : CloudProvider
{
    private readonly HttpClient _http;
    private readonly TeraBoxAppCredentials _app;
    private volatile TeraBoxApiClient? _client;

    /// <summary>
    /// Whether this build carries the Open Platform keys sign-in needs. Without them every
    /// login fails with "not yet implemented", so Add Account doesn't offer TeraBox; an
    /// account added earlier keeps working as far as it can.
    /// </summary>
    public static bool IsAvailable => TeraBoxAppCredentials.Default.IsConfigured;

    public TeraBoxProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null, null)
    {
    }

    internal TeraBoxProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler, TeraBoxAppCredentials? app)
        : base(accountId, credentials)
    {
        _http = handler is null
            ? CloudHttp.Create()
            : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        _app = app ?? TeraBoxAppCredentials.Default;

        if (credentials.Load<TeraBoxCredentials>(CredentialKey) is { } stored)
        {
            _client = CreateClient(stored);
        }
    }

    /// <summary>Waits between token polls; tests make it instant.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    public override CloudProviderType ProviderType => CloudProviderType.TeraBox;

    protected override string CredentialKeyPrefix => "terabox";

    public override bool IsAuthenticated => _client is not null;

    // MARK: - Connecting

    public override ConnectSpec ConnectSpec { get; } = new(
        ConnectField.Info("TeraBox uses device-code sign-in: tap Connect to get a QR code, then scan it with the TeraBox app to authorize. Access is limited to this app's folder on your TeraBox drive."));

    /// <summary>
    /// Device-code sign-in: shows the QR through <see cref="IConnectPrompt.ShowImageAsync"/>
    /// while polling, and stores the tokens once the user has authorized. Without provisioned
    /// application credentials this fails with "not yet implemented", as on macOS.
    /// </summary>
    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var device = await TeraBoxApiClient.RequestDeviceCodeAsync(_http, _app, cancellationToken).ConfigureAwait(false);

        using var pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var poll = TeraBoxApiClient.PollForTokenAsync(_http, _app, device, Delay, pollCancellation.Token);
        var caption = Localization.L("Open the TeraBox app and scan this QR code to authorize.");
        Task shown;
        if (device.QrPng is { } png)
        {
            shown = prompt.ShowImageAsync(png, caption, poll, cancellationToken);
        }
        else
        {
            prompt.ShowStatus(caption);
            shown = poll;
        }

        await Task.WhenAny(poll, shown).ConfigureAwait(false);
        if (!poll.IsCompleted)
        {
            // The QR was dismissed before the user authorized: stop polling.
            await pollCancellation.CancelAsync().ConfigureAwait(false);
            await Observe(poll).ConfigureAwait(false);
            await shown.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw CloudProviderException.Cancelled();
        }

        TeraBoxCredentials credentials;
        try
        {
            credentials = await poll.ConfigureAwait(false);
        }
        finally
        {
            await Observe(shown).ConfigureAwait(false);
            if (device.QrPng is null)
            {
                prompt.ShowStatus(null);
            }
        }

        Credentials.Save(CredentialKey, credentials);
        _client = CreateClient(credentials);
        SupportLogger.Shared.Log($"[TeraBox] Authenticated user {credentials.UserId}", "TeraBox");
        return new ConnectResult(credentials.UserId.ToString(CultureInfo.InvariantCulture));
    }

    public override Task DisconnectAsync()
    {
        _client = null;
        return base.DisconnectAsync();
    }

    private TeraBoxApiClient Client => _client ?? throw CloudProviderException.NotAuthenticated();

    private TeraBoxApiClient CreateClient(TeraBoxCredentials credentials)
        => new(_http, _app, credentials, refreshed => Credentials.Save(CredentialKey, refreshed));

    private static async Task Observe(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or CloudProviderException)
        {
        }
    }

    // MARK: - File operations

    public override Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Client.ListFolderAsync(CloudPath.Normalize(path), cancellationToken);

    public override Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => Client.DownloadAsync(CloudPath.Normalize(remotePath), localPath, onBytes, cancellationToken);

    public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => Client.UploadAsync(localPath, CloudPath.Normalize(remotePath), onBytes, cancellationToken);

    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
        => Client.DeleteAsync(CloudPath.Normalize(path), cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Client.CreateFolderAsync(CloudPath.Normalize(path), cancellationToken);

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => Client.RenameAsync(CloudPath.Normalize(path), newName, cancellationToken);

    public override Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => Client.MoveAsync(CloudPath.Normalize(path), CloudPath.Normalize(newPath), cancellationToken);

    /// <summary>TeraBox has no metadata-by-path call that returns listing fields; the parent is listed instead.</summary>
    public override async Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        var client = Client;
        var normalized = CloudPath.Normalize(path);
        if (normalized == "/")
        {
            return new CloudFileItem("/", string.Empty, "/", true, 0, CloudFileItem.UnknownDate);
        }

        var name = CloudPath.Name(normalized);
        var entries = await client.ListFolderAsync(CloudPath.Parent(normalized), cancellationToken).ConfigureAwait(false);
        return entries.FirstOrDefault(e => e.Name == name) ?? throw CloudProviderException.NotFound(path);
    }

    public override Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken = default)
        => _client is { } client ? client.StorageQuotaAsync(cancellationToken) : Task.FromResult<CloudStorageQuota?>(null);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _http.Dispose();
        }

        base.Dispose(disposing);
    }
}
