param location string
param aiName string

resource workspace 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: '${aiName}-law'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource ai 'Microsoft.Insights/components@2020-02-02' = {
  name: aiName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
  }
}

output connectionString string = ai.properties.ConnectionString
