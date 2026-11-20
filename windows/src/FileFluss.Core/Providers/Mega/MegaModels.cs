// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FileFluss.Core.Models;

namespace FileFluss.Core.Providers.Mega;

/// <summary>
/// What a MEGA sign-in leaves behind (the macOS <c>MegaCredentials</c>): the session id and
/// the decrypted 128-bit master key as four big-endian words. The password is never stored.
/// </summary>
internal sealed record MegaCredentials(string SessionId, uint[] MasterKey, string Email);

/// <summary>One node of the account's tree. Types: 0 file, 1 folder, 2 Cloud Drive root, 3 Inbox, 4 Trash.</summary>
/// <param name="Attributes">
/// The decrypted attribute JSON, kept so that changing one attribute can write all the others
/// back. Null for the root, the inbox and the trash.
/// </param>
internal sealed record MegaNode(string Handle, string ParentHandle, string Name, int Type, long Size, double Timestamp, string? Attributes = null)
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

/// <summary>
/// The JSON a MEGA item's attributes are kept in: <c>{"n":"name","lbl":"3",…}</c> (macOS
/// <c>MegaNodeAttributes</c>).
///
/// <para>MEGA replaces the block whole, so a change to one attribute has to carry every other
/// one along. The label is written the way MEGA's own apps write it — a string holding the
/// number — and removed by leaving the key out; it is read whether it arrives as a string or
/// as a number.</para>
/// </summary>
internal static class MegaAttributes
{
    private const string LabelKey = "lbl";
    private const string NameKey = "n";

    /// <summary>The label in an attribute block: 1…7, or 0 for none.</summary>
    public static int Label(string? json)
    {
        if (Parse(json) is not { } attributes || attributes[LabelKey] is not JsonValue value)
        {
            return 0;
        }

        int label;
        if (value.TryGetValue<string>(out var text))
        {
            if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out label))
            {
                return 0;
            }
        }
        else if (!value.TryGetValue(out label))
        {
            return 0;
        }

        return label is >= 1 and <= 7 ? label : 0;
    }

    /// <summary>
    /// The block with the name set and, when <paramref name="label"/> is given, the label set
    /// (1…7) or removed (0). Everything else stays as it was.
    /// </summary>
    public static string Json(string? json, string name, int? label = null)
    {
        var attributes = Parse(json) ?? [];
        attributes[NameKey] = name;
        if (label is { } value)
        {
            if (value is >= 1 and <= 7)
            {
                attributes[LabelKey] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                attributes.Remove(LabelKey);
            }
        }

        return attributes.ToJsonString(MegaCrypto.AttributeJson);
    }

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>MEGA numbers its seven label colours in its own order.</summary>
    public static TagColor TagColorFor(int label) => label switch
    {
        1 => TagColor.Red,
        2 => TagColor.Orange,
        3 => TagColor.Yellow,
        4 => TagColor.Green,
        5 => TagColor.Blue,
        6 => TagColor.Purple,
        7 => TagColor.Gray,
        _ => TagColor.None,
    };

    public static int LabelFor(TagColor color) => color switch
    {
        TagColor.Red => 1,
        TagColor.Orange => 2,
        TagColor.Yellow => 3,
        TagColor.Green => 4,
        TagColor.Blue => 5,
        TagColor.Purple => 6,
        TagColor.Gray => 7,
        _ => 0,
    };
}
