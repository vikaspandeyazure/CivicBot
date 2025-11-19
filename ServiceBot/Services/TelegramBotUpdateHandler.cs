using Azure.AI.OpenAI;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using ServiceBot.Models;
using ServiceBot.Plugins;
using ServiceBot.Agents;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

#pragma warning disable CS8600,CS8603,CS8618

namespace ServiceBot.Services
{
    public class TelegramBotUpdateHandler : IHostedService
    {
        private const string NegativeSummaryPrimary = "Not a civic sanitation complaint.";

        private static bool IsNegativeClassification(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            var norm = s.Trim().TrimEnd('.').ToLowerInvariant();
            return norm is "not a civic sanitation complaint" or "not a garbage complaint";
        }

        private readonly ITelegramBotClient _botClient;
        private readonly ILogger<TelegramBotUpdateHandler> _logger;
        private readonly IConfiguration _configuration;
        private readonly Kernel _kernel;
        private readonly ChatConversationService _chatConvo;
        private readonly ComplaintAgentOrchestrator _agentOrchestrator;
        private readonly BackgroundBlobUploadService _blobUploadService;
        private readonly VideoAnalysisService _videoAnalysisService;

        // Pending ticket session state per chat
        private sealed class PendingTicketSession
        {
            public ComplaintTicket Ticket { get; set; } = default!;
            public string AggregatedSummary { get; set; } = string.Empty;
            public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
            public bool AwaitingConfirmation { get; set; }
            public List<Task<BackgroundBlobUploadService.BlobUploadResult>> PendingUploads { get; set; } = new();
            public void Touch() => LastUpdatedUtc = DateTime.UtcNow;
            public bool HasValidSummary => !string.IsNullOrWhiteSpace(AggregatedSummary) && !IsNegativeClassification(AggregatedSummary);
        }

        private readonly ConcurrentDictionary<long, PendingTicketSession> _sessions = new();
        private TimeSpan SessionExpiry =>
            TimeSpan.FromMinutes(double.TryParse(_configuration["Ticketing:SessionExpiryMinutes"], out var m) && m > 0 ? m : 30);

        public TelegramBotUpdateHandler(
            ITelegramBotClient botClient,
            ILogger<TelegramBotUpdateHandler> logger,
            IConfiguration configuration,
            Kernel kernel,
            ChatConversationService chatConvo,
            ComplaintAgentOrchestrator agentOrchestrator,
            BackgroundBlobUploadService blobUploadService,
            VideoAnalysisService videoAnalysisService)
        {
            _botClient = botClient;
            _logger = logger;
            _configuration = configuration;
            _kernel = kernel;
            _chatConvo = chatConvo;
            _agentOrchestrator = agentOrchestrator;
            _blobUploadService = blobUploadService;
            _videoAnalysisService = videoAnalysisService;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _botClient.StartReceiving(
                updateHandler: HandleUpdateAsync,
                errorHandler: HandleErrorAsync,
                receiverOptions: new ReceiverOptions(),
                cancellationToken: cancellationToken
            );
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping bot service.");
            await Task.CompletedTask;
        }

        private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            if (update.Message is not { } message) return;
            var chatId = message.Chat.Id;

