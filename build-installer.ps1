param(
    [string]$Version
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $repoRoot "windows\CodexKeeper.Windows\CodexKeeper.Windows.csproj"
$project = [xml](Get-Content -LiteralPath $projectFile -Raw)
if ([String]::IsNullOrWhiteSpace($Version)) { $Version = $project.Project.PropertyGroup.Version }
if ([String]::IsNullOrWhiteSpace($Version)) { throw "Could not read the application version." }

$publishScript = Join-Path $repoRoot "publish.ps1"
& $publishScript -Runtime "win-x64"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$compiler = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if ($compiler) {
    $compilerPath = $compiler.Source
} else {
    $compilerPath = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $compilerPath) { throw "Inno Setup 6 was not found. Install it, then run this script again." }

$sourceDir = Join-Path $repoRoot ".publish\win-x64"
$outputDir = Join-Path $repoRoot "artifacts"
$scriptFile = Join-Path $repoRoot "installer\CodexKeeper.iss"
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
& $compilerPath "/DAppVersion=$Version" "/DSourceDir=$sourceDir" "/DProjectRoot=$repoRoot" "/DOutputDir=$outputDir" $scriptFile
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$installerPath = Join-Path $outputDir "CodexKeeper-Setup-$Version-win-x64.exe"
if (-not (Test-Path -LiteralPath $installerPath)) { throw "Installer output was not created: $installerPath" }
$checksum = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = "$installerPath.sha256"
Set-Content -LiteralPath $checksumPath -Value "$checksum *$(Split-Path -Leaf $installerPath)" -Encoding ascii
Write-Host "Installer: $installerPath"
Write-Host "SHA-256:  $checksumPath"
