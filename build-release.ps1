param(
    [switch]$SelfContained = $true,
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

Write-Host "==> Compilando y publicando DriverVault ($Runtime)..." -ForegroundColor White

$outDir = Join-Path $PSScriptRoot "release\$Runtime"
if (Test-Path $outDir) {
    Remove-Item $outDir -Recurse -Force
}

$scFlag = if ($SelfContained) { "true" } else { "false" }

dotnet publish "$PSScriptRoot\src\DriverVault\DriverVault.csproj" `
    -c Release `
    -r $Runtime `
    --self-contained $scFlag `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $outDir

Write-Host ""
Write-Host "Listo. Ejecutable portable generado en:" -ForegroundColor Green
Write-Host "  $outDir\DriverVault.exe" -ForegroundColor Cyan
