// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;

namespace FileFluss.Core.Tests;

/// <summary>The context menu's share entries, macOS rules.</summary>
public class ShareLinkMenuTests
{
    private static readonly ShareLinkCapabilities Full = new()
    {
        CanCreate = true,
        CanQueryExisting = true,
        CanUpdate = true,
        CanRemove = true,
        SupportsPassword = true,
    };

    [Fact]
    public void UnsharedFileOffersTheOptionsDialogWhenThereAreOptions()
    {
        var menu = ShareLinkMenu.For(Full, isShared: false);

        Assert.Equal(new ShareLinkMenu(SharedMarker: false, ChangeSharing: false, StopSharing: false, ShareWithOptions: true), menu);
    }

    [Fact]
    public void UnknownStateLooksLikeUnshared()
        => Assert.Equal(ShareLinkMenu.For(Full, isShared: false), ShareLinkMenu.For(Full, isShared: null));

    [Fact]
    public void SharedFileGetsMarkerChangeAndStop()
    {
        var menu = ShareLinkMenu.For(Full, isShared: true);

        Assert.Equal(new ShareLinkMenu(SharedMarker: true, ChangeSharing: true, StopSharing: true, ShareWithOptions: false), menu);
    }

    [Fact]
    public void ChangeSharingNeedsUpdateAndOptions()
    {
        var noOptions = Full with { SupportsPassword = false };
        var noUpdate = Full with { CanUpdate = false };

        Assert.False(ShareLinkMenu.For(noOptions, isShared: true).ChangeSharing);
        Assert.False(ShareLinkMenu.For(noUpdate, isShared: true).ChangeSharing);
        Assert.False(ShareLinkMenu.For(Full with { CanRemove = false }, isShared: true).StopSharing);
    }

    [Fact]
    public void ProvidersThatCannotReadLinksBackNeverShowShared()
    {
        // S3-style: a fresh presigned URL every time, nothing to withdraw.
        var s3 = new ShareLinkCapabilities { CanCreate = true, SupportsExpiry = true, RequiresExpiry = true };

        var menu = ShareLinkMenu.For(s3, isShared: true);

        Assert.False(menu.SharedMarker);
        Assert.False(menu.StopSharing);
        Assert.True(menu.ShareWithOptions);
    }

    [Fact]
    public void NoOptionsMeansOnlyCopyShareLink()
    {
        var plain = new ShareLinkCapabilities { CanCreate = true };

        Assert.Equal(new ShareLinkMenu(false, false, false, false), ShareLinkMenu.For(plain, isShared: null));
    }

    [Theory]
    [InlineData("Forbidden", true)]
    [InlineData("HTTP 403", true)]
    [InlineData("This feature requires a Premium subscription", true)]
    [InlineData("The network connection was lost.", false)]
    [InlineData("Invalid password", false)]
    public void PlanRefusalIsRecognised(string message, bool expected)
        => Assert.Equal(expected, ShareLinkRefusal.IsPlanRefusal(message));

    [Fact]
    public void RefusedOptionsNamesWhatWasAskedFor()
    {
        var both = new ShareLinkOptions { Password = "pw", Expiry = DateTimeOffset.Now.AddDays(1) };
        var password = new ShareLinkOptions { Password = "pw" };
        var expiry = new ShareLinkOptions { Expiry = DateTimeOffset.Now.AddDays(1) };

        Assert.Equal("a password and an expiry date", ShareLinkRefusal.RefusedOptionsKey(both, "forbidden"));
        Assert.Equal("a password", ShareLinkRefusal.RefusedOptionsKey(password, "forbidden"));
        Assert.Equal("an expiry date", ShareLinkRefusal.RefusedOptionsKey(expiry, "forbidden"));
        Assert.Null(ShareLinkRefusal.RefusedOptionsKey(ShareLinkOptions.Default, "forbidden"));
        Assert.Null(ShareLinkRefusal.RefusedOptionsKey(password, "timed out"));
    }
}

/// <summary>The saved support log: the macOS layout with a Windows header.</summary>
public class SupportLogReportTests
{
    private static readonly SupportLogEnvironment Machine = new("1.2.3", "abc123", "Microsoft Windows 10.0.26100 (24H2)", "X64 (process X64)", "de-DE", "Contoso Laptop 7");

