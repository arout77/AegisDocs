# Copies the speech engine files (Whisper's native DLLs) into the published app folder.
#
# Why this script exists: the Whisper NuGet packages are meant to drop these files into a "runtimes" folder next to the
# exe automatically, but publishing as a single exe doesn't reliably carry them along. If the folder is missing, the
# program reports "Native library not found" when you try to make a transcript. So copy them explicitly, and stop with a
# clear message if they can't be found.
#
# Usage:  powershell -ExecutionPolicy Bypass -File tools\add-speech-engine.ps1 -PublishDir publish

param(
    [string]$PublishDir = "publish",
    [string]$Version = "1.9.1"      # keep in step with the Whisper.net.Runtime packages in ReceiptCapture.csproj
)

$ErrorActionPreference = "Stop"

$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget\packages" }

function Copy-Engine([string]$packageId, [string]$targetSubfolder) {
    $packageFolder = Join-Path $packages "$packageId\$Version"
    if (-not (Test-Path $packageFolder)) {
        throw "NuGet package $packageId $Version was not found in $packages. Build the project once so it is restored."
    }

    # Normal layout: <package>\build\win-x64\whisper.dll. If the package is ever laid out differently, search for it.
    $source = Join-Path $packageFolder "build\win-x64"
    if (-not (Test-Path (Join-Path $source "whisper.dll"))) {
        $found = Get-ChildItem $packageFolder -Recurse -Filter "whisper.dll" |
                 Where-Object { $_.FullName -match "win-x64" } | Select-Object -First 1
        if (-not $found) { throw "whisper.dll for win-x64 was not found anywhere inside $packageFolder." }
        $source = $found.DirectoryName
    }

    $destination = Join-Path $PublishDir $targetSubfolder
    New-Item -ItemType Directory -Force $destination | Out-Null
    Copy-Item (Join-Path $source "*.dll") $destination -Force
    Write-Host "Speech engine ($packageId): copied $((Get-ChildItem $destination -Filter *.dll).Count) files to $destination"
}

Copy-Engine "whisper.net.runtime"       "runtimes\win-x64"          # normal processors
Copy-Engine "whisper.net.runtime.noavx" "runtimes\noavx\win-x64"    # older processors without AVX

foreach ($folder in @("runtimes\win-x64", "runtimes\noavx\win-x64")) {
    foreach ($file in @("whisper.dll", "ggml-whisper.dll", "ggml-base-whisper.dll", "ggml-cpu-whisper.dll")) {
        $path = Join-Path $PublishDir "$folder\$file"
        if (-not (Test-Path $path)) { throw "Speech engine file is missing after copying: $path" }
    }
}

# The package can also leave engines for other systems (Linux, Mac, phones) in the folder. They are not needed.
Get-ChildItem (Join-Path $PublishDir "runtimes") -Directory |
    Where-Object { $_.Name -notin @("win-x64", "noavx") } | Remove-Item -Recurse -Force

Write-Host "Speech engine files are in place."
