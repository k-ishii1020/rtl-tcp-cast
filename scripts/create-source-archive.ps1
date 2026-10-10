# SPDX-License-Identifier: GPL-2.0-or-later
# Copyright (c) 2026 いしかず
param([string]$Destination = 'artifacts/RTL-TCP-Cast-source.zip')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    # Include the working sources used by publish, without Git history or local tools.
    $files = @(Get-Item -LiteralPath 'LICENSE', 'NOTICE', 'RtlCast.sln', 'Directory.Build.props', 'global.json', '.gitignore')
    $files += Get-ChildItem -LiteralPath $repoRoot -Filter 'README*.md' -File
    foreach ($directory in @('src', 'tests', 'scripts', 'docs')) {
        $files += Get-ChildItem -LiteralPath $directory -Recurse -File | Where-Object {
            $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
        }
    }
    $destinationPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $Destination))
    New-Item -ItemType Directory -Path (Split-Path -Parent $destinationPath) -Force | Out-Null
    $stream = [IO.File]::Open($destinationPath, [IO.FileMode]::Create)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in $files) {
                $relative = $file.FullName.Substring($repoRoot.Length + 1).Replace('\', '/')
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, "RTL-TCP-Cast-source/$relative", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
}
finally { Pop-Location }
