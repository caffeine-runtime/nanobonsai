# Downloads the CEF runtime the bridge is built against (pinned version, SHA-1 checked) from CEF's official builds and
# copies what the browser needs at run time, with CEF's LICENSE.txt and CREDITS.html, into Assets/Plugins/x86_64.
# Close Unity first, then run: powershell -ExecutionPolicy Bypass -File NativePlugin\fetch-cef-windows.ps1
[CmdletBinding()]
param(
    # Keeps the downloaded archive between runs.
    [string] $CacheDirectory = (Join-Path $env:LOCALAPPDATA 'nanobonsai\cef')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$version = '148.0.10+g7ee53f5+chromium-148.0.7778.218'
$sha1 = '2974c0c195343a1452c59a2687583eaceb98839b'
$name = "cef_binary_${version}_windows64_minimal"
$destination = Join-Path (Split-Path -Parent $PSScriptRoot) 'Assets\Plugins\x86_64'
$archive = Join-Path $CacheDirectory "$name.tar.bz2"
$extracted = Join-Path $CacheDirectory 'extract'

New-Item -ItemType Directory -Path $CacheDirectory -Force | Out-Null
if (!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA1).Hash -ne $sha1) {
    $url = 'https://cef-builds.spotifycdn.com/' + [Uri]::EscapeDataString("$name.tar.bz2")
    Write-Output "Downloading $url (about 160 MB)"
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $url -OutFile "$archive.part" -UseBasicParsing
    $actual = (Get-FileHash -LiteralPath "$archive.part" -Algorithm SHA1).Hash
    if ($actual -ne $sha1) {
        Remove-Item -LiteralPath "$archive.part"
        throw "The download's SHA-1 is $actual, not $sha1."
    }
    Move-Item -LiteralPath "$archive.part" -Destination $archive -Force
}

if (Test-Path -LiteralPath $extracted) { Remove-Item -LiteralPath $extracted -Recurse -Force }
New-Item -ItemType Directory -Path $extracted | Out-Null
& "$env:SystemRoot\System32\tar.exe" -xjf $archive -C $extracted "$name/Release" "$name/Resources" "$name/LICENSE.txt" "$name/CREDITS.html"
if ($LASTEXITCODE -ne 0) { throw "Extracting $archive failed: $LASTEXITCODE" }
$sdk = Join-Path $extracted $name

New-Item -ItemType Directory -Path $destination -Force | Out-Null
# The bridge loads the DLLs, V8's snapshot and SwiftShader's manifest; the SDK's import library and bootstrap
# executables are for other ways of embedding CEF.
Get-ChildItem -LiteralPath (Join-Path $sdk 'Release') -File |
    Where-Object { $_.Extension -in '.dll', '.bin', '.json' } |
    Copy-Item -Destination $destination -Force
Get-ChildItem -LiteralPath (Join-Path $sdk 'Resources') | Copy-Item -Destination $destination -Recurse -Force
foreach ($notice in 'LICENSE.txt', 'CREDITS.html') {
    Copy-Item -LiteralPath (Join-Path $sdk $notice) -Destination $destination -Force
}
Remove-Item -LiteralPath $extracted -Recurse -Force
Write-Output "CEF $version runtime copied to $destination"
