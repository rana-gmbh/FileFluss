// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.IO;

/// <summary>
/// Where a download actually landed. Google Workspace documents have no bytes of their own:
/// the provider exports them and writes <c>Report.docx</c> where <c>Report</c> was asked for
/// (macOS checks the same four extensions in <c>CloudFileManagerViewModel</c>).
/// </summary>
public static class DownloadedFile
{
    public static readonly string[] ExportExtensions = ["docx", "xlsx", "pptx", "pdf"];

    /// <summary>The written file: <paramref name="requestedPath"/> itself, or it with an export extension.</summary>
    public static string Locate(string requestedPath)
    {
        if (File.Exists(requestedPath))
        {
            return requestedPath;
        }

        foreach (var extension in ExportExtensions)
        {
            var candidate = requestedPath + "." + extension;
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return requestedPath;
    }

    /// <summary>The extension an export added, or empty.</summary>
    public static string AddedExtension(string requestedPath, string actualPath)
        => actualPath.Length > requestedPath.Length && actualPath.StartsWith(requestedPath, StringComparison.Ordinal)
            ? actualPath[requestedPath.Length..]
            : string.Empty;
}
