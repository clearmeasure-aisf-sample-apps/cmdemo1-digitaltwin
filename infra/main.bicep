targetScope = 'resourceGroup'

@description('Azure region for Container Apps and Log Analytics.')
param location string = resourceGroup().location

@description('Azure region for Azure SQL (cmdemo1 uses centralus due to subscription regional SQL rules).')
param sqlLocation string = 'centralus'

@description('Full container image reference in ACR (acrcmdemo1chu4gq.azurecr.io/cmdemo1/digitaltwin:<tag>).')
param containerImage string

@description('ACR login server.')
param acrServer string = 'acrcmdemo1chu4gq.azurecr.io'

@description('ACR username (admin user enabled via Contributor).')
param acrUsername string = 'acrcmdemo1chu4gq'

@secure()
param acrPassword string

@description('Service principal object ID granted Key Vault secret access policy.')
param deployerObjectId string = deployer().objectId

@secure()
param sqlAdminPassword string

param azureClientId string
@secure()
param azureClientSecret string
param azureTenantId string
param azureSubscriptionId string
param octopusUrl string
@secure()
param octopusApiKey string
@secure()
param gitHubPat string
@secure()
param webhookSecret string
@secure()
param twinApiKey string

@description('Wolverine intake transport: SqlServer (queue table in the twin database) or LocalDurable (in-process durable queue). Ignored when deployServiceBus is true.')
@allowed([
  'SqlServer'
  'LocalDurable'
])
param intakeTransport string = 'SqlServer'

@description('Deploy sb-cmdemo1-twin and switch the intake to Azure Service Bus with managed identity. Requires Microsoft.ServiceBus to be registered and roleAssignments/write; disabled until a subscription Owner enables it.')
param deployServiceBus bool = false

@description('How the twin authenticates to Service Bus when deployServiceBus is true: ManagedIdentity (needs roleAssignments/write) or SharedAccessKey (Contributor-compatible).')
@allowed([
  'ManagedIdentity'
  'SharedAccessKey'
])
param serviceBusAuth string = 'ManagedIdentity'

@description('Archive raw intake events older than the retention window as compressed daily files in a private Blob container.')
param deployArchive bool = true

@description('Incoming-webhook URL for alerts (Teams or Slack). Empty (default) keeps alerts off.')
@secure()
param alertWebhookUrl string = ''

@description('Alert payload format for the webhook.')
@allowed([
  'Slack'
  'Teams'
  'Generic'
])
param alertFormat string = 'Slack'

@description('Days raw intake events stay in SQL before they move to the archive.')
param retentionDays int = 30

@description('Size budget for the raw intake table in SQL (MB): above it, the oldest complete days are archived early.')
param retentionMaxRawMb int = 500

@description('Short name of the observed system; names every twin resource (log-<slug>-twin, ca-<slug>-twin, ...). Changing it on an existing deployment creates new resources.')
param systemSlug string = 'cmdemo1'

@description('Platform tag on every twin resource.')
param platformTag string = 'clear-measure'

@description('Platform definition JSON for a platform other than cmdemo1 (see docs/setup/README.md). Empty observes cmdemo1.')
param platformJson string = ''

var compactSlug = replace(toLower(systemSlug), '-', '')
var suffix = take(uniqueString(subscription().id, resourceGroup().id, '${systemSlug}-twin'), 5)
var tags = {
  system: systemSlug
  platform: platformTag
  purpose: 'digital-twin'
  layer: 'platform-observer'
}

var logName = 'log-${systemSlug}-twin'
var caeName = 'cae-${systemSlug}-twin'
var appName = 'ca-${systemSlug}-twin'
var kvName = take('kv${compactSlug}twin${suffix}', 24)
var sqlServerName = 'sql-${systemSlug}-twin-${suffix}'
var sqlDbName = 'sqldb-${systemSlug}-twin'
var sqlAdminLogin = 'twinadmin'
var sqlConnectionString = 'Server=tcp:${sqlServerName}${az.environment().suffixes.sqlServerHostname},1433;Database=${sqlDbName};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'

resource logWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource managedEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: caeName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logWorkspace.properties.customerId
        sharedKey: logWorkspace.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
}

