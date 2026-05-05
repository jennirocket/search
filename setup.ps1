# FileSearch Build and Setup Script

$ErrorActionPreference = "Stop"

Write-Host "FileSearch - Setup Script" -ForegroundColor Cyan
Write-Host "========================" -ForegroundColor Cyan
Write-Host ""

# Check if .NET SDK is installed
Write-Host "Checking .NET SDK..." -ForegroundColor Yellow
$dotnetVersion = dotnet --version 2>&1
if ($?) {
    Write-Host "OK .NET SDK found: $dotnetVersion" -ForegroundColor Green
    Write-Host ""
} else {
    Write-Host "ERROR .NET SDK not found. Please install from https://dotnet.microsoft.com/download" -ForegroundColor Red
    exit 1
}

# Restore and build
Write-Host "Building FileSearch..." -ForegroundColor Yellow
dotnet restore
if (-not $?) {
    Write-Host "ERROR Restore failed" -ForegroundColor Red
    exit 1
}

dotnet build -c Release
if (-not $?) {
    Write-Host "ERROR Build failed" -ForegroundColor Red
    exit 1
}

Write-Host "OK Build successful" -ForegroundColor Green
Write-Host ""

# Publish as standalone
Write-Host "Publishing standalone executable..." -ForegroundColor Yellow
$publishPath = "$env:LOCALAPPDATA\FileSearch"
dotnet publish -c Release -o $publishPath
if (-not $?) {
    Write-Host "ERROR Publish failed" -ForegroundColor Red
    exit 1
}

Write-Host "OK Published to $publishPath" -ForegroundColor Green
Write-Host ""

# Add to PATH
Write-Host "Adding to system PATH..." -ForegroundColor Yellow
$currentPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($currentPath -notcontains $publishPath) {
    [Environment]::SetEnvironmentVariable("Path", "$currentPath;$publishPath", "User")
    Write-Host "OK Added to PATH" -ForegroundColor Green
    Write-Host ""
    Write-Host "Note: You may need to restart your terminal for PATH changes to take effect" -ForegroundColor Yellow
    Write-Host ""
} else {
    Write-Host "OK Already in PATH" -ForegroundColor Green
    Write-Host ""
}

# Initial index
Write-Host "Building initial file index (this may take a few minutes)..." -ForegroundColor Yellow
$exePath = Join-Path $publishPath "FileSearch.exe"
& $exePath --update-index

Write-Host ""
Write-Host "OK Setup complete!" -ForegroundColor Green
Write-Host ""
Write-Host "You can now use 'search' from any terminal:" -ForegroundColor Cyan
Write-Host "  search config.json" -ForegroundColor Gray
Write-Host "  search '*.txt'" -ForegroundColor Gray
Write-Host "  search --help" -ForegroundColor Gray