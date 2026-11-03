$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'DC07Pro.Wpf.csproj'
$publish = Join-Path $PSScriptRoot 'publish'
$iss = Join-Path $PSScriptRoot 'installer\DAC-Control-Center.iss'

Write-Host 'Publishing DAC Control Center (self-contained Windows x64)...' -ForegroundColor Cyan
dotnet publish $project -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$compilerCandidates = @(
  "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$iscc = $compilerCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
  Write-Host 'Publish succeeded. Inno Setup 6 was not found.' -ForegroundColor Yellow
  Write-Host 'Install Inno Setup 6, then run this script again to create the Setup.exe.'
  exit 0
}

Write-Host 'Compiling installer...' -ForegroundColor Cyan
& $iscc $iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
Write-Host "Installer created: $(Join-Path $PSScriptRoot 'installer\Output\DAC-Control-Center-Setup-9.7.4.exe')" -ForegroundColor Green
