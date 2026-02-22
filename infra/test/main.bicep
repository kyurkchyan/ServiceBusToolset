@description('Resource name prefix')
param prefix string

@description('Azure region')
param location string

@description('Service Bus SKU')
@allowed(['Standard', 'Premium'])
param serviceBusSku string

var logAnalyticsName = '${prefix}-law'
var appInsightsName = '${prefix}-ai'
var serviceBusName = '${prefix}-sbns'
var queueName = 'test-dlq-queue'

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: serviceBusName
  location: location
  sku: {
    name: serviceBusSku
    tier: serviceBusSku
  }
}

resource testQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: serviceBusNamespace
  name: queueName
  properties: {
    maxDeliveryCount: 1
    deadLetteringOnMessageExpiration: true
  }
}

@description('Fully qualified Service Bus namespace')
output serviceBusFqdn string = '${serviceBusNamespace.name}.servicebus.windows.net'

@description('Test queue name')
output queueName string = testQueue.name

@description('Application Insights resource ID')
output appInsightsResourceId string = appInsights.id

@description('Application Insights connection string')
output appInsightsConnectionString string = appInsights.properties.ConnectionString
