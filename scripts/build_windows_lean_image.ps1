param(
    [string]$LeanWorkspaceRoot = "C:\Users\nemo\lean-net10",
    [string]$ImageTag = "qmt-20260813-d72852f25-worktree"
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

$dockerExecutable = (Get-Command docker.exe -ErrorAction Stop).Source
$dotnetExecutable = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
$launcherProjectPath = Join-Path $LeanWorkspaceRoot "Lean\Launcher\QuantConnect.Lean.Launcher.csproj"
$launcherOutputPath = Join-Path $LeanWorkspaceRoot "Lean\Launcher\bin\Debug"
$qmtAssemblyPath = Join-Path $launcherOutputPath "QuantConnect.Brokerages.Qmt.dll"
$imageName = "lean-cli/engine:$ImageTag"

if (-not (Test-Path -LiteralPath $dotnetExecutable)) {
    throw ".NET 10 is missing: $dotnetExecutable"
}
$dotnetVersion = & $dotnetExecutable --version
if (-not $dotnetVersion.StartsWith("10.")) {
    throw "Expected .NET 10, found $dotnetVersion."
}

& $dockerExecutable version --format "{{.Server.Os}}/{{.Server.Arch}}" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Docker Desktop is not ready. Start Docker Desktop and retry."
}
Write-DeploymentLog "stage=dotnet-build status=start sdk=$dotnetVersion project=$launcherProjectPath"
& $dotnetExecutable build $launcherProjectPath --configuration Debug --nologo --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    throw "LEAN Launcher build failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $qmtAssemblyPath)) {
    throw "The Launcher output does not contain $qmtAssemblyPath"
}
$launcherDependenciesPath = Join-Path $launcherOutputPath "QuantConnect.Lean.Launcher.deps.json"
if (-not (Select-String -LiteralPath $launcherDependenciesPath -SimpleMatch "QuantConnect.Brokerages.Qmt" -Quiet)) {
    throw "The Launcher dependency manifest does not contain QuantConnect.Brokerages.Qmt."
}
Write-DeploymentLog "stage=dotnet-build status=ok qmt_assembly=$qmtAssemblyPath"

$foundationImage = "quantconnect/lean:foundation"
Write-DeploymentLog "stage=image-build status=start image=$imageName base=$foundationImage"
$temporaryDockerConfigurationDirectory = Join-Path $env:TEMP "qmt-docker-public-registry"
New-Item -ItemType Directory -Path $temporaryDockerConfigurationDirectory -Force | Out-Null
[IO.File]::WriteAllText(
    (Join-Path $temporaryDockerConfigurationDirectory "config.json"),
    '{"auths":{}}',
    $utf8Encoding
)
try {
    # Docker Desktop's credential helper requires an interactive Windows logon
    # token. This build only pulls a public image, so use an isolated empty
    # client configuration and leave the user's Docker credentials untouched.
    $previousDockerConfiguration = $env:DOCKER_CONFIG
    $env:DOCKER_CONFIG = $temporaryDockerConfigurationDirectory
    & $dockerExecutable build --file (Join-Path $LeanWorkspaceRoot "Lean\Dockerfile") --build-arg BUILDKIT_INLINE_CACHE=1 --tag $imageName $LeanWorkspaceRoot
    $imageBuildExitCode = $LASTEXITCODE
}
finally {
    $env:DOCKER_CONFIG = $previousDockerConfiguration
    Remove-Item -LiteralPath $temporaryDockerConfigurationDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
if ($imageBuildExitCode -ne 0) {
    throw "Docker image build failed with exit code $imageBuildExitCode."
}

$containerChecks = @(
    "test -f /Lean/Launcher/bin/Debug/QuantConnect.Brokerages.Qmt.dll",
    "dotnet --list-runtimes",
    "grep -q QuantConnect.Brokerages.Qmt /Lean/Launcher/bin/Debug/QuantConnect.Lean.Launcher.deps.json"
) -join " && "
& $dockerExecutable run --rm --entrypoint /bin/bash $imageName -lc $containerChecks
if ($LASTEXITCODE -ne 0) {
    throw "The QMT image content check failed with exit code $LASTEXITCODE."
}
$imageIdentifier = & $dockerExecutable image inspect --format "{{.Id}}" $imageName
Write-DeploymentLog "stage=image-build status=ok image=$imageName image_id=$imageIdentifier"
