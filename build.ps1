$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
dotnet build (Join-Path $repoRoot "CodexKeeper.Windows.sln") --configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
