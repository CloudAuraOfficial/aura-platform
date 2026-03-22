// Aura Platform — Azure Container Instance Deployment
// Deploys: API + PostgreSQL 16 + Redis 7 as a multi-container group
//
// Usage:
//   az deployment group create \
//     --resource-group cloudaura-rg \
//     --template-file infra/azure/main.bicep \
//     --parameters acrName=cloudauraregistry \
//       imageTag=latest \
//       jwtSecret='<secret>' \
//       encryptionKey='<base64-32-byte>' \
//       postgresPassword='<password>'

@description('Name of the Azure Container Registry')
param acrName string

@description('Docker image tag to deploy')
param imageTag string = 'latest'

@description('Azure region for deployment')
param location string = resourceGroup().location

@description('DNS label for the public IP')
param dnsLabel string = 'cloudaura-api'

@description('CPU cores for the API container')
param apiCpu string = '0.5'

@description('Memory in GB for the API container')
param apiMemory string = '1.0'

@description('CPU cores for PostgreSQL')
param pgCpu string = '0.5'

@description('Memory in GB for PostgreSQL')
param pgMemory string = '0.5'

@description('CPU cores for Redis')
param redisCpu string = '0.25'

@description('Memory in GB for Redis')
param redisMemory string = '0.3'

// Secrets — passed via --parameters or parameter file (never hardcoded)
@secure()
@description('JWT signing secret (min 64 chars)')
param jwtSecret string

@secure()
@description('AES-256 encryption key (base64-encoded 32 bytes)')
param encryptionKey string

@secure()
@description('PostgreSQL password')
param postgresPassword string

@description('JWT token issuer')
param jwtIssuer string = 'aura-api'

@description('JWT token audience')
param jwtAudience string = 'aura-client'

@description('JWT expiry in minutes')
param jwtExpiryMinutes string = '60'

@description('CORS allowed origins')
param corsOrigins string = '*'

@description('PostgreSQL database name')
param postgresDb string = 'aura'

@description('PostgreSQL username')
param postgresUser string = 'aura'

// Reference existing ACR
resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: acrName
}

// Container group with API + PostgreSQL + Redis
resource containerGroup 'Microsoft.ContainerInstance/containerGroups@2023-05-01' = {
  name: 'aura-api'
  location: location
  properties: {
    osType: 'Linux'
    imageRegistryCredentials: [
      {
        server: acr.properties.loginServer
        username: acr.listCredentials().username
        password: acr.listCredentials().passwords[0].value
      }
    ]
    containers: [
      {
        name: 'aura-api'
        properties: {
          image: '${acr.properties.loginServer}/aura-api:${imageTag}'
          resources: {
            requests: {
              cpu: json(apiCpu)
              memoryInGB: json(apiMemory)
            }
          }
          ports: [
            { port: 8000 }
          ]
          environmentVariables: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ASPNETCORE_URLS', value: 'http://+:8000' }
            { name: 'JWT_SECRET', secureValue: jwtSecret }
            { name: 'JWT_ISSUER', value: jwtIssuer }
            { name: 'JWT_AUDIENCE', value: jwtAudience }
            { name: 'JWT_EXPIRY_MINUTES', value: jwtExpiryMinutes }
            { name: 'ENCRYPTION_KEY', secureValue: encryptionKey }
            { name: 'POSTGRES_HOST', value: 'localhost' }
            { name: 'POSTGRES_PORT', value: '5432' }
            { name: 'POSTGRES_DB', value: postgresDb }
            { name: 'POSTGRES_USER', value: postgresUser }
            { name: 'POSTGRES_PASSWORD', secureValue: postgresPassword }
            { name: 'REDIS_HOST', value: 'localhost' }
            { name: 'REDIS_PORT', value: '6379' }
            { name: 'CORS_ORIGINS', value: corsOrigins }
            { name: 'AUTO_MIGRATE', value: 'true' }
          ]
        }
      }
      {
        name: 'postgres'
        properties: {
          image: '${acr.properties.loginServer}/postgres:16-alpine'
          resources: {
            requests: {
              cpu: json(pgCpu)
              memoryInGB: json(pgMemory)
            }
          }
          ports: [
            { port: 5432 }
          ]
          environmentVariables: [
            { name: 'POSTGRES_DB', value: postgresDb }
            { name: 'POSTGRES_USER', value: postgresUser }
            { name: 'POSTGRES_PASSWORD', secureValue: postgresPassword }
          ]
        }
      }
      {
        name: 'redis'
        properties: {
          image: '${acr.properties.loginServer}/redis:7-alpine'
          resources: {
            requests: {
              cpu: json(redisCpu)
              memoryInGB: json(redisMemory)
            }
          }
          ports: [
            { port: 6379 }
          ]
        }
      }
    ]
    ipAddress: {
      type: 'Public'
      dnsNameLabel: dnsLabel
      ports: [
        { protocol: 'TCP', port: 8000 }
      ]
    }
  }
}

// Outputs
output fqdn string = containerGroup.properties.ipAddress.fqdn
output ip string = containerGroup.properties.ipAddress.ip
output healthUrl string = 'http://${containerGroup.properties.ipAddress.fqdn}:8000/health'
output dashboardUrl string = 'http://${containerGroup.properties.ipAddress.fqdn}:8000/dashboard/login'
