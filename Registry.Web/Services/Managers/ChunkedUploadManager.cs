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
/// Implementazione di supporto per il caricamento a blocchi (chunked upload)
/// da integrare in ShareManager.cs
/// </summary>
public class ChunkedUploadManager
{
    private readonly ILogger _logger;
    private readonly string _tempDirectory;
    
    // Cache in-memory dei chunk in corso per ogni file
    // Chiave: fileId, Valore: dizionario con ChunkIndex e stato (true = caricato)
    private readonly Dictionary<string, Dictionary<int, bool>> _chunkTracker = new();
    
    // Cache delle informazioni sui file in corso di caricamento
    private readonly Dictionary<string, ChunkUploadDto> _fileInfoCache = new();
    
    // Semaforo per garantire accesso thread-safe alla cache
    private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);

    public ChunkedUploadManager(ILogger logger, string tempDirectory)
    {
        _logger = logger;
        _tempDirectory = tempDirectory;
        
        // Assicura che la directory temporanea esista
        Directory.CreateDirectory(_tempDirectory);
    }
    
    /// <summary>
    /// Gestisce il caricamento di un singolo chunk
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
            
        // Crea la directory specifica per questo file se non esiste
        var fileChunkDir = Path.Combine(_tempDirectory, chunkInfo.FileId);
        Directory.CreateDirectory(fileChunkDir);
        
        // Percorso del file per questo chunk
        var chunkPath = Path.Combine(fileChunkDir, $"chunk_{chunkInfo.ChunkIndex}.bin");
        
        // Verifica l'hash MD5 del chunk (se fornito)
        if (!string.IsNullOrEmpty(chunkInfo.ChunkMd5))
        {
            var calculatedMd5 = await CalculateMd5Async(chunkStream);
            if (!string.Equals(calculatedMd5, chunkInfo.ChunkMd5, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Chunk MD5 mismatch. Expected: {chunkInfo.ChunkMd5}, Got: {calculatedMd5}");
            }
            
            // Riporta la posizione dello stream all'inizio dopo aver calcolato l'hash
            chunkStream.Position = 0;
        }
        
        // Salva il chunk su disco
        using (var fileStream = new FileStream(chunkPath, FileMode.Create, FileAccess.Write, FileShare.None, 
                                               bufferSize: 4096, useAsync: true))
        {
            await chunkStream.CopyToAsync(fileStream);
        }
        
        // Aggiorna le informazioni di tracciamento
        await _cacheLock.WaitAsync();
        try
        {
            // Aggiorna le informazioni sul file
            _fileInfoCache[chunkInfo.FileId] = chunkInfo;
            
            // Inizializza il tracciamento chunk se necessario
            if (!_chunkTracker.TryGetValue(chunkInfo.FileId, out var chunkStatus))
            {
                chunkStatus = new Dictionary<int, bool>();
                _chunkTracker[chunkInfo.FileId] = chunkStatus;
            }
            
            // Segna questo chunk come completato
            chunkStatus[chunkInfo.ChunkIndex] = true;
            
            // Verifica se tutti i chunk sono stati caricati
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
    /// Riassembla tutti i chunk di un file e completa il caricamento
    /// </summary>
    public async Task<string> FinalizeChunkedUpload(string fileId)
    {
        await _cacheLock.WaitAsync();
        ChunkUploadDto fileInfo;
        Dictionary<int, bool> chunkStatus;
        
        try
        {
            // Verifica che tutte le informazioni necessarie siano disponibili
            if (!_fileInfoCache.TryGetValue(fileId, out fileInfo))
                throw new ArgumentException($"No information found for file ID: {fileId}");
                
            if (!_chunkTracker.TryGetValue(fileId, out chunkStatus))
                throw new ArgumentException($"No chunk tracking information for file ID: {fileId}");
                
            // Verifica che tutti i chunk siano stati caricati
            if (chunkStatus.Count != fileInfo.TotalChunks || !chunkStatus.All(x => x.Value))
                throw new InvalidOperationException("Not all chunks have been uploaded successfully");
        }
        finally
        {
            _cacheLock.Release();
        }
        
        // Directory contenente i chunk
        var fileChunkDir = Path.Combine(_tempDirectory, fileId);
        
        // Percorso finale del file temporaneo
        var outputFilePath = Path.Combine(_tempDirectory, $"{fileId}_complete");
        
        // Riassembla il file dai chunk
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
        
        // Calcola l'hash MD5 del file completo
        var fileHash = await CalculateFileMd5Async(outputFilePath);
        
        // Rimuovi i dati di tracciamento
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
    /// Pulisce i file temporanei per un dato fileId
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
    /// Calcola l'hash MD5 di uno stream
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
    /// Calcola l'hash MD5 di un file
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
