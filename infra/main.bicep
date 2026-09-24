// Deploys the production Function App onto the EXISTING production storage
// account. Never creates storage: business data, Durable state, and
// deployment content all live in the pre-provisioned production account.
// Windows Consumption plan: no Always On, no scan jobs (Durable timers only).

targetScope = 'resourceGroup'

@description('Azure region, e.g. westeurope.')
param location string

@description('Production Function App name (also used as the content share).')
param functionAppName string

@description('Consumption plan name.')
param planName string

@description('Name of the EXISTING production storage account.')
param storageAccountName string

@description('Production storage connection string (secret). Reused for content share.')
@secure()
param storageConnectionString string

@description('Production Telegram bot token (secret).')
@secure()
param telegramBotToken string

@description('Production Telegram webhook secret (secret).')
@secure()
param telegramWebhookSecret string

@description('LLM API key (secret): OpenRouter key used for DeepSeek execution and web search.')
@secure()
param llmApiKey string

@description('Durable task hub. Must stay RecurringTasksProd across deployments.')
param taskHubName string

@description('Business table name.')
param tableName string

@description('Expected production Telegram bot numeric ID (non-secret validation).')
param expectedBotId string

@description('Public production webhook URL, https://<app>.azurewebsites.net/api/webhook.')
param webhookUrl string

resource storage 'Microsoft.Storage/storageAccounts@2023-01-01' existing = {
  name: storageAccountName
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  kind: 'functionapp'
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  properties: {}
}

resource app 'Microsoft.Web/sites@2023-12-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp'
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      alwaysOn: false
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: [
        {
          name: 'AzureWebJobsStorage'
          value: storageConnectionString
        }
        {
          name: 'WEBSITE_CONTENTAZUREFILECONNECTIONSTRING'
          value: storageConnectionString
        }
        {
          name: 'WEBSITE_CONTENTSHARE'
          value: toLower(functionAppName)
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'RecurringTasksBot__AzureWebJobsStorage'
          value: storageConnectionString
        }
        {
          name: 'RecurringTasksBot__Telegram__BotToken'
          value: telegramBotToken
        }
        {
          name: 'RecurringTasksBot__Telegram__WebhookSecret'
          value: telegramWebhookSecret
        }
        {
          name: 'RecurringTasksBot__Llm__ApiKey'
          value: llmApiKey
        }
        // Non-secret LLM options come from the published appsettings profiles.
        {
          name: 'RecurringTasksBot__TableName'
          value: tableName
        }
        {
          name: 'RecurringTasksBot__TaskHubName'
          value: taskHubName
        }
        {
          name: 'RecurringTasksBot__ExpectedBotId'
          value: expectedBotId
        }
        {
          name: 'RecurringTasksBot__WebhookUrl'
          value: webhookUrl
        }
        {
          name: 'AzureFunctionsJobHost__extensions__durableTask__hubName'
          value: taskHubName
        }
      ]
    }
  }
}

output functionAppName string = app.name
output webhookUrl string = webhookUrl
output storageAccountId string = storage.id