// Access-policy Key Vault avoids Microsoft.Authorization/roleAssignments/write (Contributor-compatible)
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: kvName
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: false
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    accessPolicies: [
      {
        tenantId: subscription().tenantId
        objectId: deployerObjectId
        permissions: {
          secrets: ['get', 'list', 'set']
        }
      }
    ]
  }
}

resource sqlPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'sql-admin-password'
  properties: {
    value: sqlAdminPassword
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01' = {
  name: sqlServerName
  location: sqlLocation
  tags: tags
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// Basic (5 DTU, 2 GB, fixed ~$5/mo) is always-on: avoids Serverless free-tier auto-pause lockout under continuous 45-60s polling.
resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sqlServer
  name: sqlDbName
  location: sqlLocation
  tags: tags
  sku: {
    name: 'Basic'
    tier: 'Basic'
    capacity: 5
  }
  properties: {
    maxSizeBytes: 2147483648
  }
}

var archiveAccountName = take('st${compactSlug}twin${suffix}', 24)
var archiveContainerName = 'raw-intake-archive'

resource archiveAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = if (deployArchive) {
  name: archiveAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Cool'
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
  }
}

resource archiveBlobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = if (deployArchive) {
  parent: archiveAccount
  name: 'default'
}

resource archiveContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = if (deployArchive) {
  parent: archiveBlobService
  name: archiveContainerName
  properties: {
    publicAccess: 'None'
  }
}

// Shared key via listKeys (Contributor-compatible: no role assignment needed); passed only as a Container App secret.
var archiveConnectionString = deployArchive
  ? 'DefaultEndpointsProtocol=https;AccountName=${archiveAccountName};AccountKey=${archiveAccount!.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
  : ''

var archiveSecrets = deployArchive ? [{ name: 'archive-connection', value: archiveConnectionString }] : []

var archiveEnv = deployArchive
  ? [
      { name: 'Twin__Archive__Provider', value: 'AzureBlob' }
      { name: 'Twin__Archive__Container', value: archiveContainerName }
      { name: 'Twin__Archive__ConnectionString', secretRef: 'archive-connection' }
      { name: 'Twin__Retention__Days', value: string(retentionDays) }
      { name: 'Twin__Retention__MaxRawMb', value: string(retentionMaxRawMb) }
    ]
  : [
      { name: 'Twin__Archive__Provider', value: 'None' }
    ]

var alertSecrets = empty(alertWebhookUrl) ? [] : [{ name: 'alert-webhook-url', value: alertWebhookUrl }]
var alertEnv = concat([{ name: 'Twin__Alerts__Format', value: alertFormat }], empty(alertWebhookUrl) ? [] : [{ name: 'Twin__Alerts__WebhookUrl', secretRef: 'alert-webhook-url' }])

var twinIdentityName = 'id-${systemSlug}-twin'
var serviceBusNamespaceName = 'sb-${systemSlug}-twin'
var platformEnv = empty(platformJson) ? [] : [{ name: 'Twin__Platform__Json', value: platformJson }]

module serviceBus 'servicebus.bicep' = if (deployServiceBus) {
  name: 'deploy-${systemSlug}-twin-servicebus'
  params: {
    location: location
    namespaceName: serviceBusNamespaceName
    identityName: twinIdentityName
    tags: tags
    auth: serviceBusAuth
  }
}

var serviceBusUsesKey = deployServiceBus && serviceBusAuth == 'SharedAccessKey'

resource serviceBusAppRule 'Microsoft.ServiceBus/namespaces/authorizationRules@2024-01-01' existing = if (serviceBusUsesKey) {
  name: '${serviceBusNamespaceName}/twin-intake-app'
  dependsOn: [
    serviceBus
  ]
}

var serviceBusSecrets = serviceBusUsesKey ? [{ name: 'servicebus-connection', value: serviceBusAppRule!.listKeys().primaryConnectionString }] : []

var intakeEnv = serviceBusUsesKey
  ? [
      { name: 'Twin__Messaging__Transport', value: 'AzureServiceBus' }
      { name: 'Twin__Messaging__ServiceBusConnectionString', secretRef: 'servicebus-connection' }
    ]
  : deployServiceBus
  ? [
      { name: 'Twin__Messaging__Transport', value: 'AzureServiceBus' }
      { name: 'Twin__Messaging__ServiceBusNamespace', value: serviceBus!.outputs.namespaceFqdn }
      { name: 'Twin__Messaging__ManagedIdentityClientId', value: serviceBus!.outputs.identityClientId }
    ]
  : [
      { name: 'Twin__Messaging__Transport', value: intakeTransport }
    ]

