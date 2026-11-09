// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileFluss.Core.Models;

namespace FileFluss.Core.Providers.Drime;

/// <summary>
/// How the account was connected. Decides whether a 401 can be recovered by refreshing
/// (OAuth) or means the token was deleted (a personal access token never expires on its own).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DrimeCredentialKind>))]
internal enum DrimeCredentialKind
{
    [JsonStringEnumMemberName("oauth")] OAuth,
    [JsonStringEnumMemberName("personalToken")] PersonalToken,
}

/// <param name="RefreshToken">Empty for a personal access token.</param>
/// <param name="ExpiresAt"><see cref="DateTimeOffset.MaxValue"/> for a personal access token.</param>
internal sealed record DrimeCredentials(
    DrimeCredentialKind Kind,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string Email,
    string DisplayName)
{
    public static DrimeCredentials PersonalToken(string token, string email, string displayName)
        => new(DrimeCredentialKind.PersonalToken, token, string.Empty, DateTimeOffset.MaxValue, email, displayName);

    /// <summary>The account's label: the display name, or the email when there is none.</summary>
    public string Label => DisplayName.Length == 0 ? Email : DisplayName;
}

/// <summary>
/// A Drime file entry, decoded leniently: ids and sizes arrive as numbers or numeric strings
/// depending on the route, and most fields are nullable.
/// </summary>
internal sealed record DrimeEntry(
    long Id,
    string Name,
    string Type,
    long FileSize,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? ClientLastModified,
    string? FileHash)
{
    /// <summary>The personal Drive's root has no entry of its own; the API spells it as an omitted folder id.</summary>
    public static readonly DrimeEntry Root = new(0, string.Empty, "folder", 0, null, null, null);

    /// <summary>Only "folder" is a folder: Drime types files by content ("image", "text"), and none of those may be browsed into.</summary>
    public bool IsFolder => Type == "folder";

    public static DrimeEntry Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || DrimeJson.Int64(element, "id") is not { } id)
        {
            throw CloudProviderException.InvalidResponse();
        }

        var name = DrimeJson.String(element, "name") ?? throw CloudProviderException.InvalidResponse();
        var clientModified = DrimeJson.Int64(element, "client_last_modified");
        return new DrimeEntry(
            id,
            name,
            DrimeJson.String(element, "type") ?? "file",
            DrimeJson.Int64(element, "file_size") ?? 0,
            DrimeJson.ParseTimestamp(DrimeJson.String(element, "updated_at")),

            // Original mtime on the uploading machine, Unix milliseconds; 0 is "none", not 1970.
            clientModified is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(clientModified.Value) : null,
            DrimeJson.String(element, "file_hash") is { Length: > 0 } hash ? hash : null);
    }

    /// <summary>
    /// The panel row. The client's own modification time wins over the server's
    /// <c>updated_at</c>: it is what a sync compares.
    /// </summary>
    public CloudFileItem ToItem(string parentPath)
    {
        var path = DrimeApiClient.Join(parentPath, Name);
        return new CloudFileItem(path, Name, path, IsFolder, IsFolder ? 0 : FileSize, ClientLastModified ?? UpdatedAt ?? CloudFileItem.UnknownDate, FileHash)
        {
            IsHidden = CloudFileItem.IsDotHidden(Name),
        };
    }
}

/// <summary>JSON helpers for Drime's loosely typed payloads.</summary>
internal static class DrimeJson
{
    public static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A number, a fractional number or a numeric string.</summary>
    public static long? Int64(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var whole) => whole,
            JsonValueKind.Number when value.TryGetDouble(out var real) => (long)real,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>Drime writes <c>2024-01-20T15:30:00.000000Z</c>, sometimes without the fraction.</summary>
    public static DateTimeOffset? ParseTimestamp(string? raw)
        => !string.IsNullOrEmpty(raw) &&
           DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;

    /// <summary>
    /// The way Drime writes timestamps, <c>yyyy-MM-ddTHH:mm:ss.SSSSSSZ</c> in UTC — microseconds
    /// as macOS formats them: milliseconds, then zeros.
    /// </summary>
    public static string FormatTimestamp(DateTimeOffset date)
        => date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'000Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>{"message": …}</c>, or the first field error of a 422 when there is one — Laravel's
    /// top-level message for those is just "The given data was invalid."
    /// </summary>
    public static string? ServerMessage(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in errors.EnumerateObject())
                {
                    if (field.Value.ValueKind == JsonValueKind.Array &&
                        field.Value.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.String } first &&
                        first.GetString() is { Length: > 0 } listed)
                    {
                        return listed;
                    }

                    if (field.Value.ValueKind == JsonValueKind.String && field.Value.GetString() is { Length: > 0 } text)
                    {
                        return text;
                    }
                }
            }

            return String(root, "message") is { Length: > 0 } message ? message : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The error for an unsuccessful Drime response.</summary>
    public static CloudProviderException MapHttpError(int status, string? body)
    {
        var message = ServerMessage(body);
        return status switch
        {
            401 => CloudProviderException.NotAuthenticated(),

            // A token whose permissions don't cover the call says which scope is missing —
            // worth showing as is.
            403 when message is not null && (message.Contains("scope", StringComparison.Ordinal) || message.Contains("restricted", StringComparison.Ordinal))
                => CloudProviderException.CommandFailed(message),
            403 => CloudProviderException.Unauthorized(),
            404 => CloudProviderException.NotFound(message ?? Localization.L("Resource not found")),
            413 => CloudProviderException.FileTooLarge(0, null),
            422 when message is not null => CloudProviderException.CommandFailed(message),
            429 => CloudProviderException.RateLimited(),
            507 => CloudProviderException.QuotaExceeded(),
            _ => CloudProviderException.ServerError(status),
        };
    }
}
