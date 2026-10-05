// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.IO;

namespace FileFluss.Core.Tests.Security;

public sealed class MarkOfTheWebTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "ff-motw-" + Guid.NewGuid().ToString("N") + ".exe");

    public MarkOfTheWebTests() => File.WriteAllText(_file, "MZ");

    public void Dispose() => File.Delete(_file);

    [Fact]
    public void AnOpenedCloudFileIsMarkedAsComingFromTheInternet()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var stamp = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_file, stamp);
        Assert.Null(MarkOfTheWeb.Zone(_file));

        Assert.True(MarkOfTheWeb.Apply(_file));

        Assert.Equal(3, MarkOfTheWeb.Zone(_file));
        Assert.Contains("ZoneId=3", File.ReadAllText(_file + ":Zone.Identifier"), StringComparison.Ordinal);

        // The main stream and its date are untouched: the open cache still sees a fresh copy.
        Assert.Equal("MZ", File.ReadAllText(_file));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(_file));

        // Idempotent.
        Assert.True(MarkOfTheWeb.Apply(_file));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(_file));
    }

    [Fact]
    public void AMissingFileIsNotAnError()
        => Assert.False(MarkOfTheWeb.Apply(_file + ".missing"));
}
