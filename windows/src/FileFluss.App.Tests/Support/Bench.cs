// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using System.IO;
using Xunit.Abstractions;

namespace FileFluss.App.Tests.Support;

/// <summary>
/// A timing benchmark: skipped unless <c>FILEFLUSS_BENCH=1</c>, so the normal test run stays
/// fast and machine-independent. Run with
/// <c>$env:FILEFLUSS_BENCH=1; dotnet test windows/src/FileFluss.App.Tests --filter Category=Benchmark --logger "console;verbosity=detailed"</c>.
/// </summary>
public sealed class BenchFactAttribute : FactAttribute
{
    public BenchFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FILEFLUSS_BENCH") != "1")
        {
            Skip = "Benchmark: set FILEFLUSS_BENCH=1 to run.";
        }
    }
}

public static class Bench
{
    /// <summary>Runs <paramref name="action"/> <paramref name="runs"/> times; reports the first (cold) run and the median.</summary>
    public static double Measure(ITestOutputHelper output, string name, Action action, int runs = 5)
    {
        var times = new List<double>();
        for (var i = 0; i < runs; i++)
        {
            var watch = Stopwatch.StartNew();
            action();
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        var first = times[0];
        var median = times.Order().ElementAt(times.Count / 2);
        output.WriteLine($"{name,-58} first {first,9:F2} ms   median {median,9:F2} ms");
        return median;
    }

    public static double Once(ITestOutputHelper output, string name, Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        var elapsed = watch.Elapsed.TotalMilliseconds;
        output.WriteLine($"{name,-58} {elapsed,9:F2} ms");
        return elapsed;
    }

    /// <summary>Runs <paramref name="body"/> on a fresh STA thread, as the WPF UI thread is.</summary>
    public static void OnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new AggregateException(failure);
        }
    }

    /// <summary>
    /// A folder with <paramref name="count"/> empty files (and a few hundred subfolders) with
    /// realistic names, made once and reused across runs.
    /// </summary>
    public static string LargeFolder(int count)
    {
        var root = Path.Combine(Path.GetTempPath(), $"FileFluss-bench-{count}");
        var marker = Path.Combine(root, ".complete");
        if (File.Exists(marker))
        {
            return root;
        }

        Directory.CreateDirectory(root);
        var extensions = new[] { "jpg", "png", "pdf", "docx", "txt", "mp4", "zip", "xlsx", "cs", "json", "heic", "mp3" };
        var stems = new[] { "IMG_", "Document ", "file", "Report-2026-", "Scan ", "photo (", "Invoice_", "notes" };
        var random = new Random(42);
        for (var i = 0; i < count; i++)
        {
            var stem = stems[i % stems.Length];
            var name = stem == "photo (" ? $"photo ({i})" : $"{stem}{random.Next(0, count * 2)}";
            var path = Path.Combine(root, $"{name}-{i}.{extensions[i % extensions.Length]}");
            if (i % 100 == 0)
            {
                Directory.CreateDirectory(Path.Combine(root, $"Folder {i / 100}"));
            }

            File.Create(path).Dispose();
        }

        File.Create(marker).Dispose();
        return root;
    }
}
