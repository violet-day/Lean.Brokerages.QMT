param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Start", "Stop", "Status")]
    [string]$Action,
    [string]$RepositoryPath = "C:\Users\nemo\lean\Lean.Brokerages.QMT",
    [string]$LiveOutputPath = "C:\Users\nemo\lean_project\.qmt-live-smoke-output",
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
New-Item -ItemType Directory -Path $LiveOutputPath -Force | Out-Null

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
    "--mount", "type=bind,source=$LiveOutputPath,target=/usr/share/nginx/html,readonly",
    "--mount", "type=bind,source=$nginxConfigurationPath,target=/etc/nginx/conf.d/default.conf,readonly",
    $NginxImage
)
$containerId = & $dockerExecutable @dockerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Could not start the Nginx live log container."
}
Write-LiveLogServerLog "stage=container status=started container=$ContainerName id=$containerId"

$verificationDeadline = (Get-Date).AddSeconds(15)
$httpStatusCode = 0
while ($httpStatusCode -ne 200 -and (Get-Date) -lt $verificationDeadline) {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$Port/" -TimeoutSec 2
        $httpStatusCode = [int]$response.StatusCode
    }
    catch {
        Start-Sleep -Milliseconds 250
    }
}
if ($httpStatusCode -ne 200) {
    & $dockerExecutable logs $ContainerName 2>&1 | ForEach-Object { Write-LiveLogServerLog "nginx=$_" }
    throw "Nginx did not become ready on Windows port $Port."
}

Write-LiveLogServerLog "stage=start status=ok container=$ContainerName image=$NginxImage source=$LiveOutputPath remote_address=$AllowedRemoteAddress url=http://192.168.50.135:$Port/"
