param location string
param planName string
param appName string
param kvName string
param aiConnectionString string

var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: kvName
}

resource plan 'Microsoft.Web/serverfarms@2023-01-01' existing = {
  name: planName
}

resource app 'Microsoft.Web/sites@2023-01-01' = {
  name: appName
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      appSettings: [
        { name: 'DB_CONNECTION', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=db-connection)' }
        // Optional provider keys — referenced from Key Vault. Set these secrets in Key Vault to enable each provider.
        { name: 'ANTHROPIC_BILLING_KEY', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=anthropic-billing-key)' }
        { name: 'GITHUB_TOKEN', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=github-token)' }
        { name: 'COPILOT_ORG', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=copilot-org)' }
        { name: 'GOOGLE_BILLING_ACCOUNT_ID', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=google-billing-account-id)' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: aiConnectionString }
      ]
    }
  }
}

resource kvRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(app.id, kv.id, keyVaultSecretsUserRoleId)
  scope: kv
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output possibleOutboundIpAddresses string = app.properties.possibleOutboundIpAddresses
