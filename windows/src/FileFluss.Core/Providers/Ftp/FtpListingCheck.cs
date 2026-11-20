// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Providers.Ftp;

/// <summary>
/// A listing arrived but couldn't be read in full (macOS 1.6 <c>SFTPAPIClient.validateListing</c>
/// with <c>everyLineIsARow</c>, as its FTP client calls it). It has to be an error: a folder that
/// merely looks emptier than it is tells Mirror to delete the rest from the other side.
/// </summary>
internal static class FtpListingCheck
{
    /// <summary>What FluentFTP logs (verbose) before each raw row of a listing, parsed or not.</summary>
    public const string RawLinePrefix = "Listing:  ";

    public static CloudProviderException UnreadableListing()
        => CloudProviderException.CommandFailed(Localization.L("The server sent a folder listing FileFluss couldn't read."));

    /// <summary>
    /// Throws when <paramref name="unparsedLines"/> — rows the parser gave up on — mean the folder
    /// would be reported as emptier than it is. Only files, folders and links count: device rows
    /// put "major, minor" where the size goes and don't parse, but nothing here can copy them.
    /// With nothing parsed at all, any unexplained line means a format nobody understood (an FTP
    /// listing is the listing and nothing else).
    /// </summary>
    public static void Validate(IEnumerable<string> unparsedLines, int itemCount)
    {
        var unexplainedLine = false;
        foreach (var raw in unparsedLines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var isSelfOrParent = line.EndsWith(" .", StringComparison.Ordinal) || line.EndsWith(" ..", StringComparison.Ordinal) ||
                                 line.EndsWith("/.", StringComparison.Ordinal) || line.EndsWith("/..", StringComparison.Ordinal);
            if (IsListingRow(line) && (line[0] is '-' or 'd' or 'l') && !isSelfOrParent)
            {
                throw UnreadableListing();
            }

            if (!IsListingRow(line) && !line.StartsWith("total ", StringComparison.OrdinalIgnoreCase))
            {
                unexplainedLine = true;
            }
        }

        if (itemCount == 0 && unexplainedLine)
        {
            throw UnreadableListing();
        }
    }

    /// <summary>Cheap structural check for an <c>ls -l</c> row: a type character and nine permission characters.</summary>
    private static bool IsListingRow(string line)
    {
        if (line.Length < 10 || "-dlcbps".IndexOf(line[0]) < 0)
        {
            return false;
        }

        for (var i = 1; i < 10; i++)
        {
            if ("rwxsStT@+.\\*-".IndexOf(line[i]) < 0)
            {
                return false;
            }
        }

        return true;
    }
}
