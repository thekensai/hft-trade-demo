targetScope = 'resourceGroup'

@description('Base name for all resources')
param baseName string = 'tradedemo'

@description('Azure region')
param location string = resourceGroup().location

@description('Container image (set after first ACR push)')
param containerImage string = 'mcr.microsoft.com/dotnet/samples:aspnetapp'

var suffix = take(uniqueString(subscription().subscriptionId, resourceGroup().id), 6)
var logAnalyticsName = '${baseName}-logs-${suffix}'
var containerEnvName = '${baseName}-env-${suffix}'
var containerAppName = '${baseName}-app-${suffix}'
var serviceBusNamespaceName = '${baseName}-bus-${suffix}'

// ── Log Analytics (required for Container Apps) ──
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

// ── Container Apps Environment ──
resource containerEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: containerEnvName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

// ── Container App (hosts API + static frontend) ──
resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: containerAppName
  location: location
  properties: {
    managedEnvironmentId: containerEnv.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: 'tradedemo-api'
          image: containerImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            {
              name: 'ASPNETCORE_URLS'
              value: 'http://+:8080'
            }
            {
              name: 'ServiceBus__ConnectionString'
              value: serviceBusAuthRule.listKeys().primaryConnectionString
            }
            {
              name: 'ServiceBus__QueueName'
              value: serviceBusQueue.name
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 3
      }
    }
  }
}

// ── Service Bus Namespace ──
resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: serviceBusNamespaceName
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
}

// ── Service Bus Queue (market data ingestion) ──
resource serviceBusQueue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  parent: serviceBusNamespace
  name: 'market-events'
  properties: {
    maxDeliveryCount: 10
    defaultMessageTimeToLive: 'PT5M'
    lockDuration: 'PT30S'
    maxSizeInMegabytes: 1024
    enableBatchedOperations: true
    enablePartitioning: true
  }
}

// ── Service Bus Auth Rule ──
resource serviceBusAuthRule 'Microsoft.ServiceBus/namespaces/authorizationRules@2022-10-01-preview' = {
  parent: serviceBusNamespace
  name: 'app-send-listen'
  properties: {
    rights: [
      'Send'
      'Listen'
    ]
  }
}

// ── Outputs ──
output appUrl string = 'https://${containerApp.properties.configuration.ingress.fqdn}'
output containerAppName string = containerApp.name
output containerEnvName string = containerEnv.name
output serviceBusNamespaceName string = serviceBusNamespace.name
output queueName string = serviceBusQueue.name
