using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using ServiceBot.Models;

namespace ServiceBot.Agents
{
    public class ComplaintTicket
    {
        [JsonProperty("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("userId")]
        public long UserId { get; set; }

        [JsonProperty("chatId")]
        public long ChatId { get; set; }

        [JsonProperty("userFullName")]
        public string UserFullName { get; set; }

        [JsonProperty("mobileNumber")]
        public string? MobileNumber { get; set; }

        [JsonProperty("textSummary")]
        public string TextSummary { get; set; } = string.Empty;

        [JsonProperty("attachments")]
        public List<ComplaintAttachment> Attachments { get; set; } = new List<ComplaintAttachment>();

        [JsonProperty("conversations")]
        public List<ConversationMessage> Conversations { get; set; } = new List<ConversationMessage>();

        [JsonProperty("status")]
        public string Status { get; set; } = "New";

        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}