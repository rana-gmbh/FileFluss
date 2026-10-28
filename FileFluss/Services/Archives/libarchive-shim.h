//
//  libarchive-shim.h
//  Bridging header for the app target.
//
//  macOS ships libarchive as /usr/lib/libarchive.2.dylib and the SDK ships
//  the matching stub (usr/lib/libarchive.2.tbd), so linking it adds no
//  third-party dependency — it is a system library in the same sense as
//  libsqlite3 or libz. What the SDK does *not* ship is archive.h, so the
//  subset of the API FileFluss uses is declared here.
//
//  Only libarchive's documented public entry points appear below, and every
//  one of them takes or returns an opaque pointer or a plain C scalar: the
//  `struct archive` / `struct archive_entry` layouts are never relied upon.
//  That is the half of libarchive's ABI the project promises to keep stable
//  across 3.x, which is why hand-declaring it is safe.
//
//  Read-only: FileFluss lists and extracts through libarchive but writes zips
//  with /usr/bin/zip (see ArchiveService), so none of the archive_write_*
//  family is declared.
//

#ifndef FILEFLUSS_LIBARCHIVE_SHIM_H
#define FILEFLUSS_LIBARCHIVE_SHIM_H

#include <sys/types.h>
#include <stdint.h>
#include <time.h>

struct archive;
struct archive_entry;

// Return codes (archive.h).
#define FF_ARCHIVE_EOF      1
#define FF_ARCHIVE_OK       0
#define FF_ARCHIVE_RETRY  (-10)
#define FF_ARCHIVE_WARN   (-20)
#define FF_ARCHIVE_FAILED (-25)
#define FF_ARCHIVE_FATAL  (-30)

// st_mode type bits, as archive_entry_filetype reports them (archive_entry.h).
#define FF_AE_IFMT   0170000
#define FF_AE_IFREG  0100000
#define FF_AE_IFLNK  0120000
#define FF_AE_IFDIR  0040000

struct archive *archive_read_new(void);
int archive_read_free(struct archive *);
int archive_read_support_filter_all(struct archive *);
int archive_read_support_format_all(struct archive *);
int archive_read_support_format_raw(struct archive *);
int archive_read_open_filename(struct archive *, const char *_filename, size_t _block_size);
int archive_read_next_header(struct archive *, struct archive_entry **);
ssize_t archive_read_data(struct archive *, void *, size_t);
int archive_read_data_skip(struct archive *);
int archive_read_add_passphrase(struct archive *, const char *);

const char *archive_entry_pathname(struct archive_entry *);
const char *archive_entry_pathname_utf8(struct archive_entry *);
const char *archive_entry_symlink(struct archive_entry *);
int64_t archive_entry_size(struct archive_entry *);
int archive_entry_size_is_set(struct archive_entry *);
time_t archive_entry_mtime(struct archive_entry *);
mode_t archive_entry_filetype(struct archive_entry *);
int archive_entry_is_encrypted(struct archive_entry *);

const char *archive_error_string(struct archive *);
int archive_errno(struct archive *);
const char *archive_format_name(struct archive *);

#endif /* FILEFLUSS_LIBARCHIVE_SHIM_H */
