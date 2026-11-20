// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.S3;

/// <summary>Amazon S3 with an IAM access key (port of the macOS <c>S3Provider</c>).</summary>
[CloudProvider(CloudProviderType.S3)]
public sealed class S3Provider : S3FamilyProvider
{
    public S3Provider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal S3Provider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials, handler)
    {
        RestoreCredentials();
    }

    public override CloudProviderType ProviderType => CloudProviderType.S3;

    protected override string CredentialKeyPrefix => "s3";

    public override ConnectSpec ConnectSpec => new(
        ConnectField.Info("Enter an AWS access key with S3 permissions and the region your buckets live in. You can create a key under IAM → Users → Security credentials → Access keys."),
        new ConnectField(AccessKeyIdKey, ConnectFieldKind.Text, "Access Key ID") { Placeholder = "Access Key ID (e.g. AKIA…)" },
        SecretField(),
        new ConnectField(RegionKey, ConnectFieldKind.Choice, "Region")
        {
            Choices = S3RegionList.Choices,
            AllowsCustomValue = true,
            DefaultValue = "us-east-1",
            Placeholder = "Or type a custom region code",
            Help = "Tip: paste the exact region code (e.g. eu-central-1) from the AWS console URL if you don't see your region listed.",
        },
        PathField());

    /// <summary>
    /// With a path, the connection check probes that bucket instead of the account's bucket
    /// list, which a key scoped to one bucket is not allowed to read (issue #53). Named
    /// "AWS S3 (***6UDK)", plus " — bucket/folder" when scoped, as on macOS.
    /// </summary>
    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var accessKeyId = values.GetTrimmed(AccessKeyIdKey);
        var secret = SecretOrStored(values);
        var region = values.GetTrimmed(RegionKey, "us-east-1");
        var rootPath = NormalizeRootPath(values.Get(PathKey));
        var masked = MaskedAccessKey(accessKeyId);
        var displayName = $"AWS S3 ({masked})";

        await ConnectWithAsync(new S3Credentials(accessKeyId, secret, region, displayName, EndpointHost: null, RootPath: rootPath), cancellationToken).ConfigureAwait(false);
        return new ConnectResult(masked) { DisplayName = WithRoot(displayName, rootPath), RootPath = rootPath ?? "/" };
    }

    public override IReadOnlyDictionary<string, string> GetEditableConnectValues()
    {
        if (StoredCredentials is not { } stored)
        {
            return new Dictionary<string, string>();
        }

        return new Dictionary<string, string>
        {
            [AccessKeyIdKey] = stored.AccessKeyId,
            [RegionKey] = stored.Region,
            [PathKey] = EditablePath(),
        };
    }

    /// <summary>"***6UDK": enough to tell two keys apart, nothing more.</summary>
    internal static string MaskedAccessKey(string accessKeyId)
        => "***" + (accessKeyId.Length <= 4 ? accessKeyId : accessKeyId[^4..]);
}
