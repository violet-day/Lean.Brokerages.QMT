param(
    [string]$RepositoryPath = "C:\Users\nemo\lean\Lean.Brokerages.QMT",
    [string]$LeanProjectRoot = "C:\Users\nemo\lean_project",
    [string]$EngineImage = "quantconnect/lean:latest",
    [string]$ResearchImage = "quantconnect/research:latest",
    [string]$ModuleRoot = "$env:USERPROFILE\.lean\modules\QmtBrokerage",
    [int]$GatewayPort = 17890
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$utf8Encoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8Encoding
$OutputEncoding = $utf8Encoding
$liveTestLogPath = Join-Path $RepositoryPath ".test-logs\windows-live-test.log"
New-Item -ItemType Directory -Path (Split-Path -Parent $liveTestLogPath) -Force | Out-Null
[System.IO.File]::WriteAllText($liveTestLogPath, "", $utf8Encoding)

function Write-DeploymentLog {
    param([string]$Message)

    $logLine = "[qmt-live-test] $Message"
    [System.IO.File]::AppendAllText($liveTestLogPath, $logLine + "`r`n", $utf8Encoding)
    [Console]::Error.WriteLine($logLine)
}

$dockerExecutable = (Get-Command docker.exe -ErrorAction Stop).Source
$leanExecutable = "C:\Users\nemo\anaconda3\Scripts\lean.exe"
$configurationPath = Join-Path $LeanProjectRoot "lean-qmt.json"
$liveProjectPath = Join-Path $LeanProjectRoot "china_smoke_test"
$liveOutputPath = Join-Path $LeanProjectRoot ".qmt-live-smoke-output"

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

$engineImageMetadata = (& $dockerExecutable image inspect $EngineImage | ConvertFrom-Json)[0]
if (-not $engineImageMetadata) {
    throw "The default LEAN engine image is missing: $EngineImage"
}
$targetFramework = [string]$engineImageMetadata.Config.Labels.target_framework
$leanVersion = [string]$engineImageMetadata.Config.Labels.lean_version
$moduleDirectory = Join-Path (Join-Path $ModuleRoot $leanVersion) $targetFramework
$brokerageAssemblyPath = Join-Path $moduleDirectory "QuantConnect.Brokerages.Qmt.dll"
if (-not (Test-Path -LiteralPath $brokerageAssemblyPath)) {
    throw "The packaged QMT Brokerage assembly is missing: $brokerageAssemblyPath. Run make package-windows first."
}
Write-DeploymentLog "stage=brokerage-module status=ok image=$EngineImage lean_version=$leanVersion target_framework=$targetFramework path=$brokerageAssemblyPath"

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
    Write-DeploymentLog "stage=gateway status=failed port=$GatewayPort reason=not-listening"
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
$escapedExtraDockerConfiguration = $extraDockerConfiguration.Replace('"', '\"')

$leanArguments = @(
    "live", "deploy", $liveProjectPath,
    "--lean-config", $configurationPath,
    "--environment", "live-qmt",
    "--detach",
    "--no-update",
    "--extra-docker-config", $escapedExtraDockerConfiguration,
    "--output", $liveOutputPath
)
Write-DeploymentLog "stage=lean-live status=start image=$EngineImage environment=live-qmt project=$liveProjectPath module=$brokerageAssemblyPath"
$existingLeanContainers = @(& $dockerExecutable ps --filter "ancestor=$EngineImage" --format "{{.ID}}")
if ($existingLeanContainers.Count -ne 0) {
    throw "A LEAN container is already running: $($existingLeanContainers -join ', ')."
}

$containerId = $null
$containerLogText = ""
$historyPassed = $false
$accountPassed = $false
$minuteBarPassed = $false
$subscriptionPassed = $false
$completed = $false
$chinaTimeZone = [TimeZoneInfo]::FindSystemTimeZoneById("China Standard Time")
$chinaNow = [TimeZoneInfo]::ConvertTimeFromUtc([DateTime]::UtcNow, $chinaTimeZone)
$chinaTimeOfDay = $chinaNow.TimeOfDay
$isChinaWeekday = $chinaNow.DayOfWeek -notin @([DayOfWeek]::Saturday, [DayOfWeek]::Sunday)
$isMorningSession = $chinaTimeOfDay -ge [TimeSpan]::FromHours(9.5) -and $chinaTimeOfDay -lt [TimeSpan]::FromHours(11.5)
$isAfternoonSession = $chinaTimeOfDay -ge [TimeSpan]::FromHours(13) -and $chinaTimeOfDay -lt [TimeSpan]::FromHours(15)
$marketClosedPassed = -not ($isChinaWeekday -and ($isMorningSession -or $isAfternoonSession))
Write-DeploymentLog "stage=market-hours status=ok china_time=$($chinaNow.ToString('yyyy-MM-ddTHH:mm:ss')) market_closed=$marketClosedPassed"
$previousErrorActionPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = "Continue"
    $leanOutput = & $leanExecutable @leanArguments 2>&1
    $leanExitCode = $LASTEXITCODE
    if ($leanOutput) {
        $leanOutputText = ($leanOutput | Out-String)
        [System.IO.File]::AppendAllText($liveTestLogPath, $leanOutputText, $utf8Encoding)
        [Console]::Error.Write($leanOutputText)
    }
    if ($leanExitCode -ne 0) {
        throw "lean live deploy failed with exit code $leanExitCode."
    }

    $containerDiscoveryDeadline = (Get-Date).AddSeconds(30)
    while (-not $containerId -and (Get-Date) -lt $containerDiscoveryDeadline) {
        $containerId = (& $dockerExecutable ps --filter "ancestor=$EngineImage" --format "{{.ID}}" | Select-Object -First 1)
        if (-not $containerId) {
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $containerId) {
        throw "The detached LEAN live container did not start."
    }

    $validationDeadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $validationDeadline) {
        $containerLogText = (& $dockerExecutable logs $containerId 2>&1 | Out-String)
        $dailyHistoryPassed = $containerLogText.Contains("QmtBrokerage.GetHistory(): status=ok symbol=600000 resolution=Daily bars=5")
        $minuteHistoryPassed = $containerLogText.Contains("QmtBrokerage.GetHistory(): status=ok symbol=600000 resolution=Minute")
        $historyPassed = $dailyHistoryPassed -and $minuteHistoryPassed
        $accountPassed = $containerLogText.Contains("QmtBrokerage.GetCashBalance(): status=ok accounts=1")
        $subscriptionPassed = $containerLogText.Contains("QmtBrokerage.Subscribe(): status=ok symbol=600000")
        $minuteBarPassed = $containerLogText.Contains("[qmt-e2e] stage=minute-bar status=ok")
        $completed = $containerLogText.Contains("[qmt-e2e] stage=complete status=ok trading=disabled")

        if ($historyPassed -and $accountPassed -and $subscriptionPassed) {
            if ($minuteBarPassed -and $completed) {
                break
            }
            if ($marketClosedPassed) {
                break
            }
        }

        $containerIsRunning = (& $dockerExecutable inspect --format "{{.State.Running}}" $containerId 2>$null) -eq "true"
        if (-not $containerIsRunning) {
            break
        }
        Start-Sleep -Seconds 1
    }

    $containerIsRunning = (& $dockerExecutable inspect --format "{{.State.Running}}" $containerId 2>$null) -eq "true"
    if ($containerIsRunning) {
        & $dockerExecutable stop --time 30 $containerId | Out-Null
    }

    & $dockerExecutable inspect $containerId *> $null
    if ($LASTEXITCODE -eq 0) {
        $containerLogText = (& $dockerExecutable logs $containerId 2>&1 | Out-String)
    }
    $minuteBarPassed = $minuteBarPassed -or $containerLogText.Contains("[qmt-e2e] stage=minute-bar status=ok")
    $completed = $completed -or $containerLogText.Contains("[qmt-e2e] stage=complete status=ok trading=disabled")
    [System.IO.File]::AppendAllText($liveTestLogPath, $containerLogText, $utf8Encoding)
    [Console]::Error.Write($containerLogText)
}
finally {
    if ($containerId) {
        $containerIsRunning = (& $dockerExecutable inspect --format "{{.State.Running}}" $containerId 2>$null) -eq "true"
        if ($containerIsRunning) {
            & $dockerExecutable stop --time 30 $containerId | Out-Null
        }
    }
    $ErrorActionPreference = $previousErrorActionPreference
}
if (-not $historyPassed) {
    throw "The real QMT daily/minute history success markers were not found."
}
if (-not $accountPassed) {
    throw "The real QMT account query success marker was not found."
}
if (-not $subscriptionPassed) {
    throw "The real QMT subscription success marker was not found."
}
if (-not ($minuteBarPassed -and $completed) -and -not $marketClosedPassed) {
    throw "Neither a real QMT minute TradeBar nor the closed-market marker was found."
}
$minuteBarStatus = if ($minuteBarPassed) { "ok" } else { "deferred_market_closed" }
Write-DeploymentLog "stage=lean-live status=ok image=$EngineImage history=ok account=ok subscription=ok minute_bar=$minuteBarStatus trading_enabled=false output=$liveOutputPath"
