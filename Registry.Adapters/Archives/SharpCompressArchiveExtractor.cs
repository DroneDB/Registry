#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Registry.Ports.Archives;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace Registry.Adapters.Archives;

/// <summary>
/// <see cref="IArchiveExtractor"/> implementation backed by SharpCompress. Supports
/// zip, rar, 7z, tar and tar.(gz|bz2|xz) as well as single-file gz/bz2/xz. Uses the
/// random-access Archive API (<see cref="ArchiveFactory"/>) so the entry count and
/// total uncompressed size can be reported up-front (quota / decompression-bomb guard)
/// and individual entry streams can be opened on demand.
/// <para>
/// Compressed tarballs (<c>.tar.gz</c>, <c>.tgz</c>, ...) are seen by the Archive API
/// as a single-entry compression container wrapping the <c>.tar</c>. For those the
/// inner tar is transparently decompressed to a temporary file (lazily, only when the
/// entries are actually enumerated) and reopened for random access.
/// </para>
/// </summary>
public sealed class SharpCompressArchiveExtractor : IArchiveExtractor
{
    // Order matters: longer compound extensions must be tested before their suffixes.
    private static readonly string[] SupportedExtensions =
    {
        ".tar.gz", ".tar.bz2", ".tar.xz",
        ".tgz", ".tbz2", ".txz",
        ".zip", ".rar", ".7z", ".tar",
        ".gz", ".bz2", ".xz"
    };

    private static readonly string[] CompressedTarExtensions =
    {
        ".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tar.xz", ".txz"
    };

    // Single-stream formats that frequently carry no internal entry name.
    private static readonly string[] SingleFileExtensions = { ".gz", ".bz2", ".xz" };

    public bool IsSupported(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var lower = fileName.Trim().ToLowerInvariant();
        return SupportedExtensions.Any(ext => lower.EndsWith(ext, StringComparison.Ordinal));
    }

    public IArchiveReadSession Open(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            throw new ArgumentException("Archive path cannot be empty.", nameof(archivePath));
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Archive file not found.", archivePath);

        // Compressed tarballs are handled with the forward-only Reader API, which
        // transparently decompresses the outer layer and exposes the tar entries.
        if (IsCompressedTar(archivePath))
            return new SharpCompressArchiveReadSession(null, archivePath, true);

        // All other formats support random access via the Archive API. The file-path
        // preset owns and closes the underlying FileStream on Dispose.
        var archive = ArchiveFactory.OpenArchive(archivePath, ReaderOptions.ForFilePath);
        return new SharpCompressArchiveReadSession(archive, archivePath, false);
    }

    private static bool IsCompressedTar(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        return CompressedTarExtensions.Any(ext => lower.EndsWith(ext, StringComparison.Ordinal));
    }

    private static string DeriveSingleFileName(string archivePath)
    {
        var name = Path.GetFileName(archivePath);
        foreach (var ext in SingleFileExtensions)
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return name[..^ext.Length];
        }
        return Path.GetFileNameWithoutExtension(name);
    }

    private sealed class SharpCompressArchiveReadSession : IArchiveReadSession
    {
        private readonly string _archivePath;
        private readonly bool _isCompressedTar;
        private readonly IArchive? _archive;
        private List<IArchiveEntry>? _entries;

        private int? _fileEntryCount;
        private long? _total;
        private bool _counted;

        public SharpCompressArchiveReadSession(IArchive? archive, string archivePath, bool isCompressedTar)
        {
            _archive = archive;
            _archivePath = archivePath;
            _isCompressedTar = isCompressedTar;

            if (!_isCompressedTar)
            {
                _entries = _archive!.Entries.Where(e => e != null).ToList();
                _fileEntryCount = _entries.Count(e => !e.IsDirectory);

                long total = _archive.TotalUncompressedSize;
                if (total <= 0)
                {
                    total = 0;
                    foreach (var e in _entries)
                        if (!e.IsDirectory && e.Size > 0) total += e.Size;
                }

                _total = total > 0 ? total : null;
                _counted = true;
            }
        }

        public int FileEntryCount
        {
            get { EnsureCounted(); return _fileEntryCount ?? 0; }
        }

        public long? TotalUncompressedBytes
        {
            get { EnsureCounted(); return _total; }
        }

        public IEnumerable<ArchiveEntry> Entries()
            => _isCompressedTar ? CompressedTarEntries() : RandomAccessEntries();

        // Forward-only count + size pass for compressed tarballs (the Reader API
        // transparently decompresses the outer gz/bz2/xz layer).
        private void EnsureCounted()
        {
            if (_counted) return;
            _counted = true;

            var count = 0;
            long total = 0;
            using var reader = ReaderFactory.OpenReader(_archivePath, ReaderOptions.ForFilePath);
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory) continue;
                count++;
                if (reader.Entry.Size > 0) total += reader.Entry.Size;
            }

            _fileEntryCount = count;
            _total = total > 0 ? total : null;
        }

        private IEnumerable<ArchiveEntry> RandomAccessEntries()
        {
            var emptyKeyIndex = 0;
            foreach (var entry in _entries!)
            {
                var captured = entry;
                var key = NormalizeKey(entry.Key, entry.IsDirectory, ref emptyKeyIndex);
                yield return new ArchiveEntry(
                    Key: key,
                    IsDirectory: entry.IsDirectory,
                    Size: entry.Size,
                    LastModified: entry.LastModifiedTime)
                {
                    OpenStream = () => captured.OpenEntryStream()
                };
            }
        }

        // Forward-only iteration for compressed tarballs. The consumer must fully read
        // each entry's stream before the enumeration advances to the next entry.
        private IEnumerable<ArchiveEntry> CompressedTarEntries()
        {
            var emptyKeyIndex = 0;
            using var reader = ReaderFactory.OpenReader(_archivePath, ReaderOptions.ForFilePath);
            while (reader.MoveToNextEntry())
            {
                var entry = reader.Entry;
                var key = NormalizeKey(entry.Key, entry.IsDirectory, ref emptyKeyIndex);
                yield return new ArchiveEntry(
                    Key: key,
                    IsDirectory: entry.IsDirectory,
                    Size: entry.Size,
                    LastModified: entry.LastModifiedTime)
                {
                    OpenStream = () => reader.OpenEntryStream()
                };
            }
        }

        private string NormalizeKey(string? key, bool isDirectory, ref int emptyKeyIndex)
        {
            if (!string.IsNullOrWhiteSpace(key))
                return key!.Replace('\\', '/').TrimStart('/');

            if (isDirectory)
                return string.Empty; // directories are skipped by the consumer

            var derived = DeriveSingleFileName(_archivePath);
            if (emptyKeyIndex > 0)
            {
                var stem = Path.GetFileNameWithoutExtension(derived);
                var ext = Path.GetExtension(derived);
                derived = $"{stem}_{emptyKeyIndex}{ext}";
            }
            emptyKeyIndex++;
            return derived;
        }

        public void Dispose() => _archive?.Dispose();
    }
}
