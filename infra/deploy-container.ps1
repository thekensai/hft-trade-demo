param(
    [string]$ResourceGroupName = 'rg-tradedemo',
    [string]$Location = 'australiaeast',
    [string]$BaseName = 'tradedemo',
    [string]$ResourceSuffix,
    [string]$RegistryName,
    [string]$ImageRepository = 'tradedemo-api',
    [string]$ImageTag,
    [switch]$DeployInfra
)

$ErrorActionPreference = 'Stop'

if (-not $ImageTag) {
    $ImageTag = [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dockerfilePath = Join-Path $repoRoot 'Dockerfile'
$bicepPath = Join-Path $PSScriptRoot 'main.bicep'

if (-not (Test-Path $dockerfilePath)) {
    throw "Could not find Dockerfile at '$dockerfilePath'."
}

if (-not $RegistryName) {
    $RegistryName = az acr list --resource-group $ResourceGroupName --query "[0].name" -o tsv
}

if (-not $RegistryName) {
    $RegistryName = ('tradedemo{0}' -f ((Get-Random -Minimum 100000 -Maximum 999999)))
    Write-Host "Creating Azure Container Registry '$RegistryName'..."
    az acr create --resource-group $ResourceGroupName --name $RegistryName --sku Basic --admin-enabled true --only-show-errors | Out-Null
}

$loginServer = az acr show --name $RegistryName --resource-group $ResourceGroupName --query loginServer -o tsv
$acrUser = az acr credential show --name $RegistryName --query username -o tsv
$acrPass = az acr credential show --name $RegistryName --query "passwords[0].value" -o tsv
$imageRef = "$loginServer/$ImageRepository`:$ImageTag"

if (-not $loginServer -or -not $acrUser -or -not $acrPass) {
    throw "Could not resolve Azure Container Registry credentials for '$RegistryName'."
}

$indexPath = Join-Path $repoRoot 'src\TradeDemo.Api\wwwroot\index.html'
$indexHtml = Get-Content $indexPath -Raw
if ([string]::IsNullOrWhiteSpace($indexHtml)) {
    throw "Refusing to deploy: '$indexPath' is empty."
}
$indexHtml = $indexHtml -replace 'href="css/shared\.css(?:\?v=[^"]*)?"', "href=`"css/shared.css?v=$ImageTag`""
$indexHtml = $indexHtml -replace 'href="css/terminal\.css(?:\?v=[^"]*)?"', "href=`"css/terminal.css?v=$ImageTag`""
$indexHtml = $indexHtml -replace 'src="js/terminal\.js(?:\?v=[^"]*)?"', "src=`"js/terminal.js?v=$ImageTag`""
Set-Content $indexPath $indexHtml -NoNewline

Write-Host "Building Dockerfile in Azure Container Registry and pushing image to '$imageRef'..."
az acr build --registry $RegistryName --image "$ImageRepository`:$ImageTag" --file $dockerfilePath $repoRoot --only-show-errors | Out-Null

if ($DeployInfra) {
    if (-not $ResourceSuffix) {
        $existingAppName = az deployment group show --resource-group $ResourceGroupName --name main --query properties.outputs.containerAppName.value -o tsv 2>$null
        if ($existingAppName -match "^$([regex]::Escape($BaseName))-app-(.+)$") {
            $ResourceSuffix = $Matches[1]
            Write-Host "Reusing existing resource suffix '$ResourceSuffix' from Container App '$existingAppName'."
        }
    }

    $deploymentParameters = @(
        "baseName=$BaseName"
        "location=$Location"
        "containerImage=$imageRef"
        "registryServer=$loginServer"
        "registryUsername=$acrUser"
        "registryPassword=$acrPass"
    )

    if ($ResourceSuffix) {
        $deploymentParameters += "resourceSuffix=$ResourceSuffix"
    }

    Write-Host "Deploying Azure infrastructure with image '$imageRef'..."
    az deployment group create `
        --resource-group $ResourceGroupName `
        --template-file $bicepPath `
        --parameters $deploymentParameters `
        --only-show-errors | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "Azure infrastructure deployment failed."
    }
}

$appName = az deployment group show --resource-group $ResourceGroupName --name main --query properties.outputs.containerAppName.value -o tsv

if (-not $appName) {
    throw "Could not resolve Container App name from deployment outputs in resource group '$ResourceGroupName'. Run this script with -DeployInfra or deploy infra/main.bicep first."
}

Write-Host "Configuring registry credentials on Container App '$appName'..."
az containerapp registry set --name $appName --resource-group $ResourceGroupName --server $loginServer --username $acrUser --password $acrPass --only-show-errors | Out-Null

Write-Host "Updating Container App '$appName' to image '$imageRef'..."
az containerapp update --name $appName --resource-group $ResourceGroupName --image $imageRef --only-show-errors | Out-Null

$appUrl = az deployment group show --resource-group $ResourceGroupName --name main --query properties.outputs.appUrl.value -o tsv

Write-Host ''
Write-Host 'Container deployment complete.'
Write-Host "App URL: $appUrl"