    [Fact]
    public void HeaderNamesVersionWindowsAndArchitecture()
    {
        var text = SupportLogReport.Build(Machine, [], new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));

        Assert.StartsWith("FileFluss Support Log\n=====================\nGenerated: 2026-10-05T12:00:00Z\n", text, StringComparison.Ordinal);
        Assert.Contains("App version: 1.2.3 (build abc123)\n", text, StringComparison.Ordinal);
        Assert.Contains("Windows: Microsoft Windows 10.0.26100 (24H2)\n", text, StringComparison.Ordinal);
        Assert.Contains("Architecture: X64 (process X64)\n", text, StringComparison.Ordinal);
        Assert.Contains("Locale: de-DE\n", text, StringComparison.Ordinal);
        Assert.Contains("Hardware: Contoso Laptop 7\n\n--- Events captured ---\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyWindowSaysSo()
        => Assert.EndsWith("--- Events captured ---\n(no events captured during this window)\n", SupportLogReport.Build(Machine, [], DateTimeOffset.Now), StringComparison.Ordinal);

    [Fact]
    public void EntriesUseTheMacLineFormat()
    {
        var date = new DateTimeOffset(2026, 10, 5, 14, 3, 7, 250, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 5)));
        var entries = new[] { new SupportLogger.LogEntry(date, LogLevel.Notice, "support", "Support log recording started") };

        var text = SupportLogReport.Build(Machine, entries, DateTimeOffset.Now);

        Assert.EndsWith("14:03:07.250 [NOTICE] [support] Support log recording started\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FileNameCarriesTheTimestamp()
    {
        var date = new DateTimeOffset(2026, 10, 5, 9, 8, 7, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 5)));

        Assert.Equal("FileFluss-support-20261005-090807.log", SupportLogReport.FileName(date));
    }
}

/// <summary>Settings → Storage: the size limit, and trimming to it.</summary>
public class CacheLimitTests
{
    [Theory]
    [InlineData(100, 100)]
    [InlineData(124, 100)]
    [InlineData(125, 150)]
    [InlineData(1_337, 1_350)]
    [InlineData(20, CacheManager.MinSizeMB)]
    [InlineData(99_999, CacheManager.MaxSizeMB)]
    public void SliderSnapsToFiftyMegabytes(double value, int expected)
        => Assert.Equal(expected, CacheManager.SnapSliderMB(value));

    [Theory]
    [InlineData("750", 2000, 750)]
    [InlineData(" 3000 ", 2000, 3000)]
    [InlineData("12", 2000, CacheManager.MinSizeMB)]
    [InlineData("50000", 2000, CacheManager.MaxSizeMB)]
    [InlineData("lots", 2000, 2000)]
    [InlineData("", 2000, 2000)]
    public void FieldIsParsedAndClamped(string text, int current, int expected)
        => Assert.Equal(expected, CacheManager.ParseFieldMB(text, current));

    [Fact]
    public void TrimRemovesOldestUntilUnderTheLimit()
    {
        var now = DateTime.UtcNow;
        var entries = new[]
        {
            ("new", 40L, now),
            ("oldest", 30L, now.AddDays(-3)),
            ("middle", 30L, now.AddDays(-2)),
        };

        var deleted = CacheManager.TrimToLimit(entries, 50, _ => true);

        Assert.Equal(["oldest", "middle"], deleted);
    }

    [Fact]
    public void TrimSkipsFilesInUseAndTakesTheNextOldest()
    {
        var now = DateTime.UtcNow;
        var entries = new[]
        {
            ("open", 30L, now.AddDays(-3)),
            ("older", 30L, now.AddDays(-2)),
            ("newest", 30L, now),
        };

        var deleted = CacheManager.TrimToLimit(entries, 60, path => path != "open");

        Assert.Equal(["older"], deleted);
    }

    [Fact]
    public void TrimLeavesACacheUnderTheLimitAlone()
        => Assert.Empty(CacheManager.TrimToLimit([("a", 10L, DateTime.UtcNow)], 100, _ => true));
}
