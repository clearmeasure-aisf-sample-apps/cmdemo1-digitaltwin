// Azure Service Bus transport for the twin intake queue (future option; deployed only when main.bicep deployServiceBus=true).
// Prerequisites a subscription Owner must satisfy first:
//   az provider register --namespace Microsoft.ServiceBus
//   deployer needs Microsoft.Authorization/roleAssignments/write on the namespace (or pre-create the role assignment)

@description('Location for the Service Bus namespace and identity.')
param location string

@description('Service Bus namespace name.')
param namespaceName string = 'sb-cmdemo1-twin'

@description('Intake queue name (Wolverine Twin:Messaging:QueueName).')
param queueName string = 'twin-intake'

@description('User-assigned managed identity the twin container app uses to reach Service Bus.')
param identityName string = 'id-cmdemo1-twin'

param tags object = {}

@description('SharedAccessKey: a Send+Listen rule whose key the app reads (Contributor-compatible, no role assignment). ManagedIdentity: Data Owner role for the identity (needs roleAssignments/write).')
@allowed([
  'ManagedIdentity'
  'SharedAccessKey'
])
param auth string = 'ManagedIdentity'

var useIdentity = auth == 'ManagedIdentity'

// Azure Service Bus Data Owner: Wolverine AutoProvision creates/validates queues, so send/receive alone is not enough.
var serviceBusDataOwnerRoleId = '090c5cfd-751d-490a-894a-3ce6f1109419'

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = if (useIdentity) {
  name: identityName
  location: location
  tags: tags
}

resource namespace 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: namespaceName
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    disableLocalAuth: useIdentity
    minimumTlsVersion: '1.2'
  }
}

resource queue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: namespace
  name: queueName
  properties: {
    lockDuration: 'PT1M'
    maxDeliveryCount: 10
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P14D'
  }
}

resource dataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (useIdentity) {
  scope: namespace
  name: guid(namespace.id, identityName, serviceBusDataOwnerRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataOwnerRoleId)
    principalId: identity!.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// The queue is created here, so the app needs no Manage right (Wolverine skips provisioning with a connection string).
resource appRule 'Microsoft.ServiceBus/namespaces/authorizationRules@2024-01-01' = if (!useIdentity) {
  parent: namespace
  name: 'twin-intake-app'
  properties: {
    rights: [
      'Send'
      'Listen'
    ]
  }
}

output namespaceFqdn string = '${namespace.name}.servicebus.windows.net'
output queueName string = queue.name
output namespaceName string = namespace.name
output appRuleName string = useIdentity ? '' : appRule.name
output identityResourceId string = useIdentity ? identity.id : ''
output identityClientId string = useIdentity ? identity!.properties.clientId : ''