var twinPublicUrl = 'https://${appName}.${managedEnvironment.properties.defaultDomain}'

resource twinApp 'Microsoft.App/containerApps@2025-01-01' = {
  name: appName
  location: location
  tags: tags
  dependsOn: [
    sqlDatabase
    allowAzureServices
    archiveContainer
  ]
  identity: deployServiceBus && !serviceBusUsesKey
    ? {
        type: 'UserAssigned'
        userAssignedIdentities: {
          '${resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', twinIdentityName)}': {}
        }
      }
    : {
        type: 'None'
      }
  properties: {
    environmentId: managedEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      registries: [
        {
          server: acrServer
          username: acrUsername
          passwordSecretRef: 'acr-password'
        }
      ]
      secrets: concat([
        { name: 'acr-password', value: acrPassword }
        #disable-next-line use-secure-value-for-secure-inputs
        { name: 'twin-db-connection', value: sqlConnectionString }
        { name: 'azure-client-secret', value: azureClientSecret }
        { name: 'octopus-api-key', value: octopusApiKey }
        { name: 'gh-pat', value: gitHubPat }
        { name: 'webhook-secret', value: webhookSecret }
        { name: 'twin-api-key', value: twinApiKey }
      ], archiveSecrets, serviceBusSecrets, alertSecrets)
    }
    template: {
      containers: [
        {
          name: 'twin'
          image: containerImage
          resources: {
            cpu: json('1.0')
            memory: '2Gi'
          }
          env: concat([
            { name: 'ASPNETCORE_URLS', value: 'http://+:8080' }
            { name: 'AZURE_CLIENT_ID', value: azureClientId }
            { name: 'AZURE_TENANT_ID', value: azureTenantId }
            { name: 'AZURE_SUBSCRIPTION_ID', value: azureSubscriptionId }
            { name: 'OCTOPUS_URL', value: octopusUrl }
            { name: 'TWIN_PUBLIC_URL', value: twinPublicUrl }
            { name: 'ConnectionStrings__TwinDb', secretRef: 'twin-db-connection' }
            { name: 'AZURE_CLIENT_SECRET', secretRef: 'azure-client-secret' }
            { name: 'OCTOPUS_API_KEY', secretRef: 'octopus-api-key' }
            { name: 'GH_PAT', secretRef: 'gh-pat' }
            { name: 'WEBHOOK_SECRET', secretRef: 'webhook-secret' }
            { name: 'TWIN_API_KEY', secretRef: 'twin-api-key' }
          ], intakeEnv, archiveEnv, alertEnv, platformEnv)
          probes: [
            {
              type: 'Startup'
              httpGet: { path: '/_healthcheck', port: 8080 }
              initialDelaySeconds: 5
              periodSeconds: 5
              timeoutSeconds: 5
              // Up to ~7.5 min: first-time Wolverine schema migration is slow on the Basic tier database.
              failureThreshold: 90
            }
            {
              type: 'Readiness'
              httpGet: { path: '/_healthcheck', port: 8080 }
              periodSeconds: 15
              timeoutSeconds: 5
              failureThreshold: 4
            }
            {
              type: 'Liveness'
              httpGet: { path: '/_healthcheck', port: 8080 }
              initialDelaySeconds: 20
              periodSeconds: 30
              timeoutSeconds: 10
              failureThreshold: 5
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

output twinAppName string = twinApp.name
output twinFqdn string = twinApp.properties.configuration.ingress.fqdn
output twinUrl string = 'https://${twinApp.properties.configuration.ingress.fqdn}'
output managedEnvironmentName string = managedEnvironment.name
output keyVaultName string = keyVault.name
output sqlServerName string = sqlServer.name
output sqlDatabaseName string = sqlDatabase.name
output archiveAccountName string = deployArchive ? archiveAccountName : ''
output intakeTransport string = deployServiceBus ? 'AzureServiceBus' : intakeTransport
