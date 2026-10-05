// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Formatting;

/// <summary>
/// Sizes the way Explorer writes them: binary multiples labelled KB/MB/GB, three significant
/// digits ("6.50 MB", "13.5 MB", "268 bytes"). The macOS app follows Finder's decimal units
/// instead; matching the host's file manager matters more than matching the other platform,
/// because users compare the two side by side.
/// </summary>
public static class ByteFormatter
{
    private static readonly string[] Units = ["KB", "MB", "GB", "TB", "PB", "EB"];

    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return "--";
        }

        if (bytes < 1024)
        {
            return bytes == 1 ? Localization.L("1 byte") : Localization.L("{0} bytes", bytes.ToString("N0", Localization.Culture));
        }

        double value = bytes;
        var unit = -1;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Three significant digits, truncated rather than rounded up, as StrFormatByteSize does:
        // 1023.9 KB must not print as "1024 KB".
        var format = value < 10 ? "0.00" : value < 100 ? "0.0" : "0";
        var factor = value < 10 ? 100 : value < 100 ? 10 : 1;
        var truncated = Math.Floor(value * factor) / factor;
        return truncated.ToString(format, Localization.Culture) + " " + Units[unit];
    }

    /// <summary>"637 KB/s".</summary>
    public static string FormatSpeed(double bytesPerSecond) => Format((long)bytesPerSecond) + "/s";

    /// <summary>
    /// Rounded the way someone reads a countdown: seconds below a minute, whole minutes below
    /// an hour, hours and minutes above. Same rule as the macOS transfer details.
    /// </summary>
    public static string FormatDuration(TimeSpan duration)
    {
        var seconds = duration.TotalSeconds;
        if (seconds < 60)
        {
            return $"{Math.Max(1, (int)Math.Round(seconds))} s";
        }

        if (seconds < 3600)
        {
            return $"{(int)Math.Round(seconds / 60)} min";
        }

        var hours = (int)(seconds / 3600);
        var minutes = (int)((seconds - hours * 3600) / 60);
        return minutes == 0 ? $"{hours} h" : $"{hours} h {minutes} min";
    }
}
