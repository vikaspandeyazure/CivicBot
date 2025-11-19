using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ServiceBot.Models;

namespace ServiceBot.Services;

public sealed class ChatConversationService
{
    private readonly Kernel _kernel;
    private readonly ILogger<ChatConversationService> _logger;
    private readonly int _maxMessages;
    private readonly string _systemPrompt;

    // Per chat history
    private readonly ConcurrentDictionary<long, ChatHistory> _histories = new();
    
    // Track message types alongside history (for export purposes)
    private sealed class MessageMetadata
    {
        public string MessageType { get; set; } = "text";
        public System.DateTime Timestamp { get; set; } = System.DateTime.UtcNow;
    }
    
    private readonly ConcurrentDictionary<long, List<MessageMetadata>> _messageMetadata = new();

    public ChatConversationService(
        Kernel kernel,
        IConfiguration config,
        ILogger<ChatConversationService> logger)
    {
        _kernel = kernel;
        _logger = logger;

        _maxMessages = int.TryParse(config["ChatHistory:MaxMessages"], out var mm) && mm > 0 ? mm : 10;
        _systemPrompt = config["ChatHistory:SystemPrompt"] ??
                        "You are a helpful civic complaint intake assistant. Be concise, empathetic, and reference prior context when useful.";
    }

    private ChatHistory GetOrCreate(long chatId)
    {
        return _histories.GetOrAdd(chatId, _ =>
        {
            var h = new ChatHistory();
            h.AddSystemMessage(_systemPrompt);
            return h;
        });
    }

    private List<MessageMetadata> GetOrCreateMetadata(long chatId)
    {
        return _messageMetadata.GetOrAdd(chatId, _ => new List<MessageMetadata>());
    }

    private void Trim(ChatHistory history)
    {
        // Keep system message + last _maxMessages user/assistant messages
        // System is always first. If count > (_maxMessages + 1), drop oldest after system.
        while (history.Count > _maxMessages + 1)
        {
            // Remove second item (index 1) repeatedly to preserve system prompt at index 0
            history.RemoveAt(1);
        }
    }

    private void TrimMetadata(long chatId)
    {
        var metadata = GetOrCreateMetadata(chatId);
        // Keep only last _maxMessages entries
        while (metadata.Count > _maxMessages)
        {
            metadata.RemoveAt(0);
        }
    }

    public void AddUserMessage(long chatId, string content, string messageType = "text")
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        var h = GetOrCreate(chatId);
        h.AddUserMessage(content);
        Trim(h);
        
        // Track metadata
        var metadata = GetOrCreateMetadata(chatId);
        metadata.Add(new MessageMetadata { MessageType = messageType, Timestamp = System.DateTime.UtcNow });
        TrimMetadata(chatId);
    }

    public void AddAssistantMessage(long chatId, string content)
    {
        var history = GetOrCreate(chatId);
        history.AddAssistantMessage(content);
        Trim(history);
        
        // Track metadata
        var metadata = GetOrCreateMetadata(chatId);
        metadata.Add(new MessageMetadata { MessageType = "text", Timestamp = System.DateTime.UtcNow });
        TrimMetadata(chatId);
    }

    public string GetRecentContext(long chatId, int maxTurns = 3)
    {
        var history = GetOrCreate(chatId);
        var recent = history
            .Where(m => m.Role != Microsoft.SemanticKernel.ChatCompletion.AuthorRole.System)
            .TakeLast(maxTurns * 2) // user+assistant pairs
            .Select(m => $"{m.Role}: {m.Content}")
            .ToList();
        return recent.Any() ? string.Join("\n", recent) : string.Empty;
    }

    public void ClearHistory(long chatId)
    {
        if (_histories.TryRemove(chatId, out _))
        {
            _logger.LogInformation("Cleared conversation history for chat {ChatId}.", chatId);
        }
        _messageMetadata.TryRemove(chatId, out _);
    }

    /// <summary>
    /// Export conversation history as ConversationMessage list for ticket logging
    /// </summary>
    public List<ConversationMessage> ExportConversationHistory(long chatId)
    {
        if (!_histories.TryGetValue(chatId, out var history))
        {
            return new List<ConversationMessage>();
        }

        var metadata = GetOrCreateMetadata(chatId);
        var messages = history
            .Where(m => m.Role != Microsoft.SemanticKernel.ChatCompletion.AuthorRole.System)
            .ToList();

        var result = new List<ConversationMessage>();
        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            var meta = i < metadata.Count ? metadata[i] : new MessageMetadata();
            
            result.Add(new ConversationMessage
            {
                Role = msg.Role.ToString().ToLowerInvariant(),
                Content = msg.Content ?? string.Empty,
                Timestamp = meta.Timestamp,
                MessageType = meta.MessageType
            });
        }

        return result;
    }

    public async Task<string> GetAssistantReplyAsync(long chatId, string userIntent, CancellationToken ct = default)
    {
        var history = GetOrCreate(chatId);
        // Treat this as next user turn contextualizing summary/instructions
        history.AddUserMessage(userIntent);
        Trim(history);

        var chatService = _kernel.GetRequiredService<IChatCompletionService>();

        var result = await chatService.GetChatMessageContentAsync(
            history,
            kernel: _kernel,
            cancellationToken: ct);

        var assistantReply = result.Content ?? "I have acknowledged your message.";
        history.AddAssistantMessage(assistantReply);
        Trim(history);

        return assistantReply;
    }

    /// <summary>
    /// Generate assistant reply based on internal context WITHOUT saving the context prompt to history.
    /// Use this for internal agent prompts that shouldn't be visible in conversation logs.
    /// Only the assistant reply is saved to history.
    /// </summary>
    public async Task<string> GenerateAssistantReplyAsync(long chatId, string internalContext, CancellationToken ct = default)
    {
        var history = GetOrCreate(chatId);
        
        // Create a temporary copy of history with the internal context added
        var tempHistory = new ChatHistory();
        foreach (var msg in history)
        {
            tempHistory.Add(msg);
        }
        tempHistory.AddUserMessage(internalContext);

        var chatService = _kernel.GetRequiredService<IChatCompletionService>();

        var result = await chatService.GetChatMessageContentAsync(
            tempHistory,
            kernel: _kernel,
            cancellationToken: ct);

        var assistantReply = result.Content ?? "I have acknowledged your message.";
        
        // DON'T save to history here - let SendMessageAsync handle it
        // This avoids duplicate assistant messages
        
        return assistantReply;
    }
}