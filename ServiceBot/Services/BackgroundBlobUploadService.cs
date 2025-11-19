using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ServiceBot.Services
{
    /// <summary>
    /// Service for handling background blob uploads without blocking chat flow
    /// </summary>
    public class BackgroundBlobUploadService
    {
        private readonly ILogger<BackgroundBlobUploadService> _logger;

        public BackgroundBlobUploadService(ILogger<BackgroundBlobUploadService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Upload blob in background and return a task that can be awaited later
        /// </summary>
        public Task<BlobUploadResult> UploadBlobAsync(
            Func<Stream, string, CancellationToken, Task<string>> uploadFunc,
            Stream fileStream,
            string mimeType,
            string fileType,
            CancellationToken ct)
        {
            // Start upload task but don't await - returns immediately
            return Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation("Starting background upload for {FileType}...", fileType);
                    var blobUrl = await uploadFunc(fileStream, mimeType, ct);
                    _logger.LogInformation("Completed background upload for {FileType}: {Url}", fileType, blobUrl);
                    
                    return new BlobUploadResult
                    {
                        Success = true,
                        BlobUrl = blobUrl,
                        FileType = fileType
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to upload {FileType}", fileType);
                    return new BlobUploadResult
                    {
                        Success = false,
                        ErrorMessage = ex.Message,
                        FileType = fileType
                    };
                }
            }, ct);
        }

        public class BlobUploadResult
        {
            public bool Success { get; set; }
            public string BlobUrl { get; set; } = string.Empty;
            public string FileType { get; set; } = string.Empty;
            public string ErrorMessage { get; set; } = string.Empty;
        }
    }
}
