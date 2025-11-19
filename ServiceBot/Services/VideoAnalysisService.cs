using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceBot.Services
{
    /// <summary>
    /// Service for analyzing video content (currently disabled - Azure Video Indexer not available)
    /// </summary>
    public class VideoAnalysisService
    {
        private readonly ILogger<VideoAnalysisService> _logger;

        public VideoAnalysisService(ILogger<VideoAnalysisService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Analyze video from blob URL (placeholder - feature disabled)
        /// </summary>
        public async Task<string> AnalyzeVideoAsync(string blobUrl, string caption, CancellationToken ct)
        {
            _logger.LogWarning("Video analysis called but feature is disabled. BlobUrl: {BlobUrl}", blobUrl);
            await Task.CompletedTask;
            return "Could not analyze video - service temporarily unavailable.";
        }
    }
}
