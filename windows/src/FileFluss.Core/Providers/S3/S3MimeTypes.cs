// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Providers.S3;

/// <summary>
/// Content-Type for an upload, from the extension — the macOS client sends the file's UTType
/// MIME type. Unknown extensions send none, and S3 stores binary/octet-stream as it would on
/// the Mac.
/// </summary>
internal static class S3MimeTypes
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".csv"] = "text/csv",
        [".htm"] = "text/html",
        [".html"] = "text/html",
        [".css"] = "text/css",
        [".js"] = "text/javascript",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".gz"] = "application/gzip",
        [".tar"] = "application/x-tar",
        [".7z"] = "application/x-7z-compressed",
        [".rtf"] = "text/rtf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".heic"] = "image/heic",
        [".svg"] = "image/svg+xml",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".bmp"] = "image/bmp",
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
        [".wav"] = "audio/wav",
        [".flac"] = "audio/flac",
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/x-m4v",
        [".mov"] = "video/quicktime",
        [".mkv"] = "video/x-matroska",
        [".webm"] = "video/webm",
        [".avi"] = "video/x-msvideo",
    };

    public static string? For(string localPath)
        => ByExtension.TryGetValue(Path.GetExtension(localPath), out var type) ? type : null;
}
