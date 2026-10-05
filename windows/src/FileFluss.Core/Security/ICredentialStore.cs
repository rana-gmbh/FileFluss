// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Text.Json;

namespace FileFluss.Core.Security;

/// <summary>
/// Secret storage — the Windows counterpart of the macOS <c>KeychainService</c>. Keys follow
/// the macOS scheme (<c>dropbox.&lt;account-uuid&gt;</c>). The app's implementation encrypts
/// each entry with DPAPI for the current user; tests use <see cref="MemoryCredentialStore"/>.
/// </summary>
public interface ICredentialStore
{
    void Save(string key, byte[] data);

    byte[]? Load(string key);

    void Delete(string key);
}

public static class CredentialStoreExtensions
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Stores <paramref name="value"/> as JSON.</summary>
    public static void Save<T>(this ICredentialStore store, string key, T value)
        => store.Save(key, JsonSerializer.SerializeToUtf8Bytes(value, Options));

    /// <summary>The stored value, or null when absent or no longer decodable.</summary>
    public static T? Load<T>(this ICredentialStore store, string key)
        where T : class
    {
        var data = store.Load(key);
        if (data is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(data, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>In-memory store for tests and for providers probed before an account exists.</summary>
public sealed class MemoryCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<string, byte[]> _items = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Keys => _items.Keys.ToList();

    public void Save(string key, byte[] data) => _items[key] = data.ToArray();

    public byte[]? Load(string key) => _items.TryGetValue(key, out var data) ? data.ToArray() : null;

    public void Delete(string key) => _items.TryRemove(key, out _);
}
