Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "  AudioWin 2.0 Standalone EXE & MSI Builder"   -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host ""

$LocalDotnet = ".\.dotnet\dotnet.exe"
$WixDll = "C:\Users\123\.dotnet\tools\.store\wix\7.0.0\wix\7.0.0\tools\net8.0\any\wix.dll"

Write-Host "[1/4] Cleaning old build output..." -ForegroundColor Yellow
& $LocalDotnet clean .\AudioWin.csproj -c Release | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "Clean failed." -ForegroundColor Red; Exit 1 }

Write-Host "[2/4] Publishing self-contained single-file build..." -ForegroundColor Yellow
& $LocalDotnet publish .\AudioWin.csproj -c Release -r win-x64 `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:PublishReadyToRun=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true

if ($LASTEXITCODE -ne 0) { Write-Host "Build failed." -ForegroundColor Red; Exit 1 }

$OutputDir = ".\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"
if (-not (Test-Path $OutputDir)) {
    # Fall back to whatever publish folder actually got created.
    $found = Get-ChildItem -Path .\bin\Release -Recurse -Directory -Filter publish |
             Select-Object -First 1
    if ($found) { $OutputDir = $found.FullName }
}

$AbsOutputDir = (Resolve-Path $OutputDir).Path
$ExePath = Join-Path $AbsOutputDir "AudioWin.exe"
$MsiPath = Join-Path $AbsOutputDir "AudioWin.msi"

Write-Host "[3/4] Building MSI installer using WiX..." -ForegroundColor Yellow
& $LocalDotnet $WixDll build .\AudioWin.wxs -o $MsiPath
if ($LASTEXITCODE -ne 0) { Write-Host "MSI build failed." -ForegroundColor Red; Exit 1 }

Write-Host ""
Write-Host "==========================================" -ForegroundColor Green
Write-Host " Build succeeded"                          -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
Write-Host ""
Write-Host "EXE Output: $ExePath" -ForegroundColor Cyan
Write-Host "MSI Output: $MsiPath" -ForegroundColor Cyan
Write-Host ""

Write-Host "[4/4] Opening the output folder..." -ForegroundColor Yellow
if (Test-Path $OutputDir) { explorer.exe $AbsOutputDir }
