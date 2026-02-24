[CmdletBinding()]
param(
    [Parameter()]
    [string]$Prefix = 'sbtools-test',

    [Parameter()]
    [string]$Location = 'eastus2',

    [Parameter()]
    [string]$SubscriptionId
)

$ErrorActionPreference = 'Stop'

$resourceGroup = "$Prefix-rg"

# Set subscription if provided
if ($SubscriptionId) {
    Write-Host "Setting subscription to $SubscriptionId..."
    az account set --subscription $SubscriptionId
}

# Create resource group
Write-Host "Creating resource group '$resourceGroup' in '$Location'..."
az group create --name $resourceGroup --location $Location --output none

# Deploy Bicep template
Write-Host "Deploying infrastructure..."
$deployment = az deployment group create `
    --resource-group $resourceGroup `
    --template-file "$PSScriptRoot/main.bicep" `
    --parameters "$PSScriptRoot/main.bicepparam" `
    --parameters prefix=$Prefix location=$Location `
    --output json | ConvertFrom-Json

$fqdn = $deployment.properties.outputs.serviceBusFqdn.value
$queueName = $deployment.properties.outputs.queueName.value
$appInsightsConnectionString = $deployment.properties.outputs.appInsightsConnectionString.value
$appInsightsResourceId = $deployment.properties.outputs.appInsightsResourceId.value

# Assign Service Bus Data Owner role to current user
Write-Host "Assigning 'Azure Service Bus Data Owner' role..."
$currentUser = az ad signed-in-user show --query id --output tsv
$serviceBusId = az servicebus namespace show --resource-group $resourceGroup --name "$Prefix-sbns" --query id --output tsv

az role assignment create `
    --assignee $currentUser `
    --role "Azure Service Bus Data Owner" `
    --scope $serviceBusId `
    --output none 2>$null

Write-Host ""
Write-Host "======================================" -ForegroundColor Green
Write-Host "  Infrastructure deployed successfully" -ForegroundColor Green
Write-Host "======================================" -ForegroundColor Green
Write-Host ""
Write-Host "Service Bus FQDN              : $fqdn"
Write-Host "Queue Name                    : $queueName"
Write-Host "App Insights Connection String: $appInsightsConnectionString"
Write-Host "App Insights Resource ID      : $appInsightsResourceId"
Write-Host ""
Write-Host "Example commands:" -ForegroundColor Cyan
Write-Host "  # Generate DLQ messages with correlated App Insights telemetry:"
Write-Host "  dotnet run --project src/ServiceBusToolset.TestHarness -- generate-dlq -n $fqdn -q $queueName -c 100 --app-insights-connection-string `"$appInsightsConnectionString`""
Write-Host ""
Write-Host "  # Generate DLQ messages without telemetry:"
Write-Host "  dotnet run --project src/ServiceBusToolset.TestHarness -- generate-dlq -n $fqdn -q $queueName -c 100"
Write-Host ""
Write-Host "  # Diagnose DLQ messages (wait 2-5 min after generate-dlq for telemetry ingestion):"
Write-Host "  dotnet run --project src/ServiceBusToolset.CLI -- diagnose-dlq -n $fqdn -q $queueName -a $appInsightsResourceId"
Write-Host ""
Write-Host "  # Dump DLQ messages:"
Write-Host "  dotnet run --project src/ServiceBusToolset.CLI -- dump-dlq -n $fqdn -q $queueName -i --merge-similar -o dump.json"
