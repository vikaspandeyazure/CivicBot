using Newtonsoft.Json;

namespace ServiceBot.Models
{
    public class ComplaintAttachment
    {
        [JsonProperty("fileType")]
        public string FileType { get; set; }

        [JsonProperty("blobUrl")]
        public string BlobUrl { get; set; }
        [JsonProperty("caption")]
        public string Caption { get; set; }
        public string Transcript { get; set; }
    }
}