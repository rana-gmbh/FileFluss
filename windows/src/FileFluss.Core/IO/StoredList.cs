// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace FileFluss.Core.IO;

/// <summary>
/// Reads and writes a stored JSON list one element at a time (macOS <c>StoredList</c>).
///
/// <para>Decoding a whole <c>List&lt;T&gt;</c> at once fails on a single element this version
/// doesn't understand — an account for a provider added in a newer version, a damaged entry —
/// and the list then looked empty or fell back to an older copy. Here an unreadable element is
/// set aside and written back untouched, so a newer version finds it again and an older one
/// can't lose it.</para>
/// </summary>
public static class StoredList
{
    /// <summary>The readable elements and, as raw JSON, the others. Null when <paramref name="json"/> isn't a JSON array at all.</summary>
    public static (List<T> Items, List<JsonNode> Unreadable)? Decode<T>(string json, JsonSerializerOptions options)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        return root is JsonArray array ? Split<T>(array, options) : null;
    }

    /// <summary>Takes the elements of <paramref name="array"/> that can't be read as <typeparamref name="T"/> out of it, in order.</summary>
    public static List<JsonNode> RemoveUnreadable<T>(JsonArray array, JsonSerializerOptions options)
    {
        var unreadable = new List<JsonNode>();
        for (var index = 0; index < array.Count;)
        {
            if (TryRead<T>(array[index], options, out _))
            {
                index++;
                continue;
            }

            if (array[index] is { } element)
            {
                unreadable.Add(element.DeepClone());
            }

            array.RemoveAt(index);
        }

        return unreadable;
    }

    /// <summary>The list as JSON, the set-aside elements after the readable ones.</summary>
    public static JsonArray Encode<T>(IReadOnlyList<T> items, IReadOnlyList<JsonNode> unreadable, JsonSerializerOptions options)
    {
        var array = JsonSerializer.SerializeToNode(items, options) as JsonArray ?? [];
        Append(array, unreadable);
        return array;
    }

    /// <summary>Adds copies of the set-aside elements to an array about to be written.</summary>
    public static void Append(JsonArray array, IReadOnlyList<JsonNode> unreadable)
    {
        foreach (var element in unreadable)
        {
            array.Add(element.DeepClone());
        }
    }

    private static (List<T> Items, List<JsonNode> Unreadable) Split<T>(JsonArray array, JsonSerializerOptions options)
    {
        var items = new List<T>();
        var unreadable = new List<JsonNode>();
        foreach (var element in array)
        {
            if (TryRead<T>(element, options, out var item))
            {
                items.Add(item);
            }
            else if (element is not null)
            {
                unreadable.Add(element.DeepClone());
            }
        }

        return (items, unreadable);
    }

    private static bool TryRead<T>(JsonNode? element, JsonSerializerOptions options, out T item)
    {
        item = default!;
        if (element is null)
        {
            return false;
        }

        try
        {
            if (element.Deserialize<T>(options) is { } read)
            {
                item = read;
                return true;
            }
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or FormatException or ArgumentException)
        {
        }

        return false;
    }
}
