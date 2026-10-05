// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.S3;

/// <summary>
/// Generic S3-compatible storage — Hetzner Object Storage, MinIO, Wasabi, Backblaze B2 (S3
/// API), Cloudflare R2, Linode, DigitalOcean Spaces… (port of the macOS
/// <c>S3CompatibleProvider</c>). The user supplies the endpoint host and, optionally, the SigV4
/// region scope.
/// </summary>
[CloudProvider(CloudProviderType.S3Compatible)]
public sealed class S3CompatibleProvider : S3FamilyProvider
{
    /// <summary>
    /// Well-known endpoints offered in the Endpoint drop-down; picking one fills the field, and
    /// any other URL can still be typed. Only services whose endpoint doesn't depend on the
    /// account are listed (Cloudflare R2's carries the account id; MinIO is self-hosted).
    /// Backblaze assigns each account a cluster, which its bucket page shows.
    /// </summary>
    internal static readonly IReadOnlyList<ConnectChoice> EndpointPresets =
    [
        new("https://fsn1.your-objectstorage.com", "Hetzner Object Storage — Falkenstein (fsn1)"),
        new("https://nbg1.your-objectstorage.com", "Hetzner Object Storage — Nuremberg (nbg1)"),
        new("https://hel1.your-objectstorage.com", "Hetzner Object Storage — Helsinki (hel1)"),
        new("https://s3.us-east-1.wasabisys.com", "Wasabi — US East 1 (N. Virginia)"),
        new("https://s3.us-west-1.wasabisys.com", "Wasabi — US West 1 (Oregon)"),
        new("https://s3.eu-central-1.wasabisys.com", "Wasabi — EU Central 1 (Amsterdam)"),
        new("https://s3.eu-central-2.wasabisys.com", "Wasabi — EU Central 2 (Frankfurt)"),
        new("https://s3.eu-west-1.wasabisys.com", "Wasabi — EU West 1 (London)"),
        new("https://s3.ap-northeast-1.wasabisys.com", "Wasabi — AP Northeast 1 (Tokyo)"),
        new("https://s3.us-west-004.backblazeb2.com", "Backblaze B2 — us-west-004"),
        new("https://s3.us-east-005.backblazeb2.com", "Backblaze B2 — us-east-005"),
        new("https://s3.eu-central-003.backblazeb2.com", "Backblaze B2 — eu-central-003"),
        new("https://nyc3.digitaloceanspaces.com", "DigitalOcean Spaces — New York (nyc3)"),
        new("https://sfo3.digitaloceanspaces.com", "DigitalOcean Spaces — San Francisco (sfo3)"),
        new("https://ams3.digitaloceanspaces.com", "DigitalOcean Spaces — Amsterdam (ams3)"),
        new("https://fra1.digitaloceanspaces.com", "DigitalOcean Spaces — Frankfurt (fra1)"),
        new("https://sgp1.digitaloceanspaces.com", "DigitalOcean Spaces — Singapore (sgp1)"),
        new("https://syd1.digitaloceanspaces.com", "DigitalOcean Spaces — Sydney (syd1)"),
    ];

