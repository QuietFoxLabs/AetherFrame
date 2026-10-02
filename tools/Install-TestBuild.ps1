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
    3. reads (never writes) Dalamud's configuration, and when the game is running and will reload
       AetherFrame, waits -GraceSeconds first;
    4. backs up the plugin's data to -BackupRoot\AetherFrame-data-<yyyyMMdd-HHmmss>\, under a
       ".partial" name until the backup is complete;
    5. copies AetherFrame.json and AetherFrame.deps.json, then AetherFrame.dll last, each over the
       old file in place, and checks each copy's hash. Deleting the DLL, or renaming a file over
       it, raises no change event, so the game would keep the old build until it restarts;
    6. reports in InGame whether the owner gets the build: reloaded in game, at the next start,
       or not (not listed, the folder listed instead of the DLL, another AetherFrame listed,
       reloading off), with a warning that names the fix.

    It never deletes a staged build or a backup, and never touches the game or its settings.
    A failure throws, so the exit code is non-zero; warnings leave it at 0.

.PARAMETER Package
    DalamudPackager's latest.zip, which holds exactly the three files, or a folder holding them
    (other files in it are ignored).

.PARAMETER GraceSeconds
    How long to wait before backing up and installing when the game is running and would reload
    AetherFrame. The reload closes AetherFrame's windows and loses an editor's unsaved changes, so
    AUTOPILOT.md warns the owner first and passes the time it promised.

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

    [ValidateRange(0, 540)]
    [int] $GraceSeconds = 0,

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
# Downloaded artwork (art on demand): kept out of backups, since it downloads again when it is missing.
$ArtworkCacheFolder = 'artwork-cache'

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
# it whenever a setting changes, so it reflects the last change made in game. Each dev plugin
# location is a DLL's path: Dalamud reads it as a file and skips a folder (PluginManager). It
# doesn't deduplicate dev plugins by name, so with a second enabled AetherFrame location both
# builds may load at once and share the plugin's data.
function Get-DalamudLoadState([string] $ConfigPath, [string] $DllPath) {
    $state = [ordered]@{
        Readable       = $false
        ListsDll       = $false
        ListsFolder    = $false
        AutoReload     = $true
        StartOnBoot    = $true
        OtherLocations = @()
    }
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
        if (Test-SamePath $path $DllPath) {
            $state.ListsDll = $true
        }
        elseif (Test-SamePath $path (Split-Path -Parent $DllPath)) {
            $state.ListsFolder = $true
        }
        elseif ($path -match '(?i)\\AetherFrame\.dll$') {
            $state.OtherLocations += $path
        }
    }

    # A DLL with no entry here has Dalamud's defaults (DevPluginSettings): both on.
    $settings = Get-OptionalProperty $config 'DevPluginSettings'
    if ($null -ne $settings) {
        foreach ($entry in $settings.PSObject.Properties) {
            if ($entry.Name -ne '$type' -and (Test-SamePath $entry.Name $DllPath)) {
                $reload = Get-OptionalProperty $entry.Value 'AutomaticReloading'
                $boot = Get-OptionalProperty $entry.Value 'StartOnBoot'
                $state.AutoReload = ($null -eq $reload) -or [bool]$reload
                $state.StartOnBoot = ($null -eq $boot) -or [bool]$boot
            }
        }
    }
    return $state
}

