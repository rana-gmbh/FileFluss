// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.S3;

/// <summary>
/// Synology C2 Object Storage (port of the macOS <c>SynologyC2Provider</c>). S3-compatible:
/// the same SigV4 protocol at <c>&lt;region&gt;.s3.synologyc2.net</c> instead of AWS, so it
/// reuses <see cref="S3ApiClient"/> with the C2 host as the endpoint.
/// </summary>
[CloudProvider(CloudProviderType.SynologyC2)]
public sealed class SynologyC2Provider : S3FamilyProvider
{
    public SynologyC2Provider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal SynologyC2Provider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials, handler)
    {
        RestoreCredentials();
    }

    public override CloudProviderType ProviderType => CloudProviderType.SynologyC2;

    protected override string CredentialKeyPrefix => "synologyC2";

    public override ConnectSpec ConnectSpec => new(
        ConnectField.Info("Synology C2 Object Storage is S3-compatible. Generate an access key in the C2 console (Storage Account → Access Keys), then paste it below along with the endpoint URL the console shows for your storage."),
        new ConnectField(AccessKeyIdKey, ConnectFieldKind.Text, "Access Key ID"),
        SecretField(),
        // Text rather than Url: a bare region code ("eu-005") is accepted too.
        new ConnectField(EndpointKey, ConnectFieldKind.Text, "Endpoint URL")
        {
            Placeholder = "Endpoint URL (e.g. https://eu-005.s3.synologyc2.net)",
            Help = "Find the exact endpoint in your C2 console on the bucket detail page. Pasting the full URL is fine; FileFluss extracts the hostname and region automatically.",
        });

    /// <summary>Accepts a region code ("eu-005"), a host or a full URL. Named "Synology C2 (eu-005)", as on macOS.</summary>
    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var accessKeyId = values.GetTrimmed(AccessKeyIdKey);
        var secret = SecretOrStored(values);
        var endpointHost = EndpointForRegion(values.GetTrimmed(EndpointKey));
        var signingRegion = RegionFromEndpoint(endpointHost);
        var displayName = $"Synology C2 ({signingRegion})";

        await ConnectWithAsync(new S3Credentials(accessKeyId, secret, signingRegion, displayName, endpointHost), cancellationToken).ConfigureAwait(false);
        return new ConnectResult(signingRegion) { DisplayName = displayName };
    }

    /// <summary>The endpoint goes back into the field the user pasted it into.</summary>
    public override IReadOnlyDictionary<string, string> GetEditableConnectValues()
    {
        if (StoredCredentials is not { } stored)
        {
            return new Dictionary<string, string>();
        }

        return new Dictionary<string, string>
        {
            [AccessKeyIdKey] = stored.AccessKeyId,
            [EndpointKey] = stored.EndpointHost ?? stored.Region,
        };
    }

    /// <summary>
    /// "https://eu-005.s3.synologyc2.net/" → "eu-005.s3.synologyc2.net"; a host stays as it is;
    /// a bare region code "eu-005" → "eu-005.s3.synologyc2.net".
    /// </summary>
    public static string EndpointForRegion(string region)
    {
        var trimmed = region.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && uri.Host.Length > 0)
        {
            return uri.Host;
        }

        return trimmed.Contains('.') ? trimmed : $"{trimmed}.s3.synologyc2.net";
    }

    /// <summary>The SigV4 region scope: the first label of the host ("eu-005.s3.synologyc2.net" → "eu-005").</summary>
    public static string RegionFromEndpoint(string endpoint)
    {
        var host = endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.Host
            : endpoint;
        return host.Split('.')[0];
    }
}
