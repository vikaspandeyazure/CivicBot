using System;
using Newtonsoft.Json;

namespace ServiceBot.Models
{
    /// <summary>
    /// Represents a single message in the conversation between user and bot
    /// </summary>
    public class ConversationMessage
    {
        [JsonProperty("role")]
        public string Role { get; set; } = string.Empty; // "user" or "assistant"

        [JsonProperty("content")]
        public string Content { get; set; } = string.Empty;

        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        [JsonProperty("messageType")]
        public string? MessageType { get; set; } // "text", "audio", "photo", "video", etc.
    }
}
