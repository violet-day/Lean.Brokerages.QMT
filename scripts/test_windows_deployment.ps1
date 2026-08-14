param(
    [string]$RepositoryPath = "C:\Users\nemo\lean\Lean.Brokerages.QMT",
    [string]$LeanProjectRoot = "C:\Users\nemo\lean_project",
    [string]$EngineImage = "quantconnect/lean:latest",
    [string]$ResearchImage = "quantconnect/research:latest",
    [int]$GatewayPort = 17890
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$utf8Encoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8Encoding
$OutputEncoding = $utf8Encoding

function Write-DeploymentLog {
    param([string]$Message)

    [Console]::Error.WriteLine("[qmt-live-test] $Message")
}

$dockerExecutable = (Get-Command docker.exe -ErrorAction Stop).Source
$leanExecutable = "C:\Users\nemo\anaconda3\Scripts\lean.exe"
$configurationPath = Join-Path $LeanProjectRoot "lean-qmt.json"
$liveProjectPath = Join-Path $LeanProjectRoot "china_smoke_test"
$liveOutputPath = Join-Path $RepositoryPath ".test-logs\live-smoke-output"

& $dockerExecutable version --format "{{.Server.Os}}/{{.Server.Arch}}" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Docker Desktop is not ready."
}
if (-not (Test-Path -LiteralPath $leanExecutable)) {
    throw "lean-cli is missing: $leanExecutable"
}
if (-not (Test-Path -LiteralPath $configurationPath)) {
    throw "The QMT LEAN configuration is missing: $configurationPath"
}
if (-not (Test-Path -LiteralPath (Join-Path $liveProjectPath ".git"))) {
    throw "The Git smoke project is missing: $liveProjectPath"
}

& $leanExecutable config set engine-image $EngineImage
if ($LASTEXITCODE -ne 0) {
    throw "Could not restore the default LEAN engine image."
}
& $leanExecutable config set research-image $ResearchImage
if ($LASTEXITCODE -ne 0) {
    throw "Could not restore the default LEAN research image."
}

$moduleDirectory = & (Join-Path $RepositoryPath "scripts\test_windows.ps1") -RepositoryPath $RepositoryPath -EngineImage $EngineImage
if ($LASTEXITCODE -ne 0) {
    throw "The Windows QMT build and package step failed."
}
$moduleDirectory = [string]($moduleDirectory | Select-Object -Last 1)
$brokerageAssemblyPath = Join-Path $moduleDirectory "QuantConnect.Brokerages.Qmt.dll"
if (-not (Test-Path -LiteralPath $brokerageAssemblyPath)) {
    throw "The packaged QMT Brokerage assembly is missing: $brokerageAssemblyPath"
}
Write-DeploymentLog "stage=brokerage-module status=ok image=$EngineImage path=$brokerageAssemblyPath"

$configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
if ([string]$configuration."qmt-gateway-host" -ne "host.docker.internal") {
    throw "qmt-gateway-host must be host.docker.internal."
}
if ([int]$configuration."qmt-gateway-port" -ne $GatewayPort) {
    throw "qmt-gateway-port must be $GatewayPort."
}
if ([string]$configuration."qmt-trading-enabled" -ne "false") {
    throw "qmt-trading-enabled must remain false for the live smoke."
}
$historyProviders = @($configuration.environments."live-qmt"."history-provider")
if ($historyProviders -notcontains "BrokerageHistoryProvider") {
    throw "live-qmt must use BrokerageHistoryProvider."
}

$gatewayListener = Get-NetTCPConnection -State Listen -LocalPort $GatewayPort -ErrorAction SilentlyContinue
if (-not $gatewayListener) {
    throw "The real QMT Gateway is not listening on Windows port $GatewayPort. Run the Gateway strategy manually in QMT first."
}
Write-DeploymentLog "stage=gateway status=ok port=$GatewayPort local_address=$($gatewayListener[0].LocalAddress)"

if (Test-Path -LiteralPath $liveOutputPath) {
    Remove-Item -LiteralPath $liveOutputPath -Recurse -Force
}

$brokerageVolume = @{}
$brokerageVolume[$brokerageAssemblyPath] = @{
    "bind" = "/Lean/Launcher/bin/Debug/QuantConnect.Brokerages.Qmt.dll"
    "mode" = "ro"
}
$extraDockerConfiguration = @{
    "volumes" = $brokerageVolume
} | ConvertTo-Json -Depth 5 -Compress

$leanArguments = @(
    "live", "deploy", $liveProjectPath,
    "--lean-config", $configurationPath,
    "--environment", "live-qmt",
    "--no-update",
    "--extra-docker-config", $extraDockerConfiguration,
    "--output", $liveOutputPath
)
Write-DeploymentLog "stage=lean-live status=start image=$EngineImage environment=live-qmt project=$liveProjectPath module=$brokerageAssemblyPath"
& $leanExecutable @leanArguments
if ($LASTEXITCODE -ne 0) {
    throw "lean live deploy failed with exit code $LASTEXITCODE."
}

$logFiles = @(Get-ChildItem -LiteralPath $liveOutputPath -Recurse -File -ErrorAction SilentlyContinue)
$historyPassed = $false
$accountPassed = $false
$minuteBarPassed = $false
$completed = $false
foreach ($logFile in $logFiles) {
    if (Select-String -LiteralPath $logFile.FullName -SimpleMatch "[qmt-e2e] stage=initialize status=ok" -Quiet -ErrorAction SilentlyContinue) {
        $historyPassed = $true
    }
    if (Select-String -LiteralPath $logFile.FullName -SimpleMatch "[qmt-e2e] stage=account status=ok" -Quiet -ErrorAction SilentlyContinue) {
        $accountPassed = $true
    }
    if (Select-String -LiteralPath $logFile.FullName -SimpleMatch "[qmt-e2e] stage=minute-bar status=ok" -Quiet -ErrorAction SilentlyContinue) {
        $minuteBarPassed = $true
    }
    if (Select-String -LiteralPath $logFile.FullName -SimpleMatch "[qmt-e2e] stage=complete status=ok trading=disabled" -Quiet -ErrorAction SilentlyContinue) {
        $completed = $true
    }
}
if (-not $historyPassed) {
    throw "The real QMT daily/minute history success marker was not found in $liveOutputPath."
}
if (-not $accountPassed) {
    throw "The real QMT account success marker was not found in $liveOutputPath."
}
if (-not $minuteBarPassed) {
    throw "The real QMT minute TradeBar success marker was not found in $liveOutputPath."
}
if (-not $completed) {
    throw "The QMT E2E completion marker was not found in $liveOutputPath."
}
Write-DeploymentLog "stage=lean-live status=ok image=$EngineImage history=ok account=ok minute_bar=ok trading_enabled=false output=$liveOutputPath"
