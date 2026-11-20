// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Reflection;
using FileFluss.Core.Models;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers;

/// <summary>
/// Marks a <see cref="CloudProvider"/> as the implementation of one provider type. The class
/// needs a public constructor taking <c>(Guid accountId, ICredentialStore credentials)</c>.
/// Registration by attribute keeps each provider self-contained: adding one touches no
/// shared file.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CloudProviderAttribute(CloudProviderType type) : Attribute
{
    public CloudProviderType Type { get; } = type;
}

/// <summary>Creates providers by type — the macOS <c>SyncEngine.createProvider(for:)</c> switch.</summary>
public static class CloudProviderRegistry
{
    public delegate CloudProvider Factory(Guid accountId, ICredentialStore credentials);

    private static readonly ConcurrentDictionary<CloudProviderType, Factory> Factories = new();
    private static readonly HashSet<Assembly> Scanned = [];
    private static readonly Lock Gate = new();

    static CloudProviderRegistry()
    {
        Scan(typeof(CloudProviderRegistry).Assembly);
    }

    /// <summary>Registers every <see cref="CloudProviderAttribute"/> class in <paramref name="assembly"/> (the app adds its own).</summary>
    public static void Scan(Assembly assembly)
    {
        lock (Gate)
        {
            if (!Scanned.Add(assembly))
            {
                return;
            }
        }

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(CloudProvider).IsAssignableFrom(type))
            {
                continue;
            }

            var attribute = type.GetCustomAttribute<CloudProviderAttribute>();
            var constructor = type.GetConstructor([typeof(Guid), typeof(ICredentialStore)]);
            if (attribute is null || constructor is null)
            {
                continue;
            }

            Factories[attribute.Type] = (id, store) => (CloudProvider)constructor.Invoke([id, store]);
        }
    }

    public static void Register(CloudProviderType type, Factory factory) => Factories[type] = factory;

    public static bool IsAvailable(CloudProviderType type) => Factories.ContainsKey(type);

    public static IReadOnlyList<CloudProviderType> AvailableTypes
        => Enum.GetValues<CloudProviderType>().Where(IsAvailable).ToList();

    public static CloudProvider Create(CloudProviderType type, Guid accountId, ICredentialStore credentials)
        => Factories.TryGetValue(type, out var factory)
            ? factory(accountId, credentials)
            : throw new NotSupportedException($"No provider is registered for {type}.");
}
