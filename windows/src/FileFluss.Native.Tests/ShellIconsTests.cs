// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Native.Tests;

/// <summary>
/// The system image list is one COM object per process. Icons are taken from it on the UI
/// thread and on a background thread (the icon worker) at the same time, so it must work from
/// every apartment while the others are alive.
/// </summary>
public sealed class ShellIconsTests
{
    [Fact]
    public void IconsComeFromTheSystemImageListOnSeveralLiveThreads()
    {
        var index = ShellIcons.IconIndex("folder", isDirectory: true, exists: false);
        Assert.True(index >= 0);

        using var firstAsked = new ManualResetEventSlim();
        using var othersDone = new ManualResetEventSlim();
        var results = new System.Collections.Concurrent.ConcurrentBag<(string Thread, bool Ok)>();

        bool Ask()
        {
            foreach (var size in new[] { ShellIconSize.Large, ShellIconSize.ExtraLarge, ShellIconSize.Small })
            {
                var icon = ShellIcons.IconFromIndex(index, size);
                if (icon == 0)
                {
                    return false;
                }

                ShellIcons.DestroyIcon(icon);
            }

            return true;
        }

        // The first thread asks, then stays alive (as the UI thread does) while the others ask.
        var first = new Thread(() =>
        {
            results.Add(("first STA", Ask()));
            firstAsked.Set();
            othersDone.Wait(TimeSpan.FromSeconds(10));
        });
        first.SetApartmentState(ApartmentState.STA);
        first.Start();
        firstAsked.Wait(TimeSpan.FromSeconds(10));

        foreach (var apartment in new[] { ApartmentState.STA, ApartmentState.MTA })
        {
            var other = new Thread(() =>
            {
                try
                {
                    results.Add(($"second {apartment}", Ask()));
                }
                catch (InvalidCastException)
                {
                    results.Add(($"second {apartment}", false));
                }
            });
            other.SetApartmentState(apartment);
            other.Start();
            other.Join();
        }

        othersDone.Set();
        first.Join();
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.Ok, r.Thread));
    }
}
