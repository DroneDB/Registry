using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

        // Add code to cleanup closed sessions 
        // Add code to close timed out sessions

        public int InitSession(string fileName, int chunks, long size)
        {

            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("File name is empty");

            if (chunks < 1)
                throw new ArgumentException($"Chunks count cannot be lower than 1 ({chunks})");

            if (size < 0)
                throw new ArgumentException($"Size cannot be lower than 0 ({size})");

            _logger.LogInformation($"Starting upload session of '{fileName}' with size {size} in {chunks} chunks");

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

        public void Upload(int sessionId, Stream chunkStream, int index)
        {

            if (chunkStream == null)
                throw new ArgumentException("Upload stream is null");

            if (index < 0)
                throw new ArgumentException($"Index cannot be lower than 0 ({index})");

            if (!chunkStream.CanRead)
                throw new ArgumentException("Cannot read from stream");

            var session = _context.UploadSessions.FirstOrDefault(item => item.Id == sessionId);

            if (session == null)
                throw new ArgumentException($"Cannot find upload session {sessionId}");

            if (session.EndedOn != null)
                throw new ArgumentException("Cannot upload to a closed session");

            if (index >= session.ChunksCount)
                throw new ArgumentException($"Invalid chunk index {index} out of {session.ChunksCount}");

            _logger.LogInformation($"In session {session.Id} uploading chunk {index} out of {session.ChunksCount}");

            var tempFileName = string.Format(TempFileNameFormat, sessionId, session.FileName, index);
            var tempFilePath = Path.Combine(_settings.UploadPath, tempFileName);
            _logger.LogInformation($"Temp file '{tempFilePath}' in '{Path.GetFullPath(tempFilePath)}'");

            _context.Entry(session).Collection(item => item.Chunks).Load();
            var fileChunk = session.Chunks.FirstOrDefault(item => item.Index == index);

            if (fileChunk != null)
            {
                _logger.LogInformation($"Chunk {index} already existing, replacing it");
                fileChunk.Date = DateTime.Now;
                fileChunk.Size = chunkStream.Length;
            }
            else
            {
                _logger.LogInformation($"Chunk {index} does not exist, creating it");
                fileChunk = new FileChunk
                {
                    Date = DateTime.Now,
                    Index = index,
                    Session = session,
                    Size = chunkStream.Length
                };
                _context.FileChunks.Add(fileChunk);
            }
            _context.SaveChanges();

            if (File.Exists(tempFilePath))
            {
                _logger.LogInformation("Temp file exists, removing it");
                File.Delete(tempFilePath);
            }
            else
                _logger.LogInformation("Temp file does not exist");

            // Write temp file
            using var tmpFile = File.OpenWrite(tempFilePath);
            chunkStream.CopyTo(tmpFile);
        }

        public string CloseSession(int sessionId)
        {
            var session = _context.UploadSessions.FirstOrDefault(item => item.Id == sessionId);

            if (session == null)
                throw new ArgumentException($"Cannot find upload session {sessionId}");

            if (session.EndedOn != null)
                throw new ArgumentException("Session already closed");

            // Close session as soon as possible to mitigate concurrency problems
            session.EndedOn = DateTime.Now;
            _context.SaveChanges();

            try
            {

                _context.Entry(session).Collection(item => item.Chunks).Load();

                var chunks = session.Chunks.OrderBy(chunk => chunk.Index).ToArray();

                if (chunks.Length != session.ChunksCount)
                    throw new InvalidOperationException($"Expected {session.ChunksCount} chunks but got only {chunks}");

                _logger.LogInformation($"Closing session {sessionId} of file {session.FileName}");

                // All this only to check if the indexes are a contiguous non-repetitive sequence starting from 0
                if (Enumerable.Range(0, session.ChunksCount).Any(index => chunks.All(chunk => chunk.Index != index)))
                    throw new InvalidOperationException(
                        $"Expected chunks from 0 to {session.ChunksCount - 1} but got {string.Join(" ", chunks.Select(chunk => chunk.Index.ToString()))}");

                var targetFilePath = Path.Combine(_settings.UploadPath, session.FileName);

                _logger.LogInformation($"Destination file path '{targetFilePath}'");

                using var dest = File.OpenWrite(targetFilePath);
                foreach (var chunk in chunks)
                {
                    var tempFileName = string.Format(TempFileNameFormat, sessionId, session.FileName, chunk.Index);
                    var tempFilePath = Path.Combine(_settings.UploadPath, tempFileName);

                    // Copy content of temp file to dest file
                    using var tmp = File.OpenRead(tempFilePath);
                    tmp.CopyTo(dest);
                }

                session.EndedOn = DateTime.Now;
                _context.SaveChanges();

                return targetFilePath;

            }
            finally
            {
                session.EndedOn = null;
                _context.SaveChanges();
            }
        }


    }
}
