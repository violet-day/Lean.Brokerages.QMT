param(
    [string]$LeanRoot = "C:\Users\nemo\lean-net10\Lean",
    [string]$RepositoryPath = "C:\Users\nemo\lean-net10\Lean.Brokerages.QMT",
    [string]$LeanCliPath = "C:\Users\nemo\lean\lean-cli",
    [string]$LeanProjectRoot = "C:\Users\nemo\lean_project",
    [string]$AccountId = "",
    [string]$GatewayHost = "host.docker.internal",
    [int]$GatewayPort = 17890
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

function Add-QmtLauncherProjectReference {
    $launcherProjectPath = Join-Path $LeanRoot "Launcher\QuantConnect.Lean.Launcher.csproj"
    $referencePath = "..\..\Lean.Brokerages.QMT\QuantConnect.QmtBrokerage\QuantConnect.QmtBrokerage.csproj"
    Write-DeploymentLog "stage=lean-integration status=start launcher=$launcherProjectPath"

    [xml]$launcherProject = Get-Content -LiteralPath $launcherProjectPath -Raw
    $existingReference = @($launcherProject.Project.ItemGroup.ProjectReference) |
        Where-Object { $_.Include -eq $referencePath }
    if ($existingReference.Count -eq 0) {
        $referenceGroups = @($launcherProject.Project.ItemGroup) |
            Where-Object { @($_.ProjectReference).Count -gt 0 }
        if ($referenceGroups.Count -eq 0) {
            throw "The Launcher project has no ProjectReference ItemGroup."
        }

        $projectReference = $launcherProject.CreateElement("ProjectReference")
        $projectReference.SetAttribute("Include", $referencePath)
        [void]$referenceGroups[0].AppendChild($projectReference)
        $writerSettings = New-Object System.Xml.XmlWriterSettings
        $writerSettings.Indent = $true
        $writerSettings.IndentChars = "  "
        $writerSettings.NewLineChars = "`r`n"
        $writerSettings.Encoding = $utf8Encoding
        $writer = [System.Xml.XmlWriter]::Create($launcherProjectPath, $writerSettings)
        try {
            $launcherProject.Save($writer)
        }
        finally {
            $writer.Dispose()
        }
        Write-DeploymentLog "stage=lean-integration status=updated project_reference=$referencePath"
    }
    else {
        Write-DeploymentLog "stage=lean-integration status=unchanged project_reference=$referencePath"
    }
}

function Install-QmtLeanCliOverlay {
    $sourceModulePath = Join-Path $RepositoryPath "deployment\lean-cli\modules-local.json"
    $destinationModulePath = Join-Path $LeanCliPath "lean\modules-local.json"
    $modelsInitializerPath = Join-Path $LeanCliPath "lean\models\__init__.py"
    $overlayMarker = "qmt-local-modules-overlay"
    Write-DeploymentLog "stage=lean-cli-overlay status=start path=$LeanCliPath"

    Copy-Item -LiteralPath $sourceModulePath -Destination $destinationModulePath -Force
    $modelsInitializer = Get-Content -LiteralPath $modelsInitializerPath -Raw
    if (-not $modelsInitializer.Contains($overlayMarker)) {
        $overlayCode = @'

# qmt-local-modules-overlay: merge optional locally maintained modules after the CDN manifest.
local_modules_path = directory.parent / "modules-local.json"
if local_modules_path.is_file():
    with open(local_modules_path, encoding="utf-8") as local_modules_file:
        json_modules.extend(load(local_modules_file).get("modules", []))
'@
        Save-Utf8Text -Path $modelsInitializerPath -Content ($modelsInitializer.TrimEnd() + $overlayCode + "`n")
        Write-DeploymentLog "stage=lean-cli-overlay status=updated initializer=$modelsInitializerPath"
    }
    else {
        Write-DeploymentLog "stage=lean-cli-overlay status=unchanged initializer=$modelsInitializerPath"
    }

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
            throw "The QMT lean-cli module overlay did not load."
        }
    }
    finally {
        Pop-Location
    }
    Write-DeploymentLog "stage=lean-cli-overlay status=ok"
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
    $configuration | Add-Member -NotePropertyName "qmt-request-timeout" -NotePropertyValue "10" -Force
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
        "history-provider" = @("SubscriptionDataReaderHistoryProvider")
    }
    $configuration.environments | Add-Member -NotePropertyName "live-qmt" -NotePropertyValue ([pscustomobject]$liveQmtEnvironment) -Force

    Save-Utf8Text -Path $qmtConfigurationPath -Content (($configuration | ConvertTo-Json -Depth 100) + "`n")
    $writtenConfiguration = Get-Content -LiteralPath $qmtConfigurationPath -Raw | ConvertFrom-Json
    if ([string]$writtenConfiguration."qmt-trading-enabled" -ne "false") {
        throw "qmt-trading-enabled must remain false."
    }
    Write-DeploymentLog "stage=lean-config status=ok path=$qmtConfigurationPath trading_enabled=false"
}

Add-QmtLauncherProjectReference
Install-QmtLeanCliOverlay
New-QmtLeanConfiguration
Write-DeploymentLog "stage=install status=ok"
