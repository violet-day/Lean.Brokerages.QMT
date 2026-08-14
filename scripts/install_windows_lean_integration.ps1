param(
    [string]$RepositoryPath = "C:\Users\nemo\lean\Lean.Brokerages.QMT",
    [string]$LeanCliPath = "C:\Users\nemo\lean\lean-cli",
    [string]$LeanProjectRoot = "C:\Users\nemo\lean_project",
    [string]$AccountId = "",
    [string]$GatewayHost = "host.docker.internal",
    [int]$GatewayPort = 17890,
    [string]$EngineImage = "quantconnect/lean:latest",
    [string]$ResearchImage = "quantconnect/research:latest"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$utf8Encoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8Encoding
$OutputEncoding = $utf8Encoding

function Write-DeploymentLog {
    param([string]$Message)

    [Console]::Error.WriteLine("[qmt-deploy] $Message")
}

function Save-Utf8Text {
    param(
        [string]$Path,
        [string]$Content
    )

    $parentDirectory = Split-Path -Parent $Path
    if ($parentDirectory) {
        New-Item -ItemType Directory -Path $parentDirectory -Force | Out-Null
    }
    [System.IO.File]::WriteAllText($Path, $Content, $utf8Encoding)
}

function Confirm-QmtLeanCliIntegration {
    Write-DeploymentLog "stage=lean-cli-qmt-branch status=start path=$LeanCliPath"
    $leanPythonExecutable = "C:\Users\nemo\anaconda3\python.exe"
    if (-not (Test-Path -LiteralPath $leanPythonExecutable)) {
        throw "The lean-cli Python executable is missing: $leanPythonExecutable"
    }
    $moduleCheck = @'
from lean.models.cli import cli_brokerages, cli_data_queue_handlers
assert any(module.get_id() == "QmtBrokerage" for module in cli_brokerages)
assert any(module.get_id() == "QmtBrokerage" for module in cli_data_queue_handlers)
print("QMT brokerage and data queue modules loaded")
'@
    Push-Location $LeanCliPath
    try {
        $moduleCheck | & $leanPythonExecutable -
        if ($LASTEXITCODE -ne 0) {
            throw "The QMT lean-cli branch did not load its local module."
        }
    }
    finally {
        Pop-Location
    }
    Write-DeploymentLog "stage=lean-cli-qmt-branch status=ok"
}

function Set-DefaultLeanImages {
    $leanExecutable = "C:\Users\nemo\anaconda3\Scripts\lean.exe"
    Write-DeploymentLog "stage=default-images status=start engine=$EngineImage research=$ResearchImage"
    & $leanExecutable config set engine-image $EngineImage
    if ($LASTEXITCODE -ne 0) {
        throw "Could not set the default LEAN engine image."
    }
    & $leanExecutable config set research-image $ResearchImage
    if ($LASTEXITCODE -ne 0) {
        throw "Could not set the default LEAN research image."
    }
    Write-DeploymentLog "stage=default-images status=ok engine=$EngineImage research=$ResearchImage"
}

function New-QmtLeanConfiguration {
    $sourceConfigurationPath = Join-Path $LeanProjectRoot "lean.json"
    $qmtConfigurationPath = Join-Path $LeanProjectRoot "lean-qmt.json"
    Write-DeploymentLog "stage=lean-config status=start source=$sourceConfigurationPath destination=$qmtConfigurationPath"

    if (Test-Path -LiteralPath $qmtConfigurationPath) {
        $configuration = Get-Content -LiteralPath $qmtConfigurationPath -Raw | ConvertFrom-Json
    }
    else {
        $configuration = Get-Content -LiteralPath $sourceConfigurationPath -Raw | ConvertFrom-Json
    }

    if (-not $AccountId) {
        $existingAccountProperty = $configuration.PSObject.Properties["qmt-account-id"]
        if ($existingAccountProperty) {
            $AccountId = [string]$existingAccountProperty.Value
        }
    }
    if (-not $AccountId) {
        throw "AccountId is required the first time lean-qmt.json is created."
    }

    $configuration | Add-Member -NotePropertyName "qmt-gateway-host" -NotePropertyValue $GatewayHost -Force
    $configuration | Add-Member -NotePropertyName "qmt-gateway-port" -NotePropertyValue ([string]$GatewayPort) -Force
    $configuration | Add-Member -NotePropertyName "qmt-account-id" -NotePropertyValue $AccountId -Force
    $configuration | Add-Member -NotePropertyName "qmt-request-timeout" -NotePropertyValue "60" -Force
    $configuration | Add-Member -NotePropertyName "qmt-trading-enabled" -NotePropertyValue "false" -Force

    if (-not $configuration.PSObject.Properties["environments"]) {
        $configuration | Add-Member -NotePropertyName "environments" -NotePropertyValue ([pscustomobject]@{})
    }
    $liveQmtEnvironment = [ordered]@{
        "live-mode" = $true
        "live-mode-brokerage" = "QmtBrokerage"
        "setup-handler" = "QuantConnect.Lean.Engine.Setup.BrokerageSetupHandler"
        "result-handler" = "QuantConnect.Lean.Engine.Results.LiveTradingResultHandler"
        "data-feed-handler" = "QuantConnect.Lean.Engine.DataFeeds.LiveTradingDataFeed"
        "data-queue-handler" = @("QmtBrokerage")
        "real-time-handler" = "QuantConnect.Lean.Engine.RealTime.LiveTradingRealTimeHandler"
        "transaction-handler" = "QuantConnect.Lean.Engine.TransactionHandlers.BrokerageTransactionHandler"
        "history-provider" = @("BrokerageHistoryProvider")
    }
    $configuration.environments | Add-Member -NotePropertyName "live-qmt" -NotePropertyValue ([pscustomobject]$liveQmtEnvironment) -Force

    Save-Utf8Text -Path $qmtConfigurationPath -Content (($configuration | ConvertTo-Json -Depth 100) + "`n")
    $writtenConfiguration = Get-Content -LiteralPath $qmtConfigurationPath -Raw | ConvertFrom-Json
    if ([string]$writtenConfiguration."qmt-trading-enabled" -ne "false") {
        throw "qmt-trading-enabled must remain false."
    }
    Write-DeploymentLog "stage=lean-config status=ok path=$qmtConfigurationPath trading_enabled=false"
}

Confirm-QmtLeanCliIntegration
Set-DefaultLeanImages
New-QmtLeanConfiguration
Write-DeploymentLog "stage=install status=ok"
