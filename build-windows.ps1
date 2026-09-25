# DirectPackageInstaller local Windows build.
# Usage:  powershell -ExecutionPolicy Bypass -File build-windows.ps1 [-Runtimes win-x64,win-arm64] [-SelfContained]
# Output: Release\Windows-X64.zip (etc.), same names as Build.cmd / CI.
# Installs a .NET 8 SDK into .dotnet\ (per-user, no admin) when none is found.
param(
    [string[]]$Runtimes = @('win-x64'),
    [switch]$SelfContained
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# --- .NET 8 SDK ---
$dotnet = Join-Path $PSScriptRoot '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $sys = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if ($sys -and (& $sys --list-sdks | Select-String '^8\.')) { $dotnet = $sys }
}
if (-not (Test-Path $dotnet)) {
    Write-Host 'Installing .NET 8 SDK into .dotnet\ ...'
    $script = Join-Path $env:TEMP 'dotnet-install.ps1'
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $script -UseBasicParsing
    & $script -Channel 8.0 -InstallDir (Join-Path $PSScriptRoot '.dotnet') -NoPath
    $dotnet = Join-Path $PSScriptRoot '.dotnet\dotnet.exe'
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# --- submodules (DHCPServer, HttpServerLite, LibOrbisPkg) ---
if (-not (Test-Path 'LibOrbisPkg\LibOrbisPkg')) {
    git submodule update --init --recursive
    if ($LASTEXITCODE -ne 0) { throw 'git submodule update FAILED' }
}

$names = @{ 'win-x64' = 'Windows-X64'; 'win-x86' = 'Windows-X86'; 'win-arm' = 'Windows-ARM'; 'win-arm64' = 'Windows-ARM64' }
$project = 'DirectPackageInstaller\DirectPackageInstaller.Desktop\DirectPackageInstaller.Desktop.csproj'
New-Item -ItemType Directory -Force Release | Out-Null

foreach ($rid in $Runtimes) {
    if (-not $names.ContainsKey($rid)) { throw "Unsupported runtime '$rid' (use $($names.Keys -join ', '))" }
    Write-Host "== Building $rid"
    $out = "DirectPackageInstaller\DirectPackageInstaller.Desktop\bin\Release\net8.0\$rid\publish"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    & $dotnet publish $project -c Release -r $rid --self-contained:$($SelfContained.IsPresent) --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Publish FAILED for $rid" }

    # Same as Build.cmd's WINPublish: flip the exe to the GUI subsystem so no console window opens.
    # NSubsys targets net6; roll forward to whatever runtime the SDK ships.
    $env:DOTNET_ROLL_FORWARD = 'Major'
    & $dotnet Files\NSubsys.Tasks.dll "$out\DirectPackageInstaller.Desktop.exe"
    $rc = $LASTEXITCODE
    Remove-Item Env:DOTNET_ROLL_FORWARD
    if ($rc -ne 0) { throw "NSubsys FAILED for $rid" }

    $zip = "Release\$($names[$rid]).zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive "$out\*" $zip
    Write-Host "Built: $zip"
}