# Copies the plugin's data folder file by file. It skips the preview build's lock file, and a
# temporary file ("*.tmp") that is held open, since a save still in progress (or one Dalamud
# left open after a failed write) has its data in the real file beside it. Any other file that
# can't be copied, and any link (which Windows PowerShell would not follow), stops the install,
# so no build goes in without a full backup.
function Backup-PluginData([string] $Source, [string] $Destination) {
    $skipped = @()
    $root = (Get-Item -LiteralPath $Source).FullName.TrimEnd('\')
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $root -Recurse -Force) {
        $relative = $item.FullName.Substring($root.Length + 1)
        $target = Join-Path $Destination $relative
        if ($relative -eq $ArtworkCacheFolder) {
            $skipped += $relative + '\'
            continue
        }
        if ($relative.StartsWith($ArtworkCacheFolder + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            throw "$($item.FullName) is a link, which the backup can't follow. Back that folder up by hand; nothing was installed."
        }
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
        try {
            Copy-Item -LiteralPath $item.FullName -Destination $target
        }
        catch {
            if ($item.Name -notlike '*.tmp') {
                throw "Couldn't back up $($item.FullName) ($($_.Exception.Message)). Nothing was installed; the incomplete backup keeps its .partial name."
            }
            $skipped += $relative
        }
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

# A reload closes AetherFrame's windows, and an editor's unsaved changes go with them. When one is
# about to happen, give the owner the time AUTOPILOT.md's heads-up promised before touching anything.
# Settings that can't be read count as a reload, since the script can't rule one out.
$reloads = (-not $alreadyInstalled) -and $gameRunning -and ((-not $dalamud.Readable) -or ($dalamud.ListsDll -and $dalamud.AutoReload))
$waited = 0
if ($reloads -and $GraceSeconds -gt 0) {
    Write-Host "The game is running and may reload AetherFrame: waiting $GraceSeconds seconds before installing."
    Start-Sleep -Seconds $GraceSeconds
    $waited = $GraceSeconds
}

# 4. Back up the plugin's data, then install. The backup is written under a ".partial" name and
#    takes its final name only once it is complete.
$backupDir = $null
$skipped = @()
if (-not $alreadyInstalled) {
    $configsDir = Join-Path $XivLauncherDir 'pluginConfigs'
    # Backups are never replaced: one that already has this second's name waits for the next.
    do {
        $backupName = 'AetherFrame-data-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
        $backupDir = Join-Path $BackupRoot $backupName
        $partialDir = $backupDir + '.partial'
        $taken = (Test-Path -LiteralPath $backupDir) -or (Test-Path -LiteralPath $partialDir)
        if ($taken) {
            Start-Sleep -Milliseconds 250
        }
    } while ($taken)
    New-Item -ItemType Directory -Force -Path $partialDir | Out-Null
    $dataDir = Join-Path $configsDir 'AetherFrame'
    if (Test-Path -LiteralPath $dataDir) {
        $skipped = @(Backup-PluginData $dataDir (Join-Path $partialDir 'AetherFrame'))
    }
    else {
        Write-Warning "There is no plugin data folder at $dataDir, so the backup holds no Plates."
    }
    $configFile = Join-Path $configsDir 'AetherFrame.json'
    if (Test-Path -LiteralPath $configFile) {
        Copy-Item -LiteralPath $configFile -Destination $partialDir
    }
    Rename-Item -LiteralPath $partialDir -NewName $backupName

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

# 5. Where the build ends up in game. Only "reloaded" and "loads at the next game start" mean the
#    owner gets this build; every other state names what is wrong.
$fix = "In game: /xlsettings, Experimental, Dev Plugin Locations: add and tick $installedDll (the DLL, not the folder), untick every other AetherFrame location, then Save and Close."
if (-not $dalamud.Readable) {
    $inGame = "unknown: Dalamud's configuration couldn't be read."
}
elseif (-not $dalamud.ListsDll) {
    if ($dalamud.ListsFolder) {
        $inGame = 'not loaded: Dalamud lists the folder, and it only loads a location that is the DLL itself.'
    }
    else {
        $inGame = 'not loaded: no enabled dev plugin location is this DLL.'
    }
    Write-Warning "The game won't load this build. $fix"
}
elseif ($dalamud.OtherLocations.Count -gt 0) {
    $inGame = 'conflict: another enabled dev plugin location is an AetherFrame too, so both builds may run at once and share the plugin''s data.'
    foreach ($other in $dalamud.OtherLocations) {
        Write-Warning "Another enabled dev plugin location is AetherFrame: $other. $fix"
    }
}
elseif ($alreadyInstalled) {
    $inGame = 'already installed: nothing was copied, so nothing reloads.'
}
elseif (-not $gameRunning) {
    if ($dalamud.StartOnBoot) {
        $inGame = 'loads at the next game start.'
    }
    else {
        $inGame = 'waiting: "load on boot" is off for this DLL, so load AetherFrame in /xlplugins, Dev Tools, after the next game start.'
    }
}
elseif ($dalamud.AutoReload) {
    $inGame = 'reloaded in game: Dalamud picks up the new DLL within about a second, if AetherFrame was loaded.'
}
else {
    $inGame = 'waiting: automatic reloading is off for this DLL, so reload AetherFrame in /xlplugins, Dev Tools, or restart the game.'
    Write-Warning "Automatic reloading is off for $installedDll."
}

[pscustomobject][ordered]@{
    BuildId     = $BuildId
    Flavour     = $Flavour
    Staged      = $stageDir
    Installed   = $TestBuildDir
    DllSha256   = $hashes['AetherFrame.dll']
    DataBackup  = $backupDir
    NotBackedUp = ($skipped -join ', ')
    GameRunning = $gameRunning
    Waited      = $waited
    InGame      = $inGame
}
