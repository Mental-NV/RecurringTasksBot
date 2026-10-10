// Permanent monitoring only. Queue diagnostics and verbose scale-controller
// logging are temporary operator captures (see docs/Monitoring.md).
targetScope = 'resourceGroup'

param location string
param storageAccountName string
param workspaceName string
param applicationInsightsName string
param actionGroupName string

@minLength(3)
param alertEmail string

@minValue(1)
param queuePollingErrorThreshold int

@description('Daily ingestion cap in GB, represented as a JSON number string for fractional values.')
param dailyCapGb string

resource storage 'Microsoft.Storage/storageAccounts@2023-01-01' existing = {
  name: storageAccountName
}

resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2023-01-01' existing = {
  parent: storage
  name: 'default'
}

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: workspaceName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    workspaceCapping: {
      // Bicep has no fractional numeric literal; json() emits the number 0.1.
      dailyQuotaGb: json(dailyCapGb)
    }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: applicationInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
  }
}

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: actionGroupName
  location: 'global'
  properties: {
    groupShortName: 'recur-bot'
    enabled: true
    emailReceivers: [
      {
        name: 'operator'
        emailAddress: alertEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

var pollingApis = [
  'PeekMessage'
  'GetQueueMetadata'
]

resource pollingAlerts 'Microsoft.Insights/metricAlerts@2018-03-01' = [for apiName in pollingApis: {
  name: 'queue-${apiName}-client-errors'
  location: 'global'
  properties: {
    description: 'Repeated failed ${apiName} calls; inspect queue and scale-controller diagnostics.'
    enabled: true
    severity: 2
    evaluationFrequency: 'PT15M'
    windowSize: 'PT1H'
    autoMitigate: true
    scopes: [
      queueService.id
    ]
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'FailedQueuePolling'
          criterionType: 'StaticThresholdCriterion'
          metricNamespace: 'Microsoft.Storage/storageAccounts/queueServices'
          metricName: 'Transactions'
          timeAggregation: 'Total'
          operator: 'GreaterThan'
          threshold: queuePollingErrorThreshold
          dimensions: [
            {
              name: 'ApiName'
              operator: 'Include'
              values: [
                apiName
              ]
            }
            {
              name: 'ResponseType'
              operator: 'Include'
              values: [
                'ClientOtherError'
              ]
            }
          ]
        }
      ]
    }
    actions: [
      {
        actionGroupId: actionGroup.id
      }
    ]
  }
}]

// Read the component inside the module that creates it. A parent-side existing
// reference can be evaluated before this module completes on a first deployment.
@secure()
output applicationInsightsConnectionString string = appInsights.properties.ConnectionString
