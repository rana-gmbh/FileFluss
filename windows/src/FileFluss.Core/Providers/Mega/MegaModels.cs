// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Text.Json;
using FileFluss.Core.Models;

namespace FileFluss.Core.Providers.Mega;

/// <summary>
/// What a MEGA sign-in leaves behind (the macOS <c>MegaCredentials</c>): the session id and
/// the decrypted 128-bit master key as four big-endian words. The password is never stored.
/// </summary>
internal sealed record MegaCredentials(string SessionId, uint[] MasterKey, string Email);

/// <summary>One node of the account's tree. Types: 0 file, 1 folder, 2 Cloud Drive root, 3 Inbox, 4 Trash.</summary>
internal sealed record MegaNode(string Handle, string ParentHandle, string Name, int Type, long Size, double Timestamp)
{
    public bool IsFile => Type == 0;

    public CloudFileItem ToItem(string path) => new(
        Handle,
        Name,
        path,
        isDirectory: Type != 0,
        Size,
        MegaJson.UnixDate(Timestamp));
}

/// <summary>Lenient readers for MEGA's JSON, which sends numbers as numbers or strings depending on size.</summary>
internal static class MegaJson
{
    public static string? GetString(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static long? GetInt64(this JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var n) => n,
            JsonValueKind.Number => (long)value.GetDouble(),
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
            _ => null,
        };
    }

    public static double? GetDouble(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    /// <summary>Seconds since 1970; 0 (MEGA's "none") becomes the unknown date.</summary>
    public static DateTimeOffset UnixDate(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds) || seconds > 253_402_300_799)
        {
            return CloudFileItem.UnknownDate;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
    }
}
