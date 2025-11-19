using Microsoft.SemanticKernel;
using ServiceBot.Models;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceBot.Plugins
{
    /// <summary>
    /// Plugin for processing media attachments (photo, audio, video) in the background
    /// </summary>
    public class MediaProcessingPlugin
    {
        private readonly Kernel _kernel;

        public MediaProcessingPlugin(Kernel kernel)
        {
            _kernel = kernel;
        }

        [KernelFunction]
        [Description("Process a photo attachment and extract sanitation complaint information")]
        public async Task<string> ProcessPhotoAsync(
            [Description("The image as base64 string or blob URL")] string imageData,
            [Description("Whether imageData is base64 (true) or URL (false)")] bool isBase64,
            [Description("Caption or description provided with the photo")] string caption,
            [Description("Recent conversation context")] string conversationContext,
            CancellationToken ct = default)
        {
            // Use vision-enabled analysis (supports both base64 and URL)
            var result = await _kernel.InvokeAsync<string>(
                "AIAnalysisPlugin", "AnalyzeImage",
                new KernelArguments
                {
                    ["imageUrl"] = isBase64 ? $"data:image/jpeg;base64,{imageData}" : imageData,
                    ["caption"] = caption,
                    ["conversationContext"] = conversationContext
                },
                ct);

            return result;
        }

        [KernelFunction]
        [Description("Process an audio/voice message and extract sanitation complaint information")]
        public async Task<string> ProcessAudioAsync(
            [Description("The transcribed text from the audio")] string transcript,
            [Description("Caption or description provided with the audio")] string caption,
            [Description("Recent conversation context")] string conversationContext,
            CancellationToken ct = default)
        {
            var result = await _kernel.InvokeAsync<string>(
                "AIAnalysisPlugin", "AnalyzeAudio",
                new KernelArguments
                {
                    ["transcript"] = transcript,
                    ["caption"] = caption,
                    ["conversationContext"] = conversationContext
                },
                ct);

            return result;
        }

        [KernelFunction]
        [Description("Process a video attachment and extract sanitation complaint information")]
        public async Task<string> ProcessVideoAsync(
            [Description("The video transcript or description")] string videoTranscript,
            [Description("Caption or description provided with the video")] string caption,
            [Description("Recent conversation context")] string conversationContext,
            CancellationToken ct = default)
        {
            var result = await _kernel.InvokeAsync<string>(
                "AIAnalysisPlugin", "AnalyzeVideo",
                new KernelArguments
                {
                    ["videoTranscript"] = videoTranscript,
                    ["caption"] = caption,
                    ["conversationContext"] = conversationContext
                },
                ct);

            return result;
        }
    }
}
