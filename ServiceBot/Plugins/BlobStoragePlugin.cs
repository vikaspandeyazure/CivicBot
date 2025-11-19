
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace ServiceBot.Plugins
{
    public class BlobStoragePlugin
    {
        private readonly BlobServiceClient _blobServiceClient;
        private readonly string _containerName;

        public BlobStoragePlugin(string blobEndpointUrl, string containerName)
        {
            _blobServiceClient = new BlobServiceClient(new Uri(blobEndpointUrl), new DefaultAzureCredential());
            _containerName = containerName;
        }

        [KernelFunction]
        [Description("Uploads a file stream to Azure Blob Storage and returns the blob's public URL.")]
        public async Task<string> UploadFileAsync(
            [Description("The unique ID for the file.")] string fileId,
            [Description("The file stream to upload.")] Stream fileStream,
            [Description("The MIME type of the file.")] string fileMimeType)
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
            await containerClient.CreateIfNotExistsAsync();

            string extension = fileMimeType.Split('/')[1];
            string blobName = $"{fileId}.{extension}";
            var blobClient = containerClient.GetBlobClient(blobName);

            fileStream.Position = 0;
            await blobClient.UploadAsync(fileStream, overwrite: true);

            return blobClient.Uri.ToString();
        }
    }
}
