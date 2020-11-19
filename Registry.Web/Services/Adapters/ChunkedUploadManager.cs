using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters
{
    public class ChunkedUploadManager : IChunkedUploadManager
    {
        private readonly RegistryContext _context;
        private readonly AppSettings _settings;
        private readonly ILogger<ChunkedUploadManager> _logger;

        private const string TempFileNameFormat = "{0}-{1}.{2}.tmp";

        public ChunkedUploadManager(RegistryContext context, IOptions<AppSettings> settings,
            ILogger<ChunkedUploadManager> logger)
        {
            _context = context;
            _settings = settings.Value;
            _logger = logger;

        }

        public int InitSession(string fileName, int chunks, long size)
        {

            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("File name is empty");

            if (chunks < 1)
                throw new ArgumentException($"Chunks count cannot be lower than 1 ({chunks})");

            if (size <= 0)
                throw new ArgumentException($"Size cannot be lower than 0 ({size})");

            _logger.LogDebug($"Starting upload session of '{fileName}' with size {size} in {chunks} chunks");

            var session = new UploadSession
            {
                ChunksCount = chunks,
                StartedOn = DateTime.Now,
                TotalSize = size,
                FileName = fileName
            };

            _context.UploadSessions.Add(session);
            _context.SaveChanges();

            return session.Id;

        }

        public async Task Upload(int sessionId, Stream chunkStream, int index)
        {

            if (chunkStream == null)
                throw new ArgumentException("Upload stream is null");

            if (index < 0)
                throw new ArgumentException($"Index cannot be lower than 0 ({index})");

            if (!chunkStream.CanRead)
                throw new ArgumentException("Cannot read from stream");

            //// Safety net
            //using var mutex = new Mutex(false, $"ChunkedUploadSession-Upload-{sessionId}");
            //if (!mutex.WaitOne(TimeSpan.FromMinutes(1)))
            //    throw new InvalidOperationException($"Multiple call overlap of Upload with id {sessionId} on index {index}");

            //try
            //{

            var session = _context.UploadSessions.FirstOrDefault(item => item.Id == sessionId);

            if (session == null)
                throw new ArgumentException($"Cannot find upload session {sessionId}");

            if (session.EndedOn != null)
                throw new ArgumentException("Cannot upload to a closed session");

            if (index >= session.ChunksCount)
                throw new ArgumentException($"Invalid chunk index {index}, range [0, {session.ChunksCount - 1}]");

            _logger.LogDebug($"In session {session.Id} uploading chunk {index} out of {session.ChunksCount}");

            var tempFileName = string.Format(TempFileNameFormat, sessionId, session.FileName, index);
            var tempFilePath = Path.Combine(_settings.UploadPath, tempFileName);
            _logger.LogDebug($"Temp file '{tempFilePath}' in '{Path.GetFullPath(tempFilePath)}'");

            _context.Entry(session).Collection(item => item.Chunks).Load();
            var fileChunk = session.Chunks.FirstOrDefault(item => item.Index == index);

            if (fileChunk != null)
            {
                _logger.LogDebug($"Chunk {index} already existing, replacing it");
                fileChunk.Date = DateTime.Now;
                fileChunk.Size = chunkStream.Length;
            }
            else
            {
                _logger.LogDebug($"Chunk {index} does not exist, creating it");
                fileChunk = new FileChunk
                {
                    Date = DateTime.Now,
                    Index = index,
                    Session = session,
                    Size = chunkStream.Length
                };
                await _context.FileChunks.AddAsync(fileChunk);
            }
            await _context.SaveChangesAsync();

            if (File.Exists(tempFilePath))
            {
                _logger.LogDebug("Temp file exists, removing it");
                File.Delete(tempFilePath);
            }
            else
                _logger.LogDebug("Temp file does not exist");

            // Write temp file
            await using var tmpFile = File.OpenWrite(tempFilePath);

            await chunkStream.CopyToAsync(tmpFile);
        }

        public string CloseSession(int sessionId, bool performCleanup = true)
        {
            // Safety net
            using var mutex = new Mutex(false, $"ChunkedUploadSession-Close-{sessionId}");
            if (!mutex.WaitOne(TimeSpan.FromMinutes(1)))
                throw new InvalidOperationException($"Multiple call overlap of CloseSession with id {sessionId}");

            try
            {

                var session = _context.UploadSessions.FirstOrDefault(item => item.Id == sessionId);

                if (session == null)
                    throw new ArgumentException($"Cannot find upload session {sessionId}");

                if (session.EndedOn != null)
                    throw new ArgumentException("Session already closed");

                _context.Entry(session).Collection(item => item.Chunks).Load();

                var chunks = session.Chunks.OrderBy(chunk => chunk.Index).ToArray();

                if (chunks.Length != session.ChunksCount)
                    throw new InvalidOperationException($"Expected {session.ChunksCount} chunks but got only {chunks}");

                _logger.LogDebug($"Closing session {sessionId} of file {session.FileName}");

                // All this only to check if the indexes are a contiguous non-repetitive sequence starting from 0
                if (Enumerable.Range(0, session.ChunksCount).Any(index => chunks.All(chunk => chunk.Index != index)))
                    throw new InvalidOperationException(
                        $"Expected chunks from 0 to {session.ChunksCount - 1} but got {string.Join(" ", chunks.Select(chunk => chunk.Index.ToString()))}");

                var targetFilePath = Path.Combine(_settings.UploadPath, session.FileName);

                _logger.LogDebug($"Destination file path '{targetFilePath}'");

                using (var dest = File.OpenWrite(targetFilePath))
                {
                    foreach (var chunk in chunks)
                    {
                        var tempFileName = string.Format(TempFileNameFormat, sessionId, session.FileName, chunk.Index);
                        var tempFilePath = Path.Combine(_settings.UploadPath, tempFileName);

                        // Copy content of temp file to dest file
                        using var tmp = File.OpenRead(tempFilePath);
                        tmp.CopyTo(dest);
                    }
                }

                var info = new FileInfo(targetFilePath);
                if (info.Length != session.TotalSize)
                    throw new InvalidOperationException(
                        $"Chunks merge failed: expected file size {session.TotalSize} but got {info.Length}");

                session.EndedOn = DateTime.Now;
                _context.SaveChanges();

                if (performCleanup)
                    CleanupSession(sessionId);

                return targetFilePath;
            }
            finally
            {
                mutex.ReleaseMutex();
            }

        }

        public void RemoveTimedoutSessions()
        {
            var now = DateTime.Now;

            // The expired sessions are:
            // 1) Sessions without chunks started a long time ago
            // 2) Sessions with chunks where their newest chunk is expired
            var sessions = _context.UploadSessions
                .Include(session => session.Chunks)
                .Where(session =>
                    (session.Chunks.Count == 0 && session.StartedOn + _settings.ChunkedUploadSessionTimeout > now) ||
                    (session.Chunks.Count > 0 && session.Chunks.OrderByDescending(chunk => chunk.Date).First().Date + _settings.ChunkedUploadSessionTimeout > now))
                .ToArray();

            _logger.LogDebug($"Found {sessions.Length} timed out sessions");

            foreach (var session in sessions)
            {
                _logger.LogDebug($"Removing session {session.Id} of '{session.FileName}' started on {session.StartedOn}");
                _context.UploadSessions.Remove(session);
            }

            _context.SaveChanges();

        }

        public void RemoveClosedSessions()
        {
            var sessions = _context.UploadSessions.Where(item => item.EndedOn != null).ToArray();

            _logger.LogDebug($"Found {sessions.Length} closed sessions");

            foreach (var session in sessions)
            {
                _logger.LogDebug($"Removing session {session.Id} of '{session.FileName}' started on {session.StartedOn}");
                _context.UploadSessions.Remove(session);
            }

            _context.SaveChanges();
        }

        public void CleanupSession(int sessionId)
        {
            var session = _context.UploadSessions.FirstOrDefault(item => item.Id == sessionId);

            if (session == null)
                throw new ArgumentException($"Cannot find upload session {sessionId}");

            _logger.LogDebug($"Cleaning up session {sessionId}");

            var chunks = session.Chunks.ToArray();

            foreach (var chunk in chunks)
            {
                var tempFileName = string.Format(TempFileNameFormat, sessionId, session.FileName, chunk.Index);
                var tempFilePath = Path.Combine(_settings.UploadPath, tempFileName);

                _logger.LogDebug($"Removing temp file '{Path.GetFullPath(tempFilePath)}'");

                File.Delete(tempFilePath);

            }

        }
    }

}
