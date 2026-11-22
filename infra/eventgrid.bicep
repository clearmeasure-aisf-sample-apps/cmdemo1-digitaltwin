targetScope = 'resourceGroup'

@description('Public HTTPS webhook endpoint URL including shared secret query token.')
@secure()
param webhookEndpointUrl string

@description('Short name of the observed system (resource tag).')
param systemSlug string = 'cmdemo1'

var rgName = resourceGroup().name
var topicName = 'eg-twin-${rgName}'

resource systemTopic 'Microsoft.EventGrid/systemTopics@2022-06-15' = {
  name: topicName
  location: 'global'
  tags: {
    system: systemSlug
    purpose: 'digital-twin'
  }
  properties: {
    source: resourceGroup().id
    topicType: 'Microsoft.Resources.ResourceGroups'
  }
}

resource subscription 'Microsoft.EventGrid/systemTopics/eventSubscriptions@2022-06-15' = {
  parent: systemTopic
  name: 'twin-observer'
  properties: {
    destination: {
      endpointType: 'WebHook'
      properties: {
        endpointUrl: webhookEndpointUrl
      }
    }
    filter: {
      includedEventTypes: [
        'Microsoft.Resources.ResourceWriteSuccess'
        'Microsoft.Resources.ResourceWriteFailure'
        'Microsoft.Resources.ResourceWriteCancel'
        'Microsoft.Resources.ResourceDeleteSuccess'
        'Microsoft.Resources.ResourceDeleteFailure'
        'Microsoft.Resources.ResourceDeleteCancel'
        'Microsoft.Resources.ResourceActionSuccess'
        'Microsoft.Resources.ResourceActionFailure'
        'Microsoft.Resources.ResourceActionCancel'
      ]
    }
    eventDeliverySchema: 'EventGridSchema'
  }
}

output topicName string = systemTopic.name