            try
            {
                ExpireSessionIfOld(chatId);

                // Ensure / create session
                var session = _sessions.GetOrAdd(chatId, _ => new PendingTicketSession
                {
                    Ticket = new ComplaintTicket
                    {
                        UserId = message.From.Id,
                        ChatId = chatId,
                        UserFullName = $"{message.From.FirstName} {message.From.LastName}",
                        MobileNumber = message.Contact?.PhoneNumber
                    }
                });

                // SPECIAL HANDLING: Transcribe audio/voice first to check if it's a conversational response
                string audioTranscript = string.Empty;
                if (message.Type == MessageType.Voice || message.Type == MessageType.Audio)
                {
                    var fileId = message.Voice?.FileId ?? message.Audio?.FileId;
                    if (fileId != null)
                    {
                        var file = await _botClient.GetFile(fileId, cancellationToken);
                        var audioStream = new MemoryStream();
                        await _botClient.DownloadFile(file.FilePath, audioStream, cancellationToken);
                        audioStream.Position = 0;
                        
                        audioTranscript = await ConvertAudioToTextAsync(audioStream, cancellationToken);
                        _logger.LogInformation("Audio transcribed: {Transcript}", audioTranscript);
                    }
                }

                // Conversation history (always capture - use transcript for audio)
                if (message.Type == MessageType.Text && !string.IsNullOrWhiteSpace(message.Text))
                {
                    _chatConvo.AddUserMessage(chatId, message.Text, "text");
                }
                else if ((message.Type == MessageType.Voice || message.Type == MessageType.Audio) && !string.IsNullOrWhiteSpace(audioTranscript))
                {
                    _chatConvo.AddUserMessage(chatId, audioTranscript, "audio"); // Use transcript as conversation input
                }
                else if (message.Type == MessageType.Photo)
                {
                    var caption = !string.IsNullOrWhiteSpace(message.Caption) 
                        ? message.Caption 
                        : "User sent a photo for analysis";
                    _chatConvo.AddUserMessage(chatId, caption, "photo");
                }
                else if (message.Type == MessageType.Video)
                {
                    // Video feature disabled - skip conversation logging
                    // var caption = !string.IsNullOrWhiteSpace(message.Caption)
                    //     ? message.Caption
                    //     : "User sent a video for analysis";
                    // _chatConvo.AddUserMessage(chatId, caption, "video");
                }
                else if (message.Type == MessageType.VideoNote)
                {
                    // Video note feature disabled - skip conversation logging
                    // _chatConvo.AddUserMessage(chatId, "User sent a video note for analysis", "video");
                }
                else
                {
                    _chatConvo.AddUserMessage(chatId, $"[User sent {message.Type}]", message.Type.ToString().ToLowerInvariant());
                }

                string newContributionSummary = string.Empty;
                var recentContext = _chatConvo.GetRecentContext(chatId, maxTurns: 2); // Reduced from 3 to avoid token overflow

                // PROCESS INPUT - All media processing happens in parallel with analysis
                if (message.Type == MessageType.Text)
                {
                    _logger.LogInformation("Received Text Message: {Text}", message.Text);
                    
                    // Analyze text using AIAnalysisPlugin
                    newContributionSummary = await _kernel.InvokeAsync<string>(
                        "AIAnalysisPlugin", "AnalyzeText",
                        new KernelArguments
                        {
                            ["text"] = message.Text!,
                            ["conversationContext"] = recentContext
                        },
                        cancellationToken);
                }
                else if (message.Type == MessageType.Photo)
                {
                    await _botClient.SendMessage(chatId, "Got your photo! Analyzing...", cancellationToken: cancellationToken);
                    newContributionSummary = await ProcessPhotoAttachmentAsync(message, session, recentContext, cancellationToken);
                }
                else if (message.Type == MessageType.Voice || message.Type == MessageType.Audio)
                {
                    // Audio: Check if it's conversational (answering a question) or providing new complaint info
                    // Use agent to determine if this is additional info or just a response
                    var isConversationalResponse = await IsAudioConversationalResponseAsync(audioTranscript, recentContext, cancellationToken);
                    
                    if (isConversationalResponse)
                    {
                        // Treat as text message (conversational response)
                        _logger.LogInformation("Audio treated as conversational response: {Transcript}", audioTranscript);
                        newContributionSummary = await _kernel.InvokeAsync<string>(
                            "AIAnalysisPlugin", "AnalyzeText",
                            new KernelArguments
                            {
                                ["text"] = audioTranscript,
                                ["conversationContext"] = recentContext
                            },
                            cancellationToken);
                    }
                    else
                    {
                        // Treat as attachment (new complaint audio evidence)
                        await _botClient.SendMessage(chatId, "Processing your audio evidence...", cancellationToken: cancellationToken);
                        newContributionSummary = await ProcessAudioAttachmentAsync(message, session, recentContext, audioTranscript, cancellationToken);
                    }
                }
                else if (message.Type == MessageType.Video)
                {
                    // Video feature temporarily disabled - Azure Video Indexer service not available
                    await SendMessageAsync(chatId, 
                        "📹 Video features are currently under development.\n\n" +
                        "Please log your ticket using:\n" +
                        "• 📝 Text description\n" +
                        "• 📷 Photo/Image\n" +
                        "• 🎤 Audio message\n\n" +
                        "What sanitation issue can I help you with?", 
                        cancellationToken);
                    return;
                }
                else if (message.Type == MessageType.VideoNote)
                {
                    // Video note feature temporarily disabled - Azure Video Indexer service not available
                    await SendMessageAsync(chatId, 
                        "📹 Video features are currently under development.\n\n" +
                        "Please log your ticket using:\n" +
                        "• 📝 Text description\n" +
                        "• 📷 Photo/Image\n" +
                        "• 🎤 Audio message\n\n" +
                        "What sanitation issue can I help you with?", 
                        cancellationToken);
                    return;
                }
                else
                {
                    await SendMessageAsync(chatId, "I can process text, images, audio, or video related to civic sanitation complaints.", cancellationToken);
                    return;
                }

                // Update session with analysis results
                bool contributionIsNegative = IsNegativeClassification(newContributionSummary);
                if (!contributionIsNegative && !string.IsNullOrWhiteSpace(newContributionSummary))
                {
                    session.AggregatedSummary = MergeSummaries(session.AggregatedSummary, newContributionSummary);
                    session.Ticket.TextSummary = session.AggregatedSummary;
                    session.Touch();
                }
                else if (contributionIsNegative)
                {
                    // Off-topic content detected - send specific guidance based on media type
                    string redirectMessage = message.Type switch
                    {
                        MessageType.Photo => "I see you sent a photo, but it doesn't appear to be related to civic sanitation issues.\n\n" +
                                           "Please upload images showing problems like:\n" +
                                           "• Garbage piles\n" +
                                           "• Blocked drains\n" +
                                           "• Sewage leaks\n" +
                                           "• Pest infestations\n\n" +
                                           "What sanitation issue can I help you with?",
                        MessageType.Video or MessageType.VideoNote => "I see you sent a video, but it doesn't appear to be related to civic sanitation issues.\n\n" +
                                           "Please upload videos showing problems like garbage, blocked drains, or sewage issues.\n\n" +
                                           "What sanitation issue can I help you with?",
                        MessageType.Audio or MessageType.Voice => "I heard your audio, but it doesn't seem to be about civic sanitation issues.\n\n" +
                                           "I help with problems like garbage, blocked drains, and sewage. What can I help you with?",
                        _ => "I help with civic sanitation issues like garbage, blocked drains, sewage problems, and pests.\n\n" +
                             "What sanitation issue can I help you with?"
                    };
                    
                    await SendMessageAsync(chatId, redirectMessage, cancellationToken);
                    return; // Exit early, don't proceed to agent
                }

                // Determine the message to send to agent
                // For media with analysis results, use the summary instead of placeholder text
                string agentInputMessage;
                if (message.Type == MessageType.Photo || message.Type == MessageType.Video || message.Type == MessageType.VideoNote)
                {
                    // If we got a valid summary, use it; otherwise use type indicator
                    agentInputMessage = !string.IsNullOrWhiteSpace(newContributionSummary) && !contributionIsNegative
                        ? newContributionSummary
                        : $"[{message.Type}]";
                }
                else if (message.Type == MessageType.Voice || message.Type == MessageType.Audio)
                {
                    // Use transcript for audio
                    agentInputMessage = !string.IsNullOrWhiteSpace(audioTranscript) 
                        ? audioTranscript 
                        : $"[{message.Type}]";
                }
                else
                {
                    // Use text or message type
                    agentInputMessage = message.Text ?? $"[{message.Type}]";
                }

                // Use Agent to decide next action
                var agentResponse = await _agentOrchestrator.ProcessMessageAsync(
                    chatId,
                    agentInputMessage,
                    session.Ticket,
                    session.AwaitingConfirmation,
                    cancellationToken);

                // Update session state based on agent decision
                session.AwaitingConfirmation = agentResponse.AwaitingConfirmation;
                
                // Send response
                await SendMessageAsync(chatId, agentResponse.Message, cancellationToken);

                // Handle ticket creation - reset ticket but keep session/history for follow-up
                if (agentResponse.Action == AgentAction.TicketCreated)
                {
                    _logger.LogInformation("Ticket created for chat {ChatId}. Ticket ID: {TicketId}. Session kept alive for follow-up.", 
                        chatId, agentResponse.TicketId);
                    
                    // Reset ticket for potential new complaint while keeping conversation history
                    session.Ticket = new ComplaintTicket
                    {
                        UserId = message.From.Id,
                        ChatId = chatId,
                        UserFullName = $"{message.From.FirstName} {message.From.LastName}",
                        MobileNumber = message.Contact?.PhoneNumber
                    };
                    session.AggregatedSummary = string.Empty;
                    session.AwaitingConfirmation = false;
                    session.PendingUploads.Clear();
                    
                    // Mark session as "post-ticket" so next message triggers cleanup if it's just acknowledgment
                    session.Touch(); // Reset timer to give user time to say thanks
                }

                // Clear session if agent decided to end (user said "thanks" or "goodbye")
                if (agentResponse.ShouldClearSession)
                {
                    _logger.LogInformation("Agent decided to end session for chat {ChatId}. Reason: {Action}", chatId, agentResponse.Action);
                    _sessions.TryRemove(chatId, out _);
                    _chatConvo.ClearHistory(chatId); // Only clear history when conversation actually ends
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling Telegram update.");
                await SendMessageAsync(chatId,
                    "Sorry, something went wrong. Please try again later.",
                    cancellationToken);
            }
        }

