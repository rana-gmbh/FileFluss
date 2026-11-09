// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text;

namespace FileFluss.Core.IO;

/// <summary>
/// Small files that must survive a crash or a power cut whole: settings and the account list.
///
/// <para><see cref="File.WriteAllText(string, string)"/> followed by a rename is not enough on
/// its own. The rename is journaled by NTFS, the data is not — after a power cut the new name
/// can point at a file of zeros. So the temporary file is flushed to the disk before it
/// replaces the old one, and the previous version is kept as <c>*.bak</c> for a load to
/// fall back to.</para>
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static string BackupPath(string path) => path + ".bak";

    /// <summary>Writes <paramref name="contents"/> durably, replacing <paramref name="path"/> in one step.</summary>
    public static void WriteAllText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Unique per write: two writers (a second app instance) never share a temporary file.
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.None))
            {
                var bytes = Utf8.GetBytes(contents);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                try
                {
                    // Atomic, and keeps what was there as the backup.
                    File.Replace(temporary, path, BackupPath(path), ignoreMetadataErrors: true);
                    return;
                }
                catch (IOException)
                {
                    // ReplaceFile refuses some volumes and locked backups; a plain overwrite is
                    // still atomic, it just keeps no backup this time.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
