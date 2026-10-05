// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Formatting;
using FileFluss.Core.Models;
using FileFluss.Core.Services;
using FileFluss.Core.Settings;

namespace FileFluss.Core.Tests;

[Collection("Localization")]
public sealed class CloudQuotaFormatterTests
{
    private const long GB = 1L << 30;

    public CloudQuotaFormatterTests() => Localization.Use(AppLanguage.English);

    [Fact]
    public void UsedAndTotalWithWholePercent()
        => Assert.Equal("Dropbox: 4.00 GB of 16.0 GB used (25%)", CloudQuotaFormatter.Summary(new CloudStorageQuota(4 * GB, 16 * GB), "Dropbox"));

    [Fact]
    public void NoTotalShowsUsedOnly()
    {
        Assert.Equal("Google Drive: 402 KB used", CloudQuotaFormatter.Summary(new CloudStorageQuota(412_000, null), "Google Drive"));

        // A zero total means "unlimited", not "100% full".
        Assert.Equal("pCloud: 402 KB used", CloudQuotaFormatter.Summary(new CloudStorageQuota(412_000, 0), "pCloud"));
    }

    [Fact]
    public void PercentClampsRoundsAndKeepsSmallUsageVisible()
    {
        Assert.Equal("100%", CloudQuotaFormatter.PercentString(1_100_000_000, 1_000_000_000));
        Assert.Equal("0%", CloudQuotaFormatter.PercentString(0, 100_000_000_000));
        Assert.Equal("0%", CloudQuotaFormatter.PercentString(1234, 0));
        Assert.Equal("3%", CloudQuotaFormatter.PercentString(25, 1000)); // 2.5 rounds away from zero, as on macOS
        Assert.Equal("0.6%", CloudQuotaFormatter.PercentString(12 * GB, 2048 * GB));
        Assert.Equal("0.0%", CloudQuotaFormatter.PercentString(10_000_000, 1_000_000_000_000));
    }
}

public sealed class SidebarLayoutTests
{
    [Theory]
    [InlineData(44, SidebarLayout.CompactWidth)]
    [InlineData(71.9, SidebarLayout.CompactWidth)]
    [InlineData(72, SidebarLayout.ExpandedMinWidth)]
    [InlineData(95, SidebarLayout.ExpandedMinWidth)]
    [InlineData(96, 96)]
    [InlineData(300, 300)]
    public void NarrowWidthsSnapToTheTwoDetents(double raw, double expected) => Assert.Equal(expected, SidebarLayout.Snap(raw));

    [Fact]
    public void CompactBelowTheMidpoint()
    {
        Assert.True(SidebarLayout.IsCompact(SidebarLayout.CompactWidth));
        Assert.False(SidebarLayout.IsCompact(SidebarLayout.ExpandedMinWidth));
        Assert.False(SidebarLayout.IsCompact(SidebarLayout.DefaultWidth));
    }
}

public sealed class ReorderingTests
{
    [Theory]
    [InlineData(0, 0, 0)] // dropped above itself
    [InlineData(0, 1, 0)] // dropped below itself: no move
    [InlineData(0, 2, 1)]
    [InlineData(0, 4, 3)] // below the last row
    [InlineData(3, 0, 0)]
    [InlineData(2, 9, 3)] // out of range clamps
    public void TargetIndexAccountsForTheGapTheItemLeaves(int from, int insertion, int expected)
        => Assert.Equal(expected, Reordering.TargetIndex(from, insertion, 4));

    [Fact]
    public void MoveReordersACopy()
    {
        string[] items = ["a", "b", "c", "d"];
        Assert.Equal(["b", "c", "a", "d"], Reordering.Move(items, s => s == "a", 3));
        Assert.Equal(["d", "a", "b", "c"], Reordering.Move(items, s => s == "d", 0));
        Assert.Equal(items, Reordering.Move(items, s => s == "x", 0));
        Assert.Equal(["a", "b", "c", "d"], items);
    }

    [Fact]
    public void AccountOrderSurvivesTheStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "ff-accounts-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new AccountStore(path);
            var accounts = new List<CloudAccount>
            {
                new() { ProviderType = CloudProviderType.Dropbox, DisplayName = "Dropbox" },
                new() { ProviderType = CloudProviderType.Box, DisplayName = "Box" },
                new() { ProviderType = CloudProviderType.PCloud, DisplayName = "pCloud" },
            };
            store.Save(Reordering.Move(accounts, a => a.Id == accounts[2].Id, 0));
            Assert.Equal(["pCloud", "Dropbox", "Box"], new AccountStore(path).Load().Select(a => a.DisplayName));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AStoreThatCouldNotBeReadIsNotWipedByAnEmptySave()
    {
        var path = Path.Combine(Path.GetTempPath(), "ff-accounts-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{ not json");
            var store = new AccountStore(path);
            Assert.Empty(store.Load());
            store.Save([]);
            Assert.Equal("{ not json", File.ReadAllText(path));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
            {
                File.Delete(file);
            }
        }
    }
}