        /// <summary>
        /// Determines if audio transcript is a conversational response (answering a question) 
        /// vs. new complaint information (attachment)
        /// </summary>
        private async Task<bool> IsAudioConversationalResponseAsync(string transcript, string conversationContext, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(transcript)) return false;

            // Short responses are likely conversational
            var wordCount = transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (wordCount <= 5)
            {
                return true; // "yes", "that's correct", "Main Street", etc.
            }

            // Use LLM to determine intent
            var prompt = $@"Analyze if this audio transcript is:
A) A conversational response answering a question or confirming something
B) New complaint information (describing a sanitation issue)

Recent conversation:
{conversationContext}

Audio transcript: ""{transcript}"")

Examples of conversational responses:
- ""yes, that's correct""
- ""it's on Main Street""
- ""yes, please log the ticket""
- ""no, cancel it""
- ""the location is Park Avenue""

Examples of new complaint info:
- ""there's garbage piling up near the park entrance with flies everywhere""
- ""the drain is completely blocked and water is overflowing onto the street""

Respond with ONLY 'conversational' or 'new_info'.";

            var function = _kernel.CreateFunctionFromPrompt(prompt);
            var result = await function.InvokeAsync(_kernel, new KernelArguments(), ct);
            
            var response = result.ToString().Trim().ToLowerInvariant();
            return response.Contains("conversational");
        }

        private async Task<string> ProcessPhotoAttachmentAsync(
            Message message,
            PendingTicketSession session,
            string conversationContext,
            CancellationToken ct)
        {
            var photoFile = message.Photo?.Last();
            if (photoFile == null) return NegativeSummaryPrimary;

            var file = await _botClient.GetFile(photoFile.FileId, ct);
            var fileStream = new MemoryStream();
            await _botClient.DownloadFile(file.FilePath, fileStream, ct);
            fileStream.Position = 0;

            // Create two streams: one for upload, one for potential local processing
            byte[] imageBytes = fileStream.ToArray();
            var uploadStream = new MemoryStream(imageBytes);
            
            // Start blob upload (runs in background via BackgroundBlobUploadService)
            var uploadTask = _blobUploadService.UploadBlobAsync(
                UploadToBlobAsync,
                uploadStream,
                "image/jpeg",
                "Photo",
                ct);
            session.PendingUploads.Add(uploadTask);

            // Wait for upload to complete (2-3 seconds is acceptable for proper analysis)
            // This avoids token explosion from base64 encoding
            var uploadResult = await uploadTask;
            
            if (!uploadResult.Success)
            {
                _logger.LogError("Photo upload failed: {Error}", uploadResult.ErrorMessage);
                return NegativeSummaryPrimary;
            }

            // Use user's caption if provided, otherwise let AI analyze without bias
            var caption = !string.IsNullOrWhiteSpace(message.Caption) 
                ? message.Caption 
                : string.Empty; // Empty caption = unbiased AI analysis
            
            _logger.LogInformation("Processing photo. BlobUrl: {BlobUrl}, Caption: '{Caption}', ConversationContext: '{Context}'", 
                uploadResult.BlobUrl, caption, conversationContext);
            
            // Add attachment with blob URL
            session.Ticket.Attachments.Add(new ComplaintAttachment
            {
                FileType = "Image",
                BlobUrl = uploadResult.BlobUrl,
                Caption = caption
            });

            // Analyze from blob URL (uses minimal tokens - just the URL!)
            var analysisSummary = await _kernel.InvokeAsync<string>(
                "MediaProcessingPlugin", "ProcessPhoto",
                new KernelArguments
                {
                    ["imageData"] = uploadResult.BlobUrl,
                    ["isBase64"] = false, // Use URL to avoid token explosion
                    ["caption"] = caption,
                    ["conversationContext"] = conversationContext
                },
                ct);

            _logger.LogInformation("Photo analysis result: {AnalysisSummary}", analysisSummary);

            return analysisSummary;
        }

        private async Task<string> ProcessAudioAttachmentAsync(
            Message message,
            PendingTicketSession session,
            string conversationContext,
            string preTranscribedText,
            CancellationToken ct)
        {
            var fileId = message.Voice?.FileId ?? message.Audio?.FileId;
            if (fileId == null) return NegativeSummaryPrimary;

            var file = await _botClient.GetFile(fileId, ct);
            var audioStream = new MemoryStream();
            await _botClient.DownloadFile(file.FilePath, audioStream, ct);
            audioStream.Position = 0;

            // Use pre-transcribed text (already done in HandleUpdateAsync)
            var transcript = preTranscribedText;

            // Start blob upload in background
            var mimeType = message.Voice?.MimeType ?? message.Audio?.MimeType ?? "audio/ogg";
            var uploadTask = _blobUploadService.UploadBlobAsync(
                UploadToBlobAsync,
                audioStream,
                mimeType,
                "Audio",
                ct);
            session.PendingUploads.Add(uploadTask);

            var caption = message.Caption ?? "Audio evidence";
            
            // Proceed with analysis immediately (don't wait for upload)
            var analysisTask = _kernel.InvokeAsync<string>(
                "MediaProcessingPlugin", "ProcessAudio",
                new KernelArguments
                {
                    ["transcript"] = transcript,
                    ["caption"] = caption,
                    ["conversationContext"] = conversationContext
                },
                ct);

            // Wait for upload to complete so we have blob URL
            var uploadResult = await uploadTask;
            
            session.Ticket.Attachments.Add(new ComplaintAttachment
            {
                FileType = "Audio",
                BlobUrl = uploadResult.Success ? uploadResult.BlobUrl : string.Empty,
                Caption = caption,
                Transcript = transcript
            });

            return await analysisTask;
        }

        private async Task<string> ProcessVideoAttachmentAsync(
            Message message,
            PendingTicketSession session,
            string conversationContext,
            CancellationToken ct)
        {
            var video = message.Video;
            if (video == null) return NegativeSummaryPrimary;

            var file = await _botClient.GetFile(video.FileId, ct);
            var videoStream = new MemoryStream();
            await _botClient.DownloadFile(file.FilePath, videoStream, ct);

            // Start blob upload in background
            var mimeType = video.MimeType ?? "video/mp4";
            var uploadTask = _blobUploadService.UploadBlobAsync(
                UploadToBlobAsync,
                videoStream,
                mimeType,
                "Video",
                ct);
            session.PendingUploads.Add(uploadTask);

            // Wait for upload to get blob URL
            var uploadResult = await uploadTask;
            
            if (!uploadResult.Success)
            {
                _logger.LogError("Video upload failed: {Error}", uploadResult.ErrorMessage);
                return NegativeSummaryPrimary;
            }

            var caption = !string.IsNullOrWhiteSpace(message.Caption) 
                ? message.Caption 
                : "Analyze this video for sanitation issues";
            
            // Add attachment with blob URL
            session.Ticket.Attachments.Add(new ComplaintAttachment
            {
                FileType = "Video",
                BlobUrl = uploadResult.BlobUrl,
                Caption = caption
            });

            try
            {
                // Use VideoAnalysisService for REAL video analysis
                var videoSummary = await _videoAnalysisService.AnalyzeVideoAsync(
                    uploadResult.BlobUrl, 
                    caption, 
                    ct);

                // If video analysis fails or returns generic error, use caption-based fallback
                if (videoSummary.Contains("Could not analyze") || 
                    videoSummary.Contains("No summary produced"))
                {
                    _logger.LogWarning("Video analysis failed, using caption-based analysis");
                    
                    // Fallback: Use caption + conversation context for analysis
                    var fallbackAnalysis = await _kernel.InvokeAsync<string>(
                        "AIAnalysisPlugin", "AnalyzeText",
                        new KernelArguments
                        {
                            ["text"] = $"User uploaded a video with caption: {caption}",
                            ["conversationContext"] = conversationContext
                        },
                        ct);
                    
                    return fallbackAnalysis;
                }

                return videoSummary;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Video analysis failed unexpectedly");
                
                // Fallback: Use caption if provided, otherwise reject
                if (!string.IsNullOrWhiteSpace(message.Caption))
                {
                    var fallbackAnalysis = await _kernel.InvokeAsync<string>(
                        "AIAnalysisPlugin", "AnalyzeText",
                        new KernelArguments
                        {
                            ["text"] = $"User uploaded a video describing: {message.Caption}",
                            ["conversationContext"] = conversationContext
                        },
                        ct);
                    
                    return fallbackAnalysis;
                }
                
                return NegativeSummaryPrimary;
            }
        }

        private async Task<string> ProcessVideoNoteAttachmentAsync(
            Message message,
            PendingTicketSession session,
            string conversationContext,
            CancellationToken ct)
        {
            var note = message.VideoNote;
            if (note == null) return NegativeSummaryPrimary;

            var file = await _botClient.GetFile(note.FileId, ct);
            var videoStream = new MemoryStream();
            await _botClient.DownloadFile(file.FilePath, videoStream, ct);

            // Start blob upload in background
            var uploadTask = _blobUploadService.UploadBlobAsync(
                UploadToBlobAsync,
                videoStream,
                "video/mp4",
                "VideoNote",
                ct);
            session.PendingUploads.Add(uploadTask);

            // Wait for upload to get blob URL
            var uploadResult = await uploadTask;
            
            if (!uploadResult.Success)
            {
                _logger.LogError("Video note upload failed: {Error}", uploadResult.ErrorMessage);
                return NegativeSummaryPrimary;
            }

            var caption = "User sent a video note describing a sanitation issue";
            
            // Add attachment with blob URL
            session.Ticket.Attachments.Add(new ComplaintAttachment
            {
                FileType = "VideoNote",
                BlobUrl = uploadResult.BlobUrl,
                Caption = caption
            });

            try
            {
                // Use VideoAnalysisService for REAL video analysis
                var videoSummary = await _videoAnalysisService.AnalyzeVideoAsync(
                    uploadResult.BlobUrl, 
                    caption, 
                    ct);

                // If video analysis fails, try caption-based fallback
                if (videoSummary.Contains("Could not analyze") || 
                    videoSummary.Contains("No summary produced"))
                {
                    _logger.LogWarning("Video note analysis failed, using context-based analysis");
                    
                    // For video notes, use recent conversation context to infer the complaint
                    var fallbackAnalysis = await _kernel.InvokeAsync<string>(
                        "AIAnalysisPlugin", "AnalyzeText",
                        new KernelArguments
                        {
                            ["text"] = "User sent a video note to show the sanitation issue we discussed",
                            ["conversationContext"] = conversationContext
                        },
                        ct);
                    
                    return fallbackAnalysis;
                }

                return videoSummary;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Video note analysis failed unexpectedly");
                
                // Fallback: Use conversation context
                var fallbackAnalysis = await _kernel.InvokeAsync<string>(
                    "AIAnalysisPlugin", "AnalyzeText",
                    new KernelArguments
                    {
                        ["text"] = "User sent a video note showing the sanitation issue",
                        ["conversationContext"] = conversationContext
                    },
                    ct);
                
                return fallbackAnalysis;
            }
        }

        private void ExpireSessionIfOld(long chatId)
        {
            if (_sessions.TryGetValue(chatId, out var session))
            {
                if (DateTime.UtcNow - session.LastUpdatedUtc > SessionExpiry)
                {
                    _logger.LogInformation("Expiring stale session for chat {ChatId}.", chatId);
                    
                    // Notify user before clearing
                    _ = _botClient.SendMessage(chatId,
                        "Your session expired due to inactivity. Feel free to describe a sanitation issue anytime to start fresh!");
                    
                    _sessions.TryRemove(chatId, out _);
                    _chatConvo.ClearHistory(chatId); // Clear conversation history too
                }
            }
        }

        private static string MergeSummaries(string current, string addition)
        {
            if (string.IsNullOrWhiteSpace(addition)) return current;
            if (string.IsNullOrWhiteSpace(current)) return addition.Trim();
            return $"{current.Trim()}; {addition.Trim()}";
        }

        private async Task SendMessageAsync(long chatId, string text, CancellationToken ct)
        {
            _chatConvo.AddAssistantMessage(chatId, text);
            await _botClient.SendMessage(chatId, text, cancellationToken: ct);
        }

        // ---- Audio transcription (FFmpeg -> WAV -> Azure Speech) ----
        private async Task<string> ConvertAudioToTextAsync(Stream audioStream, CancellationToken ct = default)
        {
            using var memory = new MemoryStream();
            await audioStream.CopyToAsync(memory, ct);

            string tempIn = Path.Combine(Path.GetTempPath(), $"voice_{Guid.NewGuid():N}.bin");
            string tempWav = Path.Combine(Path.GetTempPath(), $"voice_{Guid.NewGuid():N}.wav");

            try
            {
                await System.IO.File.WriteAllBytesAsync(tempIn, memory.ToArray(), ct);

                var ffmpegPath = ResolveFfmpegPath();
                using (var proc = new System.Diagnostics.Process())
                {
                    proc.StartInfo.FileName = ffmpegPath;
                    proc.StartInfo.Arguments = $"-y -i \"{tempIn}\" -ar 16000 -ac 1 -f wav \"{tempWav}\"";
                    proc.StartInfo.RedirectStandardError = true;
                    proc.StartInfo.RedirectStandardOutput = true;
                    proc.StartInfo.UseShellExecute = false;
                    proc.StartInfo.CreateNoWindow = true;
                    proc.Start();
                    await proc.WaitForExitAsync(ct);

                    if (proc.ExitCode != 0 || !System.IO.File.Exists(tempWav))
                    {
                        _logger.LogError("FFmpeg failed (exit {Code}).", proc.ExitCode);
                        return "Audio transcription failed.";
                    }
                }

                var speechConfig = SpeechConfig.FromSubscription(
                    _configuration["AzureSpeechServiceKey"],
                    _configuration["AzureSpeechServiceRegion"]);
                speechConfig.SpeechRecognitionLanguage = "en-US";

                using var audioConfig = AudioConfig.FromWavFileInput(tempWav);
                using var recognizer = new SpeechRecognizer(speechConfig, audioConfig);
                var result = await recognizer.RecognizeOnceAsync().ConfigureAwait(false);

                if (result.Reason == ResultReason.RecognizedSpeech)
                    return result.Text;

                return "Audio transcription not recognized.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audio transcription failed.");
                return "Audio transcription failed.";
            }
            finally
            {
                TryDelete(tempIn);
                TryDelete(tempWav);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { /* ignore */ }
        }

        private async Task<string> UploadToBlobAsync(Stream fileStream, string fileMimeType, CancellationToken cancellationToken)
        {
            fileStream.Position = 0;
            return await _kernel.InvokeAsync<string>(
                "BlobStoragePlugin",
                "UploadFile",
                new KernelArguments
                {
                    ["fileId"] = Guid.NewGuid().ToString(),
                    ["fileStream"] = fileStream,
                    ["fileMimeType"] = fileMimeType
                },
                cancellationToken);
        }

        private string ResolveFfmpegPath()
        {
            var configured = _configuration["FfmpegPath"];
            if (!string.IsNullOrWhiteSpace(configured) && System.IO.File.Exists(configured))
            {
                _logger.LogInformation("Using ffmpeg from configuration: {Path}", configured);
                return configured;
            }

            bool isWindows = OperatingSystem.IsWindows();
            string exe = isWindows ? "ffmpeg.exe" : "ffmpeg";

            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "ffmpeg", exe),
                Path.Combine(Directory.GetCurrentDirectory(), "ffmpeg", exe),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "ffmpeg", exe))
            };

            foreach (var c in candidates)
                if (System.IO.File.Exists(c)) return c;

            _logger.LogWarning("ffmpeg not found in local paths; falling back to PATH.");
            return "ffmpeg";
        }

        private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            _logger.LogError(exception, "An error occurred while receiving updates.");
            return Task.CompletedTask;
        }
    }
}