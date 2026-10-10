# SPDX-License-Identifier: GPL-2.0-or-later
# Copyright (c) 2026 いしかず
param([string]$DotnetPath = 'dotnet', [switch]$Publish)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    & $DotnetPath build RtlCast.sln -c Release --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $DotnetPath run --project tests/RtlCast.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'TCP tests failed.' }
    & $DotnetPath run --project tests/RtlCast.GuiSmoke -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'GUI smoke test failed.' }
    if ($Publish) {
        $publishDirectory = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/RTL-TCP-Cast-win-x64'))
        $artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (!$publishDirectory.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Publish output must stay under artifacts: $publishDirectory"
        }
        if (Test-Path -LiteralPath $publishDirectory) {
            $existingPublishDirectory = Get-Item -LiteralPath $publishDirectory -Force
            if ($existingPublishDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Publish output must not be a reparse point: $publishDirectory"
            }
            Remove-Item -LiteralPath $publishDirectory -Recurse -Force
        }
        & $DotnetPath publish src/RtlCast.App -c Release -r win-x64 --self-contained true -o $publishDirectory --disable-build-servers -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        $unexpectedRuntimeFiles = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File | Where-Object {
            $_.Extension -in @('.dll', '.pdb') -or $_.Name -match '\.(deps|runtimeconfig)\.json$'
        })
        if ($unexpectedRuntimeFiles.Count -gt 0) {
            throw "Single-file publish left separate runtime files: $($unexpectedRuntimeFiles.Name -join ', ')"
        }
        Copy-Item -LiteralPath LICENSE -Destination (Join-Path $publishDirectory 'LICENSE.txt')
        Copy-Item -LiteralPath docs/USER-GUIDE.md -Destination (Join-Path $publishDirectory 'USER-GUIDE.md')
        & "$PSScriptRoot/copy-license-notices.ps1" -DotnetPath $DotnetPath -PublishDirectory $publishDirectory
        # Refuse packaging external RTL-SDR files, including leftovers from an earlier publish.
        $externalNames = @(Get-ChildItem -LiteralPath 'C:\tools\rtl-sdr' -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
        $externalFiles = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File | Where-Object {
            $_.Name -in $externalNames -or $_.Name -match '^(rtl_.*\.exe|lib?rtlsdr.*|rtlsdr.*|libusb.*|pthread.*)$'
        })
        if ($externalFiles.Count -gt 0) { throw "External RTL-SDR files must not be packaged: $($externalFiles.Name -join ', ')" }
        & "$PSScriptRoot/create-source-archive.ps1"
        Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath artifacts/RTL-TCP-Cast-win-x64.zip -Force
        Get-FileHash artifacts/RTL-TCP-Cast-win-x64.zip -Algorithm SHA256 | Format-List
    }
}
finally { Pop-Location }
