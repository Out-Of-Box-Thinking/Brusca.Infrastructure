# Pack and publish to the shared local NuGet feed
param(
    [string]$Version = "1.0.0",
    [string]$Feed = "..\nupkgs"
)

$ErrorActionPreference = "Stop"

# Ensure the shared local feed directory exists
New-Item -ItemType Directory -Force -Path $Feed | Out-Null

dotnet pack Brusca.Infrastructure/Brusca.Infrastructure.csproj `
    --configuration Release `
    /p:Version=$Version `
    --output $Feed

Write-Host ""
Write-Host "Packed Brusca.Infrastructure $Version → $Feed"
