<#
.SYNOPSIS
    Installs an AetherFrame test build into the one folder the owner's game loads, so Dalamud
    reloads it in game.

.DESCRIPTION
    docs/process/AUTOPILOT.md, "Test builds", steps 2 and 3. The owner's Dalamud loads AetherFrame
    as a dev plugin from -TestBuildDir, with automatic reloading on. Dalamud watches only
    AetherFrame.dll, for a last-write change, and reloads the plugin 500 ms after the last one,
    reading AetherFrame.json again (LocalDevPlugin.OnFileChanged). So this script:

    1. reads the three plugin files from -Package, and checks the DLL is the flavour asked for
       (player, or networking preview);
    2. stages them in -StagingRoot\<yyyy-MM-dd> <build id>[ preview]\ with SHA256SUMS.txt;
    3. backs up the plugin's data to -BackupRoot\AetherFrame-data-<yyyyMMdd-HHmmss>\;
    4. copies AetherFrame.json and AetherFrame.deps.json, then AetherFrame.dll last, each over the
       old file in place, and checks each copy's hash. Deleting the DLL, or renaming a file over
       it, raises no change event, so the game would keep the old build until it restarts;
    5. reads (never writes) Dalamud's configuration, and warns when the game would not load this
       folder or would not reload it.

    It never deletes a staged build or a backup, and never touches the game or its settings.
    A failure throws, so the exit code is non-zero; warnings leave it at 0.

.PARAMETER Package
    DalamudPackager's latest.zip, which holds exactly the three files, or a folder holding them
    (other files in it are ignored).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/Install-TestBuild.ps1 -Package AetherFrame/bin/x64/Release/AetherFrame/latest.zip -BuildId 01a14a5 -Flavour Player
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Package,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{7,40}$')]
    [string] $BuildId,

    [Parameter(Mandatory = $true)]
    [ValidateSet('Player', 'Preview')]
    [string] $Flavour,

    [string] $TestBuildDir = 'E:\AetherFrame Test Build',
    [string] $StagingRoot = 'E:\AetherFrame Test Builds',
    [string] $BackupRoot = 'E:\AetherFrame Archives\Acceptance backups',
    [string] $XivLauncherDir = (Join-Path $env:APPDATA 'XIVLauncher')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The install order: the DLL last, so the reload it triggers finds the new manifest beside it.
$PluginFiles = @('AetherFrame.json', 'AetherFrame.deps.json', 'AetherFrame.dll')

# Only the networking preview flavour compiles these namespaces in (PackageValidator.cs,
# NetworkingNamespaces). A type's namespace is stored as plain text in the DLL's metadata.
$NetworkingNamespaces = @('AetherFrame.Protocol', 'AetherFrame.Personas')

# Held open with no sharing while a preview build runs (PersonaInstanceLock.cs). It holds no data.
$LockFileName = 'instance.lock'

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-Sha256([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-BytesSha256([byte[]] $Bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash($Bytes)) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

# The three files' contents, by name.
function Read-BuildFiles([string] $Path) {
    $files = @{}
    if (Test-Path -LiteralPath $Path -PathType Container) {
        foreach ($name in $PluginFiles) {
            $file = Join-Path $Path $name
            if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
                throw "$Path has no $name."
            }
            $files[$name] = [System.IO.File]::ReadAllBytes($file)
        }
        return $files
    }

    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $expected = ($PluginFiles | Sort-Object) -join ', '
        $actual = (@($zip.Entries | ForEach-Object { $_.FullName }) | Sort-Object) -join ', '
        if ($actual -ne $expected) {
            throw "$Path holds '$actual'. A test build holds exactly $expected."
        }
        foreach ($entry in $zip.Entries) {
            $buffer = New-Object System.IO.MemoryStream
            $stream = $entry.Open()
            try {
                $stream.CopyTo($buffer)
            }
            finally {
                $stream.Dispose()
            }
            $files[$entry.FullName] = $buffer.ToArray()
        }
    }
    finally {
        $zip.Dispose()
    }
    return $files
}

function Get-DllFlavour([byte[]] $Bytes) {
    $text = [System.Text.Encoding]::ASCII.GetString($Bytes)
    foreach ($namespace in $NetworkingNamespaces) {
        if ($text.Contains($namespace)) {
            return 'Preview'
        }
    }
    return 'Player'
}

