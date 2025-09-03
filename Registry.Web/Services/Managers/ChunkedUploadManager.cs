using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Chunked upload support implementation
/// to be integrated into ShareManager.cs
/// </summary>
public class ChunkedUploadManager
{
    private readonly ILogger _logger;
    private readonly string _tempDirectory;

    // In-memory cache of ongoing chunks for each file
    // Key: fileId, Value: dictionary with ChunkIndex and status (true = uploaded)
    private readonly Dictionary<string, Dictionary<int, bool>> _chunkTracker = new();

    // Cache of information about files being uploaded
    private readonly Dictionary<string, ChunkUploadDto> _fileInfoCache = new();

    // Semaphore to ensure thread-safe access to the cache
    private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);

    public ChunkedUploadManager(ILogger logger, string tempDirectory)
    {
        _logger = logger;
        _tempDirectory = tempDirectory;

        // Ensure the temporary directory exists
        Directory.CreateDirectory(_tempDirectory);
    }

    /// <summary>
    /// Handles the upload of a single chunk
    /// </summary>
    public async Task<ChunkUploadResultDto> UploadChunk(ChunkUploadDto chunkInfo, Stream chunkStream)
    {
        // Assicurati che i dati del chunk siano validi
        if (chunkInfo.ChunkIndex < 0 || chunkInfo.ChunkIndex >= chunkInfo.TotalChunks)
            throw new ArgumentException($"Invalid chunk index: {chunkInfo.ChunkIndex}");

        if (chunkInfo.TotalFileSize <= 0)
            throw new ArgumentException("Invalid file size");

        if (string.IsNullOrEmpty(chunkInfo.FileId))
            throw new ArgumentException("FileId is required");

        // Create the specific directory for this file if it doesn't exist
        var fileChunkDir = Path.Combine(_tempDirectory, chunkInfo.FileId);
        Directory.CreateDirectory(fileChunkDir);

        // Path of the file for this chunk
        var chunkPath = Path.Combine(fileChunkDir, $"chunk_{chunkInfo.ChunkIndex}.bin");

        // Verify the MD5 hash of the chunk (if provided)
        if (!string.IsNullOrEmpty(chunkInfo.ChunkMd5))
        {
            var calculatedMd5 = await CalculateMd5Async(chunkStream);
            if (!string.Equals(calculatedMd5, chunkInfo.ChunkMd5, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Chunk MD5 mismatch. Expected: {chunkInfo.ChunkMd5}, Got: {calculatedMd5}");
            }

            // Reset the stream position to the beginning after calculating the hash
            chunkStream.Position = 0;
        }

        // Save the chunk to disk
        using (var fileStream = new FileStream(chunkPath, FileMode.Create, FileAccess.Write, FileShare.None,
                                               bufferSize: 4096, useAsync: true))
        {
            await chunkStream.CopyToAsync(fileStream);
        }

        // Update tracking information
        await _cacheLock.WaitAsync();
        try
        {
            // Update file information
            _fileInfoCache[chunkInfo.FileId] = chunkInfo;

            // Initialize chunk tracking if necessary
            if (!_chunkTracker.TryGetValue(chunkInfo.FileId, out var chunkStatus))
            {
                chunkStatus = new Dictionary<int, bool>();
                _chunkTracker[chunkInfo.FileId] = chunkStatus;
            }

            // Mark this chunk as completed
            chunkStatus[chunkInfo.ChunkIndex] = true;

            // Check if all chunks have been uploaded
            var isComplete = chunkStatus.Count == chunkInfo.TotalChunks &&
                             chunkStatus.All(x => x.Value);

            return new ChunkUploadResultDto
            {
                FileId = chunkInfo.FileId,
                ReceivedChunk = chunkInfo.ChunkIndex,
                Success = true,
                IsComplete = isComplete
            };
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Reassembles all chunks of a file and completes the upload
    /// </summary>
    public async Task<string> FinalizeChunkedUpload(string fileId)
    {
        await _cacheLock.WaitAsync();
        ChunkUploadDto fileInfo;
        Dictionary<int, bool> chunkStatus;

        try
        {
            // Verify that all necessary information is available
            if (!_fileInfoCache.TryGetValue(fileId, out fileInfo))
                throw new ArgumentException($"No information found for file ID: {fileId}");

            if (!_chunkTracker.TryGetValue(fileId, out chunkStatus))
                throw new ArgumentException($"No chunk tracking information for file ID: {fileId}");

            // Verify that all chunks have been uploaded successfully
            if (chunkStatus.Count != fileInfo.TotalChunks || !chunkStatus.All(x => x.Value))
                throw new InvalidOperationException("Not all chunks have been uploaded successfully");
        }
        finally
        {
            _cacheLock.Release();
        }

        // Directory containing the chunks
        var fileChunkDir = Path.Combine(_tempDirectory, fileId);

        // Final path of the temporary file
        var outputFilePath = Path.Combine(_tempDirectory, $"{fileId}_complete");

        // Reassemble the file from chunks
        using (var outputStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None,
                                               bufferSize: 81920, useAsync: true))
        {
            for (int i = 0; i < fileInfo.TotalChunks; i++)
            {
                var chunkPath = Path.Combine(fileChunkDir, $"chunk_{i}.bin");

                using (var chunkStream = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                     bufferSize: 81920, useAsync: true))
                {
                    await chunkStream.CopyToAsync(outputStream);
                }
            }
        }

        // Calculate the MD5 hash of the complete file
        var fileHash = await CalculateFileMd5Async(outputFilePath);

        // Remove tracking data
        await _cacheLock.WaitAsync();
        try
        {
            _fileInfoCache.Remove(fileId);
            _chunkTracker.Remove(fileId);
        }
        finally
        {
            _cacheLock.Release();
        }

        return outputFilePath;
    }

    /// <summary>
    /// Cleans up temporary files for a given fileId
    /// </summary>
    public void CleanupTempFiles(string fileId)
    {
        var fileChunkDir = Path.Combine(_tempDirectory, fileId);
        var outputFilePath = Path.Combine(_tempDirectory, $"{fileId}_complete");

        try
        {
            if (Directory.Exists(fileChunkDir))
                Directory.Delete(fileChunkDir, true);

            if (File.Exists(outputFilePath))
                File.Delete(outputFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error cleaning up temporary files for fileId {FileId}", fileId);
        }
    }

    /// <summary>
    /// Calculates the MD5 hash of a stream
    /// </summary>
    private async Task<string> CalculateMd5Async(Stream stream)
    {
        using (var md5 = MD5.Create())
        {
            var hash = await md5.ComputeHashAsync(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }

    /// <summary>
    /// Calculates the MD5 hash of a file
    /// </summary>
    private async Task<string> CalculateFileMd5Async(string filePath)
    {
        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                         bufferSize: 81920, useAsync: true))
        {
            using (var md5 = MD5.Create())
            {
                var hash = await md5.ComputeHashAsync(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
