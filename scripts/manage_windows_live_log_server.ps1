param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Start", "Stop", "Status")]
    [string]$Action,
    [string]$RepositoryPath = "C:\Users\nemo\lean\Lean.Brokerages.QMT",
    [string]$LogRootPath = "C:\Users\nemo\lean_logs",
    [string]$SmokeTestLivePath = "C:\Users\nemo\lean_project\china_smoke_test\live",
    [string]$TopGainerLivePath = "C:\Users\nemo\lean_project\a top gainer\live",
    [string]$ContainerName = "qmt-live-logs",
    [string]$NginxImage = "nginx:alpine",
    [string]$AllowedRemoteAddress = "192.168.50.0/24",
    [int]$Port = 8000
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$firewallRuleName = "Qmt-Live-Logs-In-TCP"
$nginxConfigurationPath = Join-Path $RepositoryPath "deploy\nginx\default.conf"
$dockerExecutable = (Get-Command docker.exe -ErrorAction Stop).Source

function Write-LiveLogServerLog {
    param([string]$Message)

    [Console]::Error.WriteLine("[qmt-live-logs] $Message")
}

function Test-ContainerExists {
    $matchingContainerNames = @(& $dockerExecutable ps --all --filter "name=^/$ContainerName$" --format "{{.Names}}")
    if ($LASTEXITCODE -ne 0) {
        throw "Could not query Docker containers."
    }
    return $matchingContainerNames -contains $ContainerName
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

& $dockerExecutable version --format "{{.Server.Os}}/{{.Server.Arch}}" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Docker Desktop is not ready."
}

if ($Action -eq "Status") {
    if (-not (Test-ContainerExists)) {
        Write-LiveLogServerLog "stage=status status=stopped container=$ContainerName url=http://192.168.50.135:$Port/"
        exit 0
    }

    $containerStatus = & $dockerExecutable inspect --format "{{.State.Status}}" $ContainerName
    $publishedPorts = & $dockerExecutable port $ContainerName 80 2>$null
    Write-LiveLogServerLog "stage=status status=$containerStatus container=$ContainerName ports=$publishedPorts url=http://192.168.50.135:$Port/"
    exit 0
}

if ($Action -eq "Stop") {
    if (Test-ContainerExists) {
        & $dockerExecutable rm --force $ContainerName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Could not remove the Nginx live log container."
        }
    }
    Get-NetFirewallRule -Name $firewallRuleName -ErrorAction SilentlyContinue | Disable-NetFirewallRule
    Write-LiveLogServerLog "stage=stop status=ok container=$ContainerName"
    exit 0
}

if (-not (Test-Path -LiteralPath $nginxConfigurationPath)) {
    throw "The Nginx configuration is missing: $nginxConfigurationPath"
}

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

if (Test-ContainerExists) {
    & $dockerExecutable rm --force $ContainerName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not replace the existing Nginx live log container."
    }
}

$portListener = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue
if ($portListener) {
    throw "Windows port $Port is already in use by process $($portListener[0].OwningProcess)."
}

$matchingImageIds = @(& $dockerExecutable image ls --quiet $NginxImage)
if ($LASTEXITCODE -ne 0) {
    throw "Could not query Docker images."
}
if ($matchingImageIds.Count -eq 0) {
    Write-LiveLogServerLog "stage=image status=pull image=$NginxImage"
    & $dockerExecutable pull $NginxImage
    if ($LASTEXITCODE -ne 0) {
        throw "Could not pull the Nginx image: $NginxImage"
    }
}

$existingFirewallRule = Get-NetFirewallRule -Name $firewallRuleName -ErrorAction SilentlyContinue
if ($existingFirewallRule) {
    $existingFirewallRule | Remove-NetFirewallRule
}
New-NetFirewallRule `
    -Name $firewallRuleName `
    -DisplayName "QMT live logs (Nginx)" `
    -Enabled True `
    -Direction Inbound `
    -Protocol TCP `
    -Action Allow `
    -LocalPort $Port `
    -RemoteAddress $AllowedRemoteAddress | Out-Null

$dockerArguments = @(
    "run",
    "--detach",
    "--name", $ContainerName,
    "--restart", "unless-stopped",
    "--publish", "${Port}:80",
    "--mount", "type=bind,source=$LogRootPath,target=/usr/share/nginx/html,readonly",
    "--mount", "type=bind,source=$nginxConfigurationPath,target=/etc/nginx/conf.d/default.conf,readonly"
)
$dockerArguments += $NginxImage
$containerId = & $dockerExecutable @dockerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Could not start the Nginx live log container."
}
Write-LiveLogServerLog "stage=container status=started container=$ContainerName id=$containerId"

$verificationPaths = @("", "smoke_test/", "broker/", "a-top-gainer/")
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
        & $dockerExecutable logs $ContainerName 2>&1 | ForEach-Object { Write-LiveLogServerLog "nginx=$_" }
        throw "Nginx did not serve '/$verificationPath' on Windows port $Port."
    }
}

Write-LiveLogServerLog "stage=start status=ok container=$ContainerName image=$NginxImage root=$LogRootPath remote_address=$AllowedRemoteAddress url=http://192.168.50.135:$Port/"
