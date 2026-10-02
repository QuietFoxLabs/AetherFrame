<#
.SYNOPSIS
    Packages a networking preview build as a tester kit (NETWORK2's N2-11): a zip the owner gives
    to another player for the sharing test.

.DESCRIPTION
    A tester kit is how a preview build reaches a second player (docs/networking/NETWORK2.md,
    section 3; "N2-11's tester kit" in docs/networking/DecisionRegister.md). It is never a release
    or a test build, and never goes into the folder the owner's game loads. Since October 1, 2026
    ("Releases carry sharing" in the decision register) the testing channel serves the
    sharing build, so a kit is only made when the owner asks for one. This script:
    1. reads the three plugin files from -Package, checks them against the SHA256SUMS.txt staged
       with them, and checks the DLL is the preview flavour, stamped with -BuildId's commit, and
       the manifest is AetherFrame's, so a kit always holds exactly the staged build named;
    2. makes -StagingRoot\<yyyy-MM-dd> <build id> tester kit\, which must not exist yet;
    3. writes AetherFrame-tester-kit-<build id>.zip there, holding an AetherFrame folder with the
       three files, distribution\tester-kit\How to install.txt, and SHA256SUMS.txt for the three
       files;
    4. writes the zip's own checksum beside it, and prints where it is.
    It deletes and overwrites nothing.

.PARAMETER Package
    A staged preview build: a folder holding AetherFrame.dll, AetherFrame.json,
    AetherFrame.deps.json and the SHA256SUMS.txt Install-TestBuild.ps1 wrote for them, and nothing
    else, as AUTOPILOT.md's preview test builds stage them.

.PARAMETER BuildId
    The short or full commit the build was made from.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/New-TesterKit.ps1 -Package "E:\AetherFrame Test Builds\2026-10-01 abc1234 preview" -BuildId abc1234
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Package,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{7,40}$')]
    [string] $BuildId,

    [string] $StagingRoot = 'E:\AetherFrame Test Builds'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$PluginFiles = @('AetherFrame.dll', 'AetherFrame.json', 'AetherFrame.deps.json')

# The sharing build (the script's Preview, the default since 0.1.8) compiles these namespaces in; a
# player build doesn't (as Install-TestBuild.ps1 checks).
$NetworkingNamespaces = @('AetherFrame.Protocol', 'AetherFrame.Personas')

$Instructions = Join-Path $PSScriptRoot '..\distribution\tester-kit\How to install.txt'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-BytesSha256([byte[]] $Bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash($Bytes)) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Add-Entry([System.IO.Compression.ZipArchive] $Zip, [string] $Name, [byte[]] $Bytes) {
    $entry = $Zip.CreateEntry($Name, [System.IO.Compression.CompressionLevel]::Optimal)
    $stream = $entry.Open()
    try {
        $stream.Write($Bytes, 0, $Bytes.Length)
    }
    finally {
        $stream.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $Package -PathType Container)) {
    throw "$Package isn't a folder."
}

# A staged build's SHA256SUMS.txt is left behind: the kit writes its own.
$extra = @(Get-ChildItem -LiteralPath $Package -File | Where-Object { $PluginFiles -notcontains $_.Name -and $_.Name -ne 'SHA256SUMS.txt' } | ForEach-Object { $_.Name })
if ($extra.Count -gt 0) {
    throw "$Package holds files a kit doesn't: $($extra -join ', '). A kit holds exactly $($PluginFiles -join ', ')."
}

$files = [ordered]@{}
foreach ($name in $PluginFiles) {
    $path = Join-Path $Package $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "$Package has no $name."
    }
    $files[$name] = [System.IO.File]::ReadAllBytes($path)
}

# The staged checksums: every file must be exactly the one staged.
$sumsPath = Join-Path $Package 'SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $sumsPath -PathType Leaf)) {
    throw "$Package has no SHA256SUMS.txt. Package a staged build: E:\AetherFrame Test Builds\<date> <commit> preview\."
}
$staged = @{}
foreach ($line in [System.IO.File]::ReadAllLines($sumsPath)) {
    if ($line -match '^([0-9a-f]{64})  (\S+)$') {
        $staged[$Matches[2]] = $Matches[1]
    }
}
foreach ($name in $PluginFiles) {
    if ($staged[$name] -ne (Get-BytesSha256 $files[$name])) {
        throw "$name isn't the file SHA256SUMS.txt names: this isn't the staged build."
    }
}

$dllText = [System.Text.Encoding]::ASCII.GetString($files['AetherFrame.dll'])

# The build's commit, stamped in its informational version as "<version>+<full sha>".
if ($dllText -notmatch ('\d+\.\d+\.\d+\+' + [regex]::Escape($BuildId))) {
    throw "The DLL isn't stamped with commit $BuildId. Name the commit the staged build was made from."
}
$isPreview = $false
foreach ($namespace in $NetworkingNamespaces) {
    if ($dllText.Contains($namespace)) {
        $isPreview = $true
    }
}
if (-not $isPreview) {
    throw 'The DLL is a player build. A tester kit is always the sharing build.'
}

$manifest = [System.Text.Encoding]::UTF8.GetString($files['AetherFrame.json']) | ConvertFrom-Json
if ($manifest.InternalName -ne 'AetherFrame') {
    throw "The manifest's InternalName is '$($manifest.InternalName)', not AetherFrame."
}

if (-not (Test-Path -LiteralPath $Instructions -PathType Leaf)) {
    throw "The instructions aren't at $Instructions."
}
$instructionBytes = [System.IO.File]::ReadAllBytes($Instructions)

$date = Get-Date -Format 'yyyy-MM-dd'
$folder = Join-Path $StagingRoot "$date $BuildId tester kit"
if (Test-Path -LiteralPath $folder) {
    throw "$folder already exists. A kit is never written over."
}
New-Item -ItemType Directory -Path $folder | Out-Null

$sums = New-Object System.Text.StringBuilder
foreach ($name in $PluginFiles) {
    [void]$sums.Append("$(Get-BytesSha256 $files[$name])  AetherFrame/$name`n")
}

$zipName = "AetherFrame-tester-kit-$BuildId.zip"
$zipPath = Join-Path $folder $zipName
$stream = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::CreateNew)
try {
    $zip = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $PluginFiles) {
            Add-Entry $zip "AetherFrame/$name" $files[$name]
        }
        Add-Entry $zip 'How to install.txt' $instructionBytes
        Add-Entry $zip 'SHA256SUMS.txt' ([System.Text.Encoding]::ASCII.GetBytes($sums.ToString()))
    }
    finally {
        $zip.Dispose()
    }
}
finally {
    $stream.Dispose()
}

$zipSum = Get-BytesSha256 ([System.IO.File]::ReadAllBytes($zipPath))
[System.IO.File]::WriteAllText((Join-Path $folder "$zipName.sha256"), "$zipSum  $zipName`n", (New-Object System.Text.UTF8Encoding($false)))

[pscustomobject]@{
    BuildId    = $BuildId
    Version    = $manifest.AssemblyVersion
    Kit        = $zipPath
    KitSha256  = $zipSum
    DllSha256  = Get-BytesSha256 $files['AetherFrame.dll']
}
