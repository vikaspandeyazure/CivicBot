using ServiceBot.Plugins;
using ServiceBot.Plugins.Session;
using ServiceBot.Services;
using ServiceBot.Agents;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Telegram.Bot;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureServices((hostContext, services) =>
    {
        var config = hostContext.Configuration;

        // Register Telegram Bot Client
        services.AddSingleton<ITelegramBotClient>(provider =>
        {
            var token = config["TelegramBotToken"] ?? throw new ArgumentNullException("TelegramBotToken is missing.");
            return new TelegramBotClient(token);
        });

        services.AddHttpClient(); // For future external calls (video analysis, etc.)

        // Register Semantic Kernel with all plugins
        services.AddSingleton(provider =>
        {
            var kernel = Kernel.CreateBuilder()
                .AddAzureOpenAIChatCompletion(
                    deploymentName: config["AzureOpenAIDeploymentName"]!,
                    endpoint: config["AzureOpenAIEndpoint"]!,
                    apiKey: config["AzureOpenAIKey"]!
                )
                .Build();

            // Core plugins
            var blobPlugin = new BlobStoragePlugin(
                config["AzureBlobStorageConnectionString"]!,
                config["AzureBlobContainerName"]!
            );
            var cosmosPlugin = new CosmosDbPlugin(
                config["TenantId"]!,
                config["AzureCosmosDbConnectionString"]!,
                config["AzureCosmosDbDatabaseName"]!,
                config["AzureCosmosDbContainerName"]!
            );
            var aiAnalysisPlugin = new AIAnalysisPlugin(kernel);

            // Agent plugins
            var mediaProcessingPlugin = new MediaProcessingPlugin(kernel);
            var sessionManagementPlugin = new SessionManagementPlugin(kernel);

            kernel.ImportPluginFromObject(blobPlugin, "BlobStoragePlugin");
            kernel.ImportPluginFromObject(cosmosPlugin, "CosmosDbPlugin");
            kernel.ImportPluginFromObject(aiAnalysisPlugin, "AIAnalysisPlugin");
            kernel.ImportPluginFromObject(mediaProcessingPlugin, "MediaProcessingPlugin");
            kernel.ImportPluginFromObject(sessionManagementPlugin, "SessionManagementPlugin");

            return kernel;
        });

        // Register services
        services.AddSingleton<ChatConversationService>();
        services.AddSingleton<BackgroundBlobUploadService>();
        services.AddSingleton<VideoAnalysisService>();
        services.AddSingleton<ComplaintAgentOrchestrator>();

        // Register the TelegramBotUpdateHandler as a hosted service
        services.AddHostedService<TelegramBotUpdateHandler>();
    });

var app = builder.Build();
await app.RunAsync();