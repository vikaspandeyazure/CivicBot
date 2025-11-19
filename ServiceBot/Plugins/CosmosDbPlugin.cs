using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.SemanticKernel;
using ServiceBot.Agents;
using System.Collections.Concurrent;
using System.ComponentModel;

namespace ServiceBot.Plugins
{
    public class CosmosDbPlugin
    {
        private readonly CosmosClient _cosmosClient;
        private readonly Microsoft.Azure.Cosmos.Container _container;

        public CosmosDbPlugin(string TenantId,string endpoint, string databaseName, string containerName)
        {

            var options = new DefaultAzureCredentialOptions
            {
                TenantId = TenantId
            };
            var credential = new DefaultAzureCredential(options);
            _cosmosClient = new CosmosClient(endpoint, credential);


//            var credential = new DefaultAzureCredential();
  //          _cosmosClient = new CosmosClient(endpoint, credential);
            var database = _cosmosClient.GetDatabase(databaseName);
            _container = database.GetContainer(containerName);
        }

        [KernelFunction]
        [Description("Creates a new complaint ticket in Azure Cosmos DB.")]
        public async Task<string> CreateTicketAsync(
            [Description("The complaint ticket object.")] ComplaintTicket ticket)
        {
            var response = await _container.CreateItemAsync(ticket, new PartitionKey(ticket.Id));
            return response.Resource.Id;
        }
    }
}
