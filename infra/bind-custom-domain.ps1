param(
    [Parameter(Mandatory = $true)]
    [string]$Hostname,

    [string]$ResourceGroupName = 'rg-tradedemo',

    [string]$ContainerAppName,

    [switch]$ApexDomain,

    [switch]$Bind
)

$ErrorActionPreference = 'Stop'

if (-not $ContainerAppName) {
    $ContainerAppName = az deployment group show --resource-group $ResourceGroupName --name main --query properties.outputs.containerAppName.value -o tsv
}

if (-not $ContainerAppName) {
    throw "Could not resolve Container App name from deployment outputs in resource group '$ResourceGroupName'."
}

$appInfo = az containerapp show --name $ContainerAppName --resource-group $ResourceGroupName --query "{fqdn:properties.configuration.ingress.fqdn,verificationId:properties.customDomainVerificationId,environmentId:properties.managedEnvironmentId}" -o json | ConvertFrom-Json

if (-not $appInfo.fqdn -or -not $appInfo.verificationId -or -not $appInfo.environmentId) {
    throw "Could not resolve required Container App custom-domain details for '$ContainerAppName'."
}

$envInfo = az containerapp env show --ids $appInfo.environmentId --query "{staticIp:properties.staticIp,defaultDomain:properties.defaultDomain}" -o json | ConvertFrom-Json

Write-Output ''
Write-Output "Container App : $ContainerAppName"
Write-Output "Default FQDN  : $($appInfo.fqdn)"
Write-Output "Verify TXT    : $($appInfo.verificationId)"

if ($ApexDomain) {
    Write-Output "Static IP     : $($envInfo.staticIp)"
    Write-Output ''
    Write-Output 'Create these DNS records for the root/apex domain:'
    Write-Output "- A    $Hostname -> $($envInfo.staticIp)"
    Write-Output "- TXT  asuid.$Hostname -> $($appInfo.verificationId)"
}
else {
    Write-Output ''
    Write-Output 'Create these DNS records for the subdomain:'
    Write-Output "- CNAME  $Hostname -> $($appInfo.fqdn)"
    Write-Output "- TXT    asuid.$Hostname -> $($appInfo.verificationId)"
}

Write-Output ''
Write-Output 'Wait for DNS propagation before binding the hostname.'

if ($Bind) {
    Write-Output ''
    Write-Output "Binding hostname '$Hostname' to Container App '$ContainerAppName'..."
    az containerapp hostname bind --name $ContainerAppName --resource-group $ResourceGroupName --hostname $Hostname --only-show-errors | Out-Null
    Write-Output 'Hostname binding complete.'
}
else {
    $apexDomainArg = ''
    if ($ApexDomain) {
        $apexDomainArg = ' -ApexDomain'
    }

    Write-Output "When DNS is ready, run: .\bind-custom-domain.ps1 -Hostname $Hostname -ResourceGroupName $ResourceGroupName$apexDomainArg -Bind"
}