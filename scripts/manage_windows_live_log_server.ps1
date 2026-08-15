param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Start", "Stop", "Status")]
    [string]$Action,
    [string]$RepositoryPath = "C:\Users\nemo\lean\Lean.Brokerages.QMT",
    [string]$LogRootPath = "C:\Users\nemo\lean_logs",
    [string]$SmokeTestLivePath = "C:\Users\nemo\lean_project\china_smoke_test\live",
    [string]$TopGainerLivePath = "C:\Users\nemo\lean_project\a top gainer\live",
    [string]$NginxInstallPath = "C:\Users\nemo\tools\nginx-1.30.4",
    [string]$NginxArchivePath = "",
    [string]$AllowedRemoteAddress = "192.168.50.0/24",
    [int]$Port = 8000
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$firewallRuleName = "Qmt-Live-Logs-In-TCP"
$scheduledTaskName = "QmtLiveLogs"
$expectedNginxArchiveSha256 = "159294214d403f34f0bb4ae598801ab1f6a0d8c8da707f8f08748e294a222a01"
$repositoryNginxConfigurationPath = Join-Path $RepositoryPath "deploy\nginx\nginx.conf"
$installedNginxConfigurationPath = Join-Path $NginxInstallPath "conf\qmt-live-logs.conf"
$nginxExecutable = Join-Path $NginxInstallPath "nginx.exe"
$nginxPrefix = $NginxInstallPath.Replace("\", "/") + "/"

function Write-LiveLogServerLog {
    param([string]$Message)

    [Console]::Error.WriteLine("[qmt-live-logs] $Message")
}

function Set-CanonicalLogDirectory {
    param(
        [string]$SourcePath,
        [string]$CanonicalPath
    )

    New-Item -ItemType Directory -Path (Split-Path -Parent $SourcePath) -Force | Out-Null
    $sourceItem = Get-Item -LiteralPath $SourcePath -Force -ErrorAction SilentlyContinue
    $canonicalItem = Get-Item -LiteralPath $CanonicalPath -Force -ErrorAction SilentlyContinue

    if ($sourceItem -and $sourceItem.LinkType -eq "SymbolicLink") {
        if (-not [string]::Equals([string]$sourceItem.Target, $CanonicalPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The project log link points to '$($sourceItem.Target)' instead of '$CanonicalPath': $SourcePath"
        }
        if (-not $canonicalItem -or -not $canonicalItem.PSIsContainer -or $canonicalItem.LinkType) {
            throw "The canonical log directory is missing or is not a physical directory: $CanonicalPath"
        }
        return
    }

    if ($canonicalItem -and $canonicalItem.LinkType -eq "SymbolicLink") {
        if (-not [string]::Equals([string]$canonicalItem.Target, $SourcePath, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The legacy log link points to '$($canonicalItem.Target)' instead of '$SourcePath': $CanonicalPath"
        }
        & cmd.exe /d /c "rmdir `"$CanonicalPath`""
        if ($LASTEXITCODE -ne 0) {
            throw "Could not remove the legacy log link: $CanonicalPath"
        }
        $canonicalItem = $null
    }

    if ($canonicalItem) {
        throw "The canonical log path already exists while the project path is not linked: $CanonicalPath"
    }

    if ($sourceItem) {
        if (-not $sourceItem.PSIsContainer -or $sourceItem.LinkType) {
            throw "The project log path is not a physical directory: $SourcePath"
        }
        Move-Item -LiteralPath $SourcePath -Destination $CanonicalPath
        Write-LiveLogServerLog "stage=migrate status=ok source=$SourcePath destination=$CanonicalPath"
    }
    else {
        New-Item -ItemType Directory -Path $CanonicalPath -Force | Out-Null
    }

    New-Item -ItemType SymbolicLink -Path $SourcePath -Target $CanonicalPath | Out-Null
    Write-LiveLogServerLog "stage=link status=created link=$SourcePath target=$CanonicalPath"
}

function Stop-NativeNginx {
    $scheduledTask = Get-ScheduledTask -TaskName $scheduledTaskName -ErrorAction SilentlyContinue
    if ($scheduledTask) {
        Stop-ScheduledTask -TaskName $scheduledTaskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $scheduledTaskName -Confirm:$false
    }

    Get-Process nginx -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($NginxInstallPath, [System.StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force
}

function Install-NativeNginx {
    if (Test-Path -LiteralPath $nginxExecutable) {
        return
    }
    if (Test-Path -LiteralPath $NginxInstallPath) {
        throw "The Nginx install directory is incomplete: $NginxInstallPath"
    }
    if (-not $NginxArchivePath -or -not (Test-Path -LiteralPath $NginxArchivePath)) {
        throw "The official Windows Nginx archive is required for first install: $NginxArchivePath"
    }
    $archiveSha256 = (Get-FileHash -LiteralPath $NginxArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($archiveSha256 -ne $expectedNginxArchiveSha256) {
        throw "The Nginx archive SHA-256 does not match the pinned official nginx-1.30.4.zip."
    }

    $toolsDirectory = Split-Path -Parent $NginxInstallPath
    New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
    Expand-Archive -LiteralPath $NginxArchivePath -DestinationPath $toolsDirectory
    if (-not (Test-Path -LiteralPath $nginxExecutable)) {
        throw "The archive did not install nginx.exe at $nginxExecutable"
    }
    Write-LiveLogServerLog "stage=install status=ok version=1.30.4 path=$NginxInstallPath sha256=$archiveSha256"
}

if ($Action -eq "Status") {
    $scheduledTask = Get-ScheduledTask -TaskName $scheduledTaskName -ErrorAction SilentlyContinue
    $nginxProcesses = @(Get-Process nginx -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($NginxInstallPath, [System.StringComparison]::OrdinalIgnoreCase)
    })
    $httpStatusCode = 0
    try {
        $httpStatusCode = [int](Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$Port/" -TimeoutSec 2).StatusCode
    }
    catch {
    }
    $status = if ($scheduledTask -and $nginxProcesses.Count -gt 0 -and $httpStatusCode -eq 200) { "running" } else { "stopped" }
    Write-LiveLogServerLog "stage=status status=$status task_state=$($scheduledTask.State) processes=$($nginxProcesses.Count) http_status=$httpStatusCode url=http://192.168.50.135:$Port/"
    exit 0
}

if ($Action -eq "Stop") {
    Stop-NativeNginx
    Get-NetFirewallRule -Name $firewallRuleName -ErrorAction SilentlyContinue | Disable-NetFirewallRule
    Write-LiveLogServerLog "stage=stop status=ok task=$scheduledTaskName"
    exit 0
}

if (-not (Test-Path -LiteralPath $repositoryNginxConfigurationPath)) {
    throw "The Nginx configuration is missing: $repositoryNginxConfigurationPath"
}

Install-NativeNginx
Stop-NativeNginx

New-Item -ItemType Directory -Path $LogRootPath -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $LogRootPath "broker") -Force | Out-Null
$logSources = @(
    @{ Name = "smoke_test"; Path = $SmokeTestLivePath },
    @{ Name = "a-top-gainer"; Path = $TopGainerLivePath }
)
foreach ($logSource in $logSources) {
    Set-CanonicalLogDirectory `
        -SourcePath $logSource.Path `
        -CanonicalPath (Join-Path $LogRootPath $logSource.Name)
}

Copy-Item -LiteralPath $repositoryNginxConfigurationPath -Destination $installedNginxConfigurationPath -Force
$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$configurationTestOutput = & $nginxExecutable -t -p $nginxPrefix -c "conf\qmt-live-logs.conf" 2>&1
$configurationTestExitCode = $LASTEXITCODE
$ErrorActionPreference = $previousErrorActionPreference
if ($configurationTestExitCode -ne 0) {
    $configurationTestOutput | ForEach-Object { Write-LiveLogServerLog "nginx=$_" }
    throw "The native Nginx configuration is invalid."
}
Write-LiveLogServerLog "stage=config status=ok path=$installedNginxConfigurationPath"

$portListener = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue
if ($portListener) {
    $ownerProcess = Get-Process -Id $portListener[0].OwningProcess -ErrorAction SilentlyContinue
    throw "Windows port $Port is already in use by process $($portListener[0].OwningProcess) ($($ownerProcess.ProcessName))."
}

$existingFirewallRule = Get-NetFirewallRule -Name $firewallRuleName -ErrorAction SilentlyContinue
if ($existingFirewallRule) {
    $existingFirewallRule | Remove-NetFirewallRule
}
New-NetFirewallRule `
    -Name $firewallRuleName `
    -DisplayName "QMT live logs (native Nginx)" `
    -Enabled True `
    -Direction Inbound `
    -Protocol TCP `
    -Action Allow `
    -LocalPort $Port `
    -RemoteAddress $AllowedRemoteAddress | Out-Null

$scheduledTaskAction = New-ScheduledTaskAction `
    -Execute $nginxExecutable `
    -Argument "-p `"$nginxPrefix`" -c `"conf\qmt-live-logs.conf`"" `
    -WorkingDirectory $NginxInstallPath
$scheduledTaskTrigger = New-ScheduledTaskTrigger -AtStartup
$scheduledTaskPrincipal = New-ScheduledTaskPrincipal `
    -UserId "SYSTEM" `
    -LogonType ServiceAccount `
    -RunLevel Highest
$scheduledTaskSettings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -MultipleInstances IgnoreNew
Register-ScheduledTask `
    -TaskName $scheduledTaskName `
    -Action $scheduledTaskAction `
    -Trigger $scheduledTaskTrigger `
    -Principal $scheduledTaskPrincipal `
    -Settings $scheduledTaskSettings | Out-Null
Start-ScheduledTask -TaskName $scheduledTaskName

$verificationPaths = @("", "smoke_test/", "broker/", "a-top-gainer/", "e2e/")
foreach ($verificationPath in $verificationPaths) {
    $verificationDeadline = (Get-Date).AddSeconds(15)
    $httpStatusCode = 0
    while ($httpStatusCode -ne 200 -and (Get-Date) -lt $verificationDeadline) {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$Port/$verificationPath" -TimeoutSec 2
            $httpStatusCode = [int]$response.StatusCode
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }
    if ($httpStatusCode -ne 200) {
        throw "Native Nginx did not serve '/$verificationPath' on Windows port $Port."
    }
}

Write-LiveLogServerLog "stage=start status=ok runtime=native-windows task=$scheduledTaskName root=$LogRootPath remote_address=$AllowedRemoteAddress url=http://192.168.50.135:$Port/"
