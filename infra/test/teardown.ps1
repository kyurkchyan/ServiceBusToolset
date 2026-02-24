[CmdletBinding()]
param(
    [Parameter()]
    [string]$Prefix = 'sbtools-test',

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

# Confirm deletion
$confirm = Read-Host "Are you sure you want to delete resource group '$resourceGroup'? (y/N)"
if ($confirm -ne 'y') {
    Write-Host "Cancelled."
    return
}

# Delete resource group
Write-Host "Deleting resource group '$resourceGroup' (no-wait)..."
az group delete --name $resourceGroup --yes --no-wait

Write-Host "Resource group deletion initiated. It may take a few minutes to complete." -ForegroundColor Yellow
