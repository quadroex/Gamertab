targetScope = 'resourceGroup'

param location string = resourceGroup().location
param githubRepository string = 'quadroex/Gamertab'
param databaseVersion string = '18'
@secure()
param databasePassword string
@secure()
param igdbClientId string
@secure()
param igdbClientSecret string
@secure()
param googleClientId string = ''
@secure()
param googleClientSecret string = ''

var suffix = uniqueString(subscription().id, resourceGroup().id)
var appName = 'gamertab-${suffix}'

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'gamertab-plan'
  location: location
  kind: 'linux'
  sku: {
    name: 'F1'
    tier: 'Free'
  }
  properties: {
    reserved: true
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'gamertab${suffix}'
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: 'gamertab-db-${suffix}'
  location: location
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    version: databaseVersion
    administratorLogin: 'gamertabadmin'
    administratorLoginPassword: databasePassword
    authConfig: {
      activeDirectoryAuth: 'Disabled'
      passwordAuth: 'Enabled'
    }
    storage: {
      storageSizeGB: 32
      autoGrow: 'Disabled'
    }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      mode: 'Disabled'
    }
    network: {
      publicNetworkAccess: 'Enabled'
    }
  }
}

resource appDatabase 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: database
  name: 'gamertab'
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

resource app 'Microsoft.Web/sites@2024-04-01' = {
  name: appName
  location: location
  kind: 'app,linux'
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: false
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
    }
  }
}

resource appSettings 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: app
  name: 'appsettings'
  properties: {
    ASPNETCORE_ENVIRONMENT: 'Production'
    ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'true'
    AllowedHosts: app.properties.defaultHostName
    ConnectionStrings__Gamertab: 'Host=${database.properties.fullyQualifiedDomainName};Database=${appDatabase.name};Username=gamertabadmin;Password=${databasePassword};SSL Mode=VerifyFull'
    ConnectionStrings__QueueStorage: 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
    NewsUpdates__Enabled: 'true'
    Igdb__ClientId: igdbClientId
    Igdb__ClientSecret: igdbClientSecret
    Authentication__Google__ClientId: googleClientId
    Authentication__Google__ClientSecret: googleClientSecret
    WEBSITE_RUN_FROM_PACKAGE: '1'
    WEBSITE_WARMUP_PATH: '/health'
    WEBSITE_WARMUP_STATUSES: '200'
  }
}

resource deployIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'gamertab-deploy'
  location: location
}

resource githubCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: deployIdentity
  name: 'github-main'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: 'repo:${githubRepository}:ref:refs/heads/main'
    audiences: ['api://AzureADTokenExchange']
  }
}

resource deploymentRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(app.id, deployIdentity.id, 'website-contributor')
  scope: app
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'de139f84-1756-47ae-9be6-808fbbe84772')
    principalId: deployIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output appName string = app.name
output appUrl string = 'https://${app.properties.defaultHostName}'
output databaseName string = database.name
output outboundIpAddresses string = app.properties.outboundIpAddresses
output possibleOutboundIpAddresses string = app.properties.possibleOutboundIpAddresses
output deploymentClientId string = deployIdentity.properties.clientId
output tenantId string = tenant().tenantId
output subscriptionId string = subscription().subscriptionId
