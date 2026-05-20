Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "   AudioWin Standalone EXE Release Builder" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

# Clean previous build artifacts
Write-Host "[1/3] Cleaning up old build caches..." -ForegroundColor Yellow
dotnet clean .\AudioWin\AudioWin.csproj -c Release

if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Clean failed." -ForegroundColor Red
    Exit 1
}

# Publish optimized standalone single-file EXE
Write-Host "[2/3] Compiling high-performance native standalone EXE..." -ForegroundColor Yellow
dotnet publish .\AudioWin\AudioWin.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:PublishReadyToRun=true /p:IncludeNativeLibrariesForSelfExtract=true

if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Standalone compilation failed." -ForegroundColor Red
    Exit 1
}

$OutputDir = ".\AudioWin\bin\Release\net10.0-windows\win-x64\publish"

Write-Host ""
Write-Host "==========================================" -ForegroundColor Green
Write-Host " SUCCESS! Standalone Release Compiled Successfully!" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Your standalone release file is located at:" -ForegroundColor White
Write-Host " -> $OutputDir\AudioWin.exe" -ForegroundColor Cyan
Write-Host ""
Write-Host "[3/3] Opening output directory in Windows Explorer..." -ForegroundColor Yellow

# Open the output folder in Explorer for easy drag-and-drop to GitHub Releases
explorer.exe (Resolve-Path $OutputDir).Path
