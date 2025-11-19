# CivicBot

CivicBot is an intelligent automation bot designed to assist with civic tasks and digital automation.
<img width="832" height="822" alt="Screenshot 2025-11-19 211532" src="https://github.com/user-attachments/assets/89f4aa7a-ad62-4f85-99cd-cce147925e72" />

## Prerequisites

- **Windows** environment (for Prerequisite.bat script)
- [.NET SDK](https://dotnet.microsoft.com/) (version per your solution requirements)

## Setup Instructions

1. **Install Required Packages**

   Run the following script to install all necessary dependencies:
   ```
   Prerequisite.bat
   ```

2. **Download and Prepare FFmpeg**

   - Download the appropriate FFmpeg binary for your platform from [FFmpeg official downloads](https://ffmpeg.org/download.html).
   - Extract the FFmpeg binaries and copy the contents of the `bin` folder (containing `ffmpeg.exe`, `ffprobe.exe`, etc.) into the project's `ffmpeg` directory.

     ```
     <project_root>/ffmpeg/
       ├── ffmpeg.exe
       ├── ffprobe.exe
       └── ...
     ```

3. **Configure appsettings.json**
 
   - Create an `appsettings.json` file in the project root.
   - Add your configuration variables as required by the code; for example:

     ```json
 {
  "TelegramBotToken": "Your Bot father token",
  "AzureOpenAIEndpoint": "",
  "AzureOpenAIDeploymentName": "gpt-4o-mini",
  "AzureOpenAIKey": "",
  "AzureBlobStorageConnectionString": "",
  "AzureBlobContainerName": "complaints",
  "AzureCosmosDbConnectionString": "",
  "AzureCosmosDbDatabaseName": "ComplaintsDb",
  "AzureCosmosDbContainerName": "complaints",
  "AzureSpeechServiceKey": "",
  "AzureSpeechServiceRegion": "eastus",
  "TenantId": "",
  "FfmpegPath": "",
  
  "AzureVisionAIEndpoint": "",
  "AzureVisionAIKey": "",
  "AzureVisionAIApiVersion": "2023-05-01-preview",
  "AzureOpenAIExtensionsApiVersion": "2023-07-01-preview",
  "AzureVisionAIDeleteIndexAfterUse": "true",
  
  "ChatHistory": {
    "MaxMessages": 6,
    "SystemPrompt": "You are CivicBot, a friendly sanitation assistant.\n\n**Style:**\n- First message: Warm greeting + brief intro (2-3 sentences)\n- Gathering info: Brief and direct (1 sentence)\n- Confirmation: Show summary + ask yes/no\n- Don't repeat what user already told you\n\n**Topics:** Garbage, waste, drains, sewage, odors, pests.\n\n**Rules:**\n- Welcome users naturally on first contact\n- Ask for missing details only: location, urgency, duration\n- Off-topic: Politely redirect\n- Never create tickets without confirmation"
  },
  
  "Ticketing": {
    "SessionExpiryMinutes": 30
  }
}


     ```
   - **Note**: The above are actual examples. Please refer to your project code for specific required keys and their usage.

4. **Build the Project**

   Open a command prompt in the project root and build the solution:
   ```
   dotnet build
   ```

5. **Run the Project**

   Run your bot with:
   ```
   dotnet run
   ```

## Contributing

Pull requests are welcome. For major changes, please open an issue first to discuss what you would like to change.

## License

[MIT](LICENSE)
