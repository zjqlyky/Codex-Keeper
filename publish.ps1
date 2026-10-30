param(
    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $repoRoot "windows\CodexKeeper.Windows\CodexKeeper.Windows.csproj"
$destination = Join-Path $repoRoot ".publish\$Runtime"

dotnet publish $project `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    --output $destination

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$projectFile = Join-Path $repoRoot "windows\CodexKeeper.Windows\CodexKeeper.Windows.csproj"
$version = [xml](Get-Content -LiteralPath $projectFile -Raw)
$version = $version.Project.PropertyGroup.Version
$mainFile = Join-Path $repoRoot "CodexKeeper.Windows.exe"
$versionedFile = Join-Path $repoRoot "CodexKeeper.Windows.$version.exe"
Copy-Item -LiteralPath (Join-Path $destination "CodexKeeper.Windows.exe") -Destination $versionedFile -Force
$running = Get-Process -Name "CodexKeeper.Windows" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and [StringComparer]::OrdinalIgnoreCase.Equals($_.Path, $mainFile) }
if (-not $running) { Copy-Item -LiteralPath $versionedFile -Destination $mainFile -Force }
Write-Host "Published Codex Keeper to $destination"
Write-Host "Launch $versionedFile"
if ($running) { Write-Host "The current CodexKeeper.Windows.exe is still running; close its tray menu before replacing the unversioned copy." }