    public S3CompatibleProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal S3CompatibleProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials, handler)
    {
        RestoreCredentials();
    }

    public override CloudProviderType ProviderType => CloudProviderType.S3Compatible;

    protected override string CredentialKeyPrefix => "s3Compatible";

    public override ConnectSpec ConnectSpec => new(
        ConnectField.Info("Generic S3-compatible storage — Hetzner Object Storage, MinIO, Wasabi, Backblaze B2, Cloudflare R2, DigitalOcean Spaces, Linode, and the like. Paste the endpoint URL from your provider's console along with the access key it issued."),
        new ConnectField(AccessKeyIdKey, ConnectFieldKind.Text, "Access Key ID"),
        SecretField(),
        new ConnectField(EndpointKey, ConnectFieldKind.Choice, "Endpoint URL")
        {
            Choices = EndpointPresets,
            AllowsCustomValue = true,
            Placeholder = "Endpoint URL (e.g. https://fsn1.your-objectstorage.com)",
        },
        new ConnectField(RegionKey, ConnectFieldKind.Text, "Region")
        {
            Required = false,
            Placeholder = "Region (leave empty to auto-detect, e.g. fsn1, us-east-1, auto)",
            Help = "Region tips: Hetzner uses the location code (fsn1, nbg1, hel1). Cloudflare R2 expects \"auto\". MinIO and most Backblaze B2 setups want \"us-east-1\".",
        },
        new ConnectField(DisplayNameKey, ConnectFieldKind.Text, "Display name (optional)")
        {
            Required = false,
            Placeholder = "Display name (optional, e.g. \"Hetzner FSN1\")",
        },
        PathField());

    /// <summary>
    /// The endpoint may be a bare host, a URL or host:port; an empty region is guessed from
    /// the host. Named after what the user typed, or "S3 (host)", plus " — bucket/folder" when
    /// scoped. Editing keeps the bucket scope (the macOS edit path dropped it).
    /// </summary>
    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var accessKeyId = values.GetTrimmed(AccessKeyIdKey);
        var secret = SecretOrStored(values);
        var host = HostFromEndpoint(values.GetTrimmed(EndpointKey));
        if (host.Length == 0)
        {
            throw CloudProviderException.CommandFailed(
                Localization.L("S3 endpoint URL is invalid (host: \"{0}\"). Double-check the Endpoint URL field.", host));
        }

        var typedRegion = values.GetTrimmed(RegionKey);
        var region = typedRegion.Length == 0 ? RegionGuess(host) : typedRegion;
        var typedName = values.GetTrimmed(DisplayNameKey);
        var name = typedName.Length == 0 ? $"S3 ({host})" : typedName;
        var rootPath = NormalizeRootPath(values.Get(PathKey));

        await ConnectWithAsync(new S3Credentials(accessKeyId, secret, region, name, host, rootPath), cancellationToken).ConfigureAwait(false);
        return new ConnectResult(name) { DisplayName = WithRoot(name, rootPath), RootPath = rootPath ?? "/" };
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
            [EndpointKey] = stored.EndpointHost ?? string.Empty,
            [RegionKey] = stored.Region,
            [DisplayNameKey] = stored.DisplayName,
            [PathKey] = EditablePath(),
        };
    }

    /// <summary>
    /// A bare host ("fsn1.your-objectstorage.com") from whatever was typed: a URL, a host, or
    /// host:port. An explicit non-default port is kept — macOS dropped it from URLs, which
    /// made a MinIO on :9000 unreachable. Requests always use HTTPS, as on macOS.
    /// </summary>
    public static string HostFromEndpoint(string endpoint)
    {
        var trimmed = endpoint.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && uri.Host.Length > 0)
        {
            return HasExplicitPort(trimmed) && !uri.IsDefaultPort ? $"{uri.Host}:{uri.Port}" : uri.Host;
        }

        // Not a URL: drop a stray scheme and a trailing slash.
        var text = trimmed;
        foreach (var scheme in new[] { "https://", "http://" })
        {
            if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                text = text[scheme.Length..];
                break;
            }
        }

        return text.TrimEnd('/');
    }

    /// <summary>
    /// The SigV4 region when the field is left blank: the first label of the host, which fits
    /// Hetzner's "fsn1.your-objectstorage.com" and Synology C2's "eu-005.s3.synologyc2.net".
    /// Backblaze puts the service first ("s3.eu-central-003.backblazeb2.com"), so a leading
    /// "s3" takes the second label — B2 accepts "s3", other services don't. Cloudflare R2's
    /// host starts with the account id; R2 signs with "auto". Otherwise "us-east-1", the
    /// MinIO/Backblaze convention.
    /// </summary>
    public static string RegionGuess(string host)
    {
        var bare = host.Split(':')[0];
        if (bare.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase))
        {
            return "auto";
        }

        var labels = bare.Split('.');
        var head = labels[0];
        if (head.Length == 0)
        {
            return "us-east-1";
        }

        if (head.Equals("s3", StringComparison.OrdinalIgnoreCase) && labels.Length > 1 && labels[1].Length > 0)
        {
            return labels[1];
        }

        return head;
    }

    /// <summary>True when the authority of a URL names a port ("https://h:9000/x"), IPv6 literals included.</summary>
    private static bool HasExplicitPort(string url)
    {
        var start = url.IndexOf("://", StringComparison.Ordinal);
        var authority = start < 0 ? url : url[(start + 3)..];
        var end = authority.IndexOfAny(['/', '?', '#']);
        authority = end < 0 ? authority : authority[..end];
        var at = authority.LastIndexOf('@');
        authority = at < 0 ? authority : authority[(at + 1)..];
        var bracket = authority.LastIndexOf(']');
        return authority.IndexOf(':', bracket + 1) >= 0;
    }
}
