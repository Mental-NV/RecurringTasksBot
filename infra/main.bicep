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

@description('LLM API key (secret): OpenRouter key for the OpenRouter profile.')
@secure()
param llmApiKey string

@description('LLM API key (secret): direct DeepSeek key for the DeepSeek profile. May be empty while the inactive credential is not provisioned.')
@secure()
param deepSeekApiKey string = ''

@description('Selected LLM profile (non-secret): switches the complete provider connection together.')
param llmActiveProfile string = 'OpenRouter'

@description('Durable task hub for the current state. Must match the selected environment file taskHubName.')
param taskHubName string

@description('Business table name.')
param tableName string

@description('Expected production Telegram bot numeric ID (non-secret validation).')
param expectedBotId string

@description('Public production webhook URL, https://<app>.azurewebsites.net/api/webhook.')
param webhookUrl string

@description('Dedicated Log Analytics workspace for application telemetry and temporary diagnostic captures.')
param monitoringWorkspaceName string

@description('Workspace-based Application Insights component.')
param applicationInsightsName string

@description('Email action group for queue polling alerts.')
param monitoringActionGroupName string

@description('Operator email for monitoring notifications, supplied by the production GitHub environment.')
@minLength(3)
param monitoringAlertEmail string

@description('Hourly ClientOtherError count above which each queue polling alert fires.')
@minValue(1)
param queuePollingErrorThreshold int = 100

@description('Daily log ingestion cap in GB as a JSON number string; 0.1 is 100 MB/day.')
param monitoringDailyCapGb string = '0.1'

module monitoring './monitoring.bicep' = {
  name: 'monitoring'
  params: {
    location: location
    storageAccountName: storageAccountName
    workspaceName: monitoringWorkspaceName
    applicationInsightsName: applicationInsightsName
    actionGroupName: monitoringActionGroupName
    alertEmail: monitoringAlertEmail
    queuePollingErrorThreshold: queuePollingErrorThreshold
    dailyCapGb: monitoringDailyCapGb
  }
}

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
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          // Module output defers the read until the component has been created.
          value: monitoring.outputs.applicationInsightsConnectionString
        }
        {
          name: 'AzureFunctionsJobHost__logging__logLevel__default'
          value: 'Warning'
        }
        {
          name: 'AzureFunctionsJobHost__logging__logLevel__Host.Results'
          value: 'Information'
        }
        {
          name: 'AzureFunctionsJobHost__logging__logLevel__Host.Aggregator'
          value: 'Information'
        }
        {
          name: 'AzureFunctionsJobHost__logging__logLevel__Function'
          value: 'Information'
        }
        {
          name: 'AzureFunctionsJobHost__logging__applicationInsights__samplingSettings__isEnabled'
          value: 'true'
        }
        {
          name: 'AzureFunctionsJobHost__logging__applicationInsights__samplingSettings__maxTelemetryItemsPerSecond'
          value: '2'
        }
        {
          name: 'AzureFunctionsJobHost__logging__applicationInsights__samplingSettings__excludedTypes'
          value: 'Request;Exception'
        }
        {
          name: 'SCALE_CONTROLLER_LOGGING_ENABLED'
          value: 'AppInsights:None'
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
          name: 'RecurringTasksBot__Llm__OpenRouter__ApiKey'
          value: llmApiKey
        }
        {
          name: 'RecurringTasksBot__Llm__DeepSeek__ApiKey'
          value: deepSeekApiKey
        }
        {
          name: 'RecurringTasksBot__Llm__ActiveProfile'
          value: llmActiveProfile
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
