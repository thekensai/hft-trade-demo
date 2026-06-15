param(
    [string]$ResourceGroupName = 'rg-tradedemo',
    [string]$RegistryName,
    [string]$ImageRepository = 'tradedemo-api',
    [string]$ImageTag
)

$ErrorActionPreference = 'Stop'

if (-not $ImageTag) {
    $ImageTag = [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
}

$appName = az deployment group show --resource-group $ResourceGroupName --name main --query properties.outputs.containerAppName.value -o tsv

if (-not $appName) {
    throw "Could not resolve Container App name from deployment outputs in resource group '$ResourceGroupName'."
}

if (-not $RegistryName) {
    $RegistryName = az acr list --resource-group $ResourceGroupName --query "[0].name" -o tsv
}

if (-not $RegistryName) {
    $RegistryName = ('tradedemo{0}' -f ((Get-Random -Minimum 100000 -Maximum 999999)))
    Write-Host "Creating Azure Container Registry '$RegistryName'..."
    az acr create --resource-group $ResourceGroupName --name $RegistryName --sku Basic --admin-enabled true --only-show-errors | Out-Null
}

$indexPath = Join-Path $PSScriptRoot 'src\TradeDemo.Api\wwwroot\index.html'
$indexHtml = Get-Content $indexPath -Raw
if ([string]::IsNullOrWhiteSpace($indexHtml)) {
    throw "Refusing to deploy: '$indexPath' is empty."
}
$indexHtml = $indexHtml -replace 'href="css/shared\.css(?:\?v=[^"]*)?"', "href=`"css/shared.css?v=$ImageTag`""
$indexHtml = $indexHtml -replace 'href="css/terminal\.css(?:\?v=[^"]*)?"', "href=`"css/terminal.css?v=$ImageTag`""
$indexHtml = $indexHtml -replace 'src="js/terminal\.js(?:\?v=[^"]*)?"', "src=`"js/terminal.js?v=$ImageTag`""
Set-Content $indexPath $indexHtml -NoNewline

$loginServer = az acr show --name $RegistryName --resource-group $ResourceGroupName --query loginServer -o tsv
$acrUser = az acr credential show --name $RegistryName --query username -o tsv
$acrPass = az acr credential show --name $RegistryName --query "passwords[0].value" -o tsv
$imageRef = "$loginServer/$ImageRepository`:$ImageTag"

if (-not $loginServer -or -not $acrUser -or -not $acrPass) {
    throw "Could not resolve Azure Container Registry credentials for '$RegistryName'."
}

Write-Host "Publishing container image to '$imageRef'..."
$env:DOTNET_CONTAINER_REGISTRY_UNAME = $acrUser
$env:DOTNET_CONTAINER_REGISTRY_PWORD = $acrPass

try {
    dotnet publish .\src\TradeDemo.Api\TradeDemo.Api.csproj --os linux --arch x64 /t:PublishContainer -p:ContainerRegistry=$loginServer -p:ContainerRepository=$ImageRepository -p:ContainerImageTags=$ImageTag
}
finally {
    Remove-Item Env:DOTNET_CONTAINER_REGISTRY_UNAME -ErrorAction SilentlyContinue
    Remove-Item Env:DOTNET_CONTAINER_REGISTRY_PWORD -ErrorAction SilentlyContinue
}

Write-Host "Configuring registry credentials on Container App '$appName'..."
az containerapp registry set --name $appName --resource-group $ResourceGroupName --server $loginServer --username $acrUser --password $acrPass --only-show-errors | Out-Null

Write-Host "Updating Container App '$appName' to image '$imageRef'..."
az containerapp update --name $appName --resource-group $ResourceGroupName --image $imageRef --only-show-errors | Out-Null

$appUrl = az deployment group show --resource-group $ResourceGroupName --name main --query properties.outputs.appUrl.value -o tsv

Write-Host ''
Write-Host 'Container deployment complete.'
Write-Host "App URL: $appUrl"