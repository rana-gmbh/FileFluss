// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using FileFluss.Core.Compare;

namespace FileFluss.Core.Tests.Compare;

public sealed class CompareSelectionTests
{
    private sealed record Node(string Path, bool IsDirectory);

    private static List<string> Topmost(params Node[] selected)
        => CompareSelection.Topmost(selected, n => n.Path, n => n.IsDirectory).Select(n => n.Path).ToList();

    [Fact]
    public void EntriesInsideASelectedFolderAreLeftOut()
    {
        Assert.Equal(
            ["a", "z.txt"],
            Topmost(new("a", true), new("a/b", true), new("a/b/c.txt", false), new("a/d.txt", false), new("z.txt", false)));
    }

    [Fact]
    public void ANameThatOnlyStartsLikeASelectedFolderIsNotInsideIt()
    {
        // "a/bc" is not inside "a/b", nor "ab.txt" inside "a".
        Assert.Equal(["a/b", "a/bc", "a/bc/x.txt"], Topmost(new("a/b", true), new("a/bc", false), new("a/bc/x.txt", false)));
        Assert.Equal(["a", "ab.txt"], Topmost(new("a", true), new("ab.txt", false)));
    }

    [Fact]
    public void AFileIsNeverTreatedAsAFolder()
    {
        Assert.Equal(["a", "a/b.txt"], Topmost(new("a", false), new("a/b.txt", false)));
    }

    [Fact]
    public void TheOriginalOrderIsKept()
    {
        Assert.Equal(["z", "b.txt", "a"], Topmost(new("z", true), new("b.txt", false), new("z/q", false), new("a", true)));
    }

    [Fact]
    public void ADeepFolderOfTwentyThousandSelectedRowsIsQuick()
    {
        // Select all over 2,000 folders of 10 files each: every check against every folder
        // was 20k × 2k string comparisons, which hung the Compare window.
        var nodes = new List<Node>();
        for (var f = 0; f < 2_000; f++)
        {
            nodes.Add(new($"root/folder {f:0000}", true));
            for (var i = 0; i < 10; i++)
            {
                nodes.Add(new($"root/folder {f:0000}/file {i}.txt", false));
            }
        }

        var watch = Stopwatch.StartNew();
        var topmost = CompareSelection.Topmost(nodes, n => n.Path, n => n.IsDirectory);
        watch.Stop();

        Assert.Equal(2_000, topmost.Count);
        Assert.All(topmost, n => Assert.True(n.IsDirectory));
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(250), $"took {watch.ElapsedMilliseconds} ms");
    }
}
