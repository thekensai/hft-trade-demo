param(
    [string]$ResourceGroupName = 'rg-tradedemo',
    [string]$Location = 'australiaeast'
)

$ErrorActionPreference = 'Stop'

function Ensure-AzExtension {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    Write-Host "Ensuring Azure CLI extension '$Name' is installed..."
    az extension add --name $Name --upgrade --only-show-errors | Out-Null
}

function Ensure-ProviderRegistration {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Namespace
    )

    $state = az provider show --namespace $Namespace --query registrationState -o tsv 2>$null

    if ($state -ne 'Registered') {
        Write-Host "Registering resource provider '$Namespace'..."
        az provider register -n $Namespace --wait --only-show-errors | Out-Null
    }
    else {
        Write-Host "Resource provider '$Namespace' is already registered."
    }
}

Ensure-AzExtension -Name 'containerapp'

$requiredProviders = @(
    'Microsoft.App',
    'Microsoft.OperationalInsights',
    'Microsoft.ContainerRegistry',
    'Microsoft.ServiceBus'
)

foreach ($provider in $requiredProviders) {
    Ensure-ProviderRegistration -Namespace $provider
}

Write-Host "Ensuring resource group '$ResourceGroupName' exists in '$Location'..."
az group create --name $ResourceGroupName --location $Location --only-show-errors | Out-Null

Write-Host ''
Write-Host 'Azure bootstrap complete.'
Write-Host "Next: az deployment group create --resource-group $ResourceGroupName --template-file main.bicep --parameters main.bicepparam"