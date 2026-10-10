# SPDX-License-Identifier: GPL-2.0-or-later
# Copyright (c) 2026 いしかず
param(
    [string]$DotnetPath = 'dotnet',
    [string]$PublishDirectory = 'artifacts/RTL-TCP-Cast-win-x64'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $runtimeConfigPath = Join-Path $PublishDirectory 'RTL-TCP-Cast.runtimeconfig.json'
    if (!(Test-Path -LiteralPath $runtimeConfigPath)) {
        # Single-file publish bundles the runtime config into RTL-TCP-Cast.exe.
        $runtimeConfigPath = Join-Path $repoRoot 'src/RtlCast.App/bin/Release/net10.0-windows/win-x64/RTL-TCP-Cast.runtimeconfig.json'
    }
    if (!(Test-Path -LiteralPath $runtimeConfigPath)) { throw "Runtime config not found: $runtimeConfigPath" }
    $runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
    $assets = Get-Content -LiteralPath 'src/RtlCast.App/obj/project.assets.json' -Raw | ConvertFrom-Json
    $noticeDirectory = Join-Path $PublishDirectory 'licenses'
    New-Item -ItemType Directory -Path $noticeDirectory -Force | Out-Null
    foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
        $packageName = "$($framework.name.ToLowerInvariant()).runtime.win-x64"
        $packageDirectory = $null
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $folder "$packageName/$($framework.version)"
            if (Test-Path -LiteralPath $candidate -PathType Container) { $packageDirectory = $candidate; break }
        }
        if (!$packageDirectory) { throw "Runtime package not found: $packageName $($framework.version)" }
        $license = Get-ChildItem -LiteralPath $packageDirectory -File | Where-Object { $_.Name -match '^LICENSE(\.TXT)?$' } | Select-Object -First 1
        if (!$license) { throw "Runtime license not found: $packageDirectory" }
        Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $noticeDirectory "$($framework.name)-LICENSE.txt")
        $notices = Join-Path $packageDirectory 'THIRD-PARTY-NOTICES.TXT'
        if (!(Test-Path -LiteralPath $notices)) {
            # WindowsDesktop ships its third-party notices with the matching SDK.
            if ($framework.name -ne 'Microsoft.WindowsDesktop.App') { throw "Runtime notices not found: $packageDirectory" }
            $sdkPath = & $DotnetPath msbuild src/RtlCast.App/RtlCast.App.csproj -nologo -getProperty:MSBuildSDKsPath
            if ($LASTEXITCODE -ne 0) { throw 'Could not locate WindowsDesktop SDK notices.' }
            $notices = Join-Path ($sdkPath | Select-Object -Last 1) 'Microsoft.NET.Sdk.WindowsDesktop/THIRD-PARTY-NOTICES.TXT'
        }
        if (!(Test-Path -LiteralPath $notices)) { throw "Third-party notices not found: $notices" }
        Copy-Item -LiteralPath $notices -Destination (Join-Path $noticeDirectory "$($framework.name)-THIRD-PARTY-NOTICES.txt")
    }
    if (!$runtimeConfig.runtimeOptions.includedFrameworks) { throw 'Expected a self-contained publish with included frameworks.' }
    Copy-Item -LiteralPath 'docs/LICENSES.md' -Destination (Join-Path $PublishDirectory 'LICENSES.md')
    Copy-Item -LiteralPath 'LICENSE' -Destination (Join-Path $PublishDirectory 'LICENSE.txt')
    Copy-Item -LiteralPath 'NOTICE' -Destination (Join-Path $PublishDirectory 'NOTICE.txt')
}
finally { Pop-Location }
