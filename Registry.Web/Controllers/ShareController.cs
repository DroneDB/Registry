using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web.Controllers
{
    [Authorize]
    [ApiController]
    [Route("share")]
    public class ShareController : ControllerBaseEx
    {
        private readonly IShareManager _shareManager;
        private readonly ILogger<ShareController> _logger;

        // TODO: Move to config
        private const string TempUploadFolderName = "uploads";
        private const string UploadFolderName = "uploads";
        private readonly TimeSpan DeleteDelay = new TimeSpan(0, 10, 0);


        public ShareController(IShareManager shareManager, ILogger<ShareController> logger)
        {
            _shareManager = shareManager;
            _logger = logger;
        }

        [HttpPost("init")]
        public async Task<IActionResult> Init([FromForm] ShareInitDto parameters)
        {
            try
            {
                _logger.LogDebug($"Share controller Init('{parameters}')");

                var initRes = await _shareManager.Initialize(parameters);

                return Ok(initRes);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Share controller Init('{parameters}')");

                return ExceptionResult(ex);
            }
        }

        [HttpPost("upload/{token}")]
        public async Task<IActionResult> Upload(string token, [FromForm] string path, IFormFile file)
        {
            try
            {

                _logger.LogDebug($"Share controller Upload('{token}', '{path}', '{file?.FileName}')");

                if (file == null)
                    return BadRequest(new ErrorResponse("No file uploaded"));

                await using var stream = file.OpenReadStream();
                var res = await _shareManager.Upload(token, path, stream);
                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Share controller Upload('{token}', '{path}', '{file?.FileName}')");

                return ExceptionResult(ex);
            }
        }

        [HttpPost("uploadchunk/{token}")]
        public async Task<IActionResult> UploadChunk(string token, [FromForm] string path, IFormFile file, [FromForm] int index, [FromForm] int totalCount)
        {
            if (index > totalCount - 1 || index < 0 || totalCount < 1)
                return BadRequest("index out of range");

            if (file == null)
                return BadRequest(nameof(file) + " is null");

            var tempPath = Path.Combine(Path.GetTempPath(), TempUploadFolderName);

            if (!Directory.Exists(tempPath))
                Directory.CreateDirectory(tempPath);
            else
                RemoveTempFilesAfterDelay(tempPath);

            try
            {
                var tempFilePath = Path.Combine(tempPath, $"{file.FileName}.{index}.tmp");

                Debug.WriteLine("Temp file: " + tempFilePath);

                // Overwrite existing chunk
                if (System.IO.File.Exists(tempFilePath))
                    System.IO.File.Delete(tempFilePath);

                // Overwrite existing chunk signal
                if (System.IO.File.Exists(tempFilePath + "-OK"))
                    System.IO.File.Delete(tempFilePath + "-OK");

                using (var stream = new FileStream(tempFilePath, FileMode.CreateNew, FileAccess.Write))
                {
                    file.CopyTo(stream);
                }

                // Write signal file
                System.IO.File.WriteAllText(tempFilePath + "-OK", string.Empty);

                Debug.WriteLine("Temp file created, trying to merge chunks ");

                // Verify that all chunks were uploaded
                var chunksPaths = Enumerable.Range(0, totalCount).Select(item => Path.Combine(tempPath, $"{file.FileName}.{item}.tmp")).ToArray();

                if (chunksPaths.Any(chunk => !System.IO.File.Exists(chunk + "-OK")))
                {
                    Debug.WriteLine("Not enough chunks");
                    return Ok();
                }

                MergeChunks(file.FileName, chunksPaths);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }

            return Ok();
        }

        private static void MergeChunks(string fileName, string[] chunksPaths)
        {

            var targetFile = Path.Combine(UploadFolderName, fileName);

            Debug.WriteLine("Target file: " + targetFile);

            if (System.IO.File.Exists(targetFile))
            {
                Debug.WriteLine("Preventing race contition");
                return;
            }

            try
            {
                // Merge chunks
                using var writer = System.IO.File.OpenWrite(targetFile);
                foreach (var chunk in chunksPaths)
                {
                    Debug.WriteLine("Merging chunk: " + chunk);

                    using var reader = System.IO.File.OpenRead(chunk);
                    reader.CopyTo(writer);
                }

                foreach (var chunk in chunksPaths)
                {
                    Debug.WriteLine("Deleting chunk: " + chunk);
                    System.IO.File.Delete(chunk);
                    System.IO.File.Delete(chunk + "-OK");
                }

            }
            catch (IOException ex)
            {
                Debug.WriteLine("Preventing race contition: " + ex.Message);
            }
        }

        private void RemoveTempFilesAfterDelay(string path)
        {
            var dir = new DirectoryInfo(path);

            if (!dir.Exists) return;

            foreach (var file in dir.GetFiles("*.tmp").Where(f => f.LastWriteTimeUtc.Add(DeleteDelay) < DateTime.UtcNow))
                file.Delete();

            foreach (var file in dir.GetFiles("*.tmp-OK").Where(f => f.LastWriteTimeUtc.Add(DeleteDelay) < DateTime.UtcNow))
                file.Delete();
        }

        [HttpPost("commit/{token}")]
        public async Task<IActionResult> Commit(string token)
        {
            try
            {

                _logger.LogDebug($"Share controller Commit('{token}')");

                var res = await _shareManager.Commit(token);

                return Ok(res);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Share controller Commit('{token}')");

                return ExceptionResult(ex);
            }
        }

    }
}