function Test-SamePath([string] $A, [string] $B) {
    $left = [System.IO.Path]::GetFullPath($A).TrimEnd('\')
    $right = [System.IO.Path]::GetFullPath($B).TrimEnd('\')
    return [string]::Equals($left, $right, [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-OptionalProperty($Object, [string] $Name) {
    if ($null -eq $Object) {
        return $null
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }
    return $property.Value
}

# What Dalamud's saved configuration says about loading AetherFrame from $DllPath. Dalamud saves
# it whenever a setting changes, so it reflects the last change made in game.
function Get-DalamudLoadState([string] $ConfigPath, [string] $DllPath) {
    $state = [ordered]@{ Readable = $false; LoadsThisFolder = $false; AutoReload = $false; OtherLocations = @() }
    if (-not (Test-Path -LiteralPath $ConfigPath)) {
        return $state
    }
    try {
        $config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        return $state
    }
    $state.Readable = $true

    $locations = @(Get-OptionalProperty (Get-OptionalProperty $config 'DevPluginLoadLocations') '$values')
    foreach ($location in $locations) {
        $path = [string](Get-OptionalProperty $location 'Path')
        if ([string]::IsNullOrWhiteSpace($path) -or -not (Get-OptionalProperty $location 'IsEnabled')) {
            continue
        }
        if ((Test-SamePath $path $DllPath) -or (Test-SamePath $path (Split-Path -Parent $DllPath))) {
            $state.LoadsThisFolder = $true
        }
        elseif ($path -match '(?i)\\AetherFrame\.dll$' -or (Test-Path -LiteralPath (Join-Path $path 'AetherFrame.dll'))) {
            $state.OtherLocations += $path
        }
    }

    $settings = Get-OptionalProperty $config 'DevPluginSettings'
    if ($null -ne $settings) {
        foreach ($entry in $settings.PSObject.Properties) {
            if ($entry.Name -ne '$type' -and (Test-SamePath $entry.Name $DllPath)) {
                $state.AutoReload = [bool](Get-OptionalProperty $entry.Value 'AutomaticReloading')
            }
        }
    }
    return $state
}

# Copies the plugin's data folder file by file, skipping only the preview build's lock file.
# Any other file that can't be copied stops the install, so no build goes in without a backup.
function Backup-PluginData([string] $Source, [string] $Destination) {
    $skipped = @()
    $root = (Get-Item -LiteralPath $Source).FullName.TrimEnd('\')
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $root -Recurse -Force) {
        $relative = $item.FullName.Substring($root.Length + 1)
        $target = Join-Path $Destination $relative
        if ($item.PSIsContainer) {
            New-Item -ItemType Directory -Force -Path $target | Out-Null
            continue
        }
        if ($item.Name -eq $LockFileName) {
            $skipped += $relative
            continue
        }
        $parent = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
        }
        Copy-Item -LiteralPath $item.FullName -Destination $target
    }
    return $skipped
}

# 1. The build.
$files = Read-BuildFiles (Resolve-Path -LiteralPath $Package).ProviderPath
$hashes = @{}
foreach ($name in $PluginFiles) {
    $hashes[$name] = Get-BytesSha256 $files[$name]
}
$dllFlavour = Get-DllFlavour $files['AetherFrame.dll']
if ($dllFlavour -ne $Flavour) {
    throw "The DLL is a $dllFlavour build, not $Flavour. Nothing was staged or installed."
}

# 2. Stage it. A staged build is never replaced: one that already exists is reused only when its
#    files are these.
$stageName = (Get-Date -Format 'yyyy-MM-dd') + ' ' + $BuildId
if ($Flavour -eq 'Preview') {
    $stageName += ' preview'
}
$stageDir = Join-Path $StagingRoot $stageName
if (Test-Path -LiteralPath $stageDir) {
    foreach ($name in $PluginFiles) {
        $staged = Join-Path $stageDir $name
        if (-not (Test-Path -LiteralPath $staged) -or (Get-Sha256 $staged) -ne $hashes[$name]) {
            throw "$stageDir already exists and doesn't hold this build. Staged builds are never replaced: check that folder."
        }
    }
}
else {
    New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
    foreach ($name in $PluginFiles) {
        $staged = Join-Path $stageDir $name
        [System.IO.File]::WriteAllBytes($staged, $files[$name])
        if ((Get-Sha256 $staged) -ne $hashes[$name]) {
            throw "The staged $staged doesn't match the build."
        }
    }
    $sums = @($PluginFiles | Sort-Object | ForEach-Object { $hashes[$_] + '  ' + $_ })
    [System.IO.File]::WriteAllText((Join-Path $stageDir 'SHA256SUMS.txt'), ($sums -join "`n") + "`n")
}

# 3. Dalamud's view, read before anything changes.
$installedDll = Join-Path $TestBuildDir 'AetherFrame.dll'
$dalamud = Get-DalamudLoadState (Join-Path $XivLauncherDir 'dalamudConfig.json') $installedDll
$gameRunning = @(Get-Process -Name 'ffxiv_dx11' -ErrorAction SilentlyContinue).Count -gt 0

$alreadyInstalled = $true
foreach ($name in $PluginFiles) {
    $target = Join-Path $TestBuildDir $name
    if (-not (Test-Path -LiteralPath $target) -or (Get-Sha256 $target) -ne $hashes[$name]) {
        $alreadyInstalled = $false
    }
}

# 4. Back up the plugin's data, then install.
$backupDir = $null
$skipped = @()
if (-not $alreadyInstalled) {
    $configsDir = Join-Path $XivLauncherDir 'pluginConfigs'
    # Backups are never replaced: one that already has this second's name waits for the next.
    $backupDir = Join-Path $BackupRoot ('AetherFrame-data-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    while (Test-Path -LiteralPath $backupDir) {
        Start-Sleep -Milliseconds 250
        $backupDir = Join-Path $BackupRoot ('AetherFrame-data-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    }
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    $dataDir = Join-Path $configsDir 'AetherFrame'
    if (Test-Path -LiteralPath $dataDir) {
        $skipped = @(Backup-PluginData $dataDir (Join-Path $backupDir 'AetherFrame'))
    }
    $configFile = Join-Path $configsDir 'AetherFrame.json'
    if (Test-Path -LiteralPath $configFile) {
        Copy-Item -LiteralPath $configFile -Destination $backupDir
    }

    New-Item -ItemType Directory -Force -Path $TestBuildDir | Out-Null
    foreach ($name in $PluginFiles) {
        $target = Join-Path $TestBuildDir $name
        try {
            # In place over the old file: never delete it or rename over it (see the description).
            Copy-Item -LiteralPath (Join-Path $stageDir $name) -Destination $target -Force
        }
        catch {
            throw "Couldn't replace $target ($($_.Exception.Message)). The build stays staged in $stageDir; install it from there on the next run."
        }
        if ((Get-Sha256 $target) -ne $hashes[$name]) {
            throw "The installed $target doesn't match the staged build."
        }
    }
}

# 5. Where the build ends up in game.
if (-not $dalamud.Readable) {
    $inGame = "unknown: Dalamud's configuration couldn't be read."
}
elseif (-not $dalamud.LoadsThisFolder) {
    $inGame = 'not loaded: Dalamud has no enabled dev plugin location for this folder.'
    Write-Warning "The game won't load this build. In game: /xlsettings, Experimental, Dev Plugin Locations: add and tick $installedDll, then Save and Close."
}
elseif ($alreadyInstalled) {
    $inGame = 'already installed: nothing was copied, so nothing reloads.'
}
elseif (-not $gameRunning) {
    $inGame = 'loads at the next game start.'
}
elseif ($dalamud.AutoReload) {
    $inGame = 'reloaded in game: Dalamud picks up the new DLL within about a second, if AetherFrame was loaded.'
}
else {
    $inGame = 'waiting: automatic reloading is off for this folder, so reload AetherFrame in /xlplugins, Dev Tools, or restart the game.'
    Write-Warning "Automatic reloading is off for $installedDll."
}
foreach ($other in $dalamud.OtherLocations) {
    Write-Warning "Another enabled dev plugin location holds AetherFrame: $other. Dalamud loads one AetherFrame at a time; untick that location in /xlsettings, Experimental."
}

[pscustomobject][ordered]@{
    BuildId        = $BuildId
    Flavour        = $Flavour
    Staged         = $stageDir
    Installed      = $TestBuildDir
    DllSha256      = $hashes['AetherFrame.dll']
    DataBackup     = $backupDir
    NotBackedUp    = ($skipped -join ', ')
    GameRunning    = $gameRunning
    DalamudLoadsIt = $dalamud.LoadsThisFolder
    AutoReload     = $dalamud.AutoReload
    InGame         = $inGame
}
