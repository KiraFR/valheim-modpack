<#
.SYNOPSIS
    Installs the Valheim modpack (BepInEx + mods) into the game or dedicated server folder.

.DESCRIPTION
    - Finds Valheim through Steam (registry + every library listed in libraryfolders.vdf).
    - Installs BepInExPack Valheim from Thunderstore if it is missing.
    - Downloads valheim-modpack.zip from the latest GitHub release and copies each mod into
      BepInEx/plugins/<Mod>/, replacing the previous version. Other mods and .cfg files are left untouched.
    - With -Server, targets the dedicated server and only installs the mods needed server-side.

    Valheim must be closed during the installation.

.EXAMPLE
    irm https://raw.githubusercontent.com/KiraFR/valheim-modpack/main/install.ps1 | iex

.EXAMPLE
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/KiraFR/valheim-modpack/main/install.ps1))) -Server

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1 -ValheimPath "D:\Games\Valheim"
#>
[CmdletBinding()]
param(
    # Valheim folder. Default: detected through Steam.
    [string]$ValheimPath,
    # Targets the dedicated server (Valheim dedicated server) instead of the game.
    [switch]$Server,
    # Release to install, for example v1.2.0. Default: the latest one.
    [string]$Version = 'latest',
    # Local modpack archive to use instead of downloading it.
    [string]$ZipPath,
    # Installs BepInEx only, without the mods.
    [switch]$BepInExOnly,
    # Reinstalls BepInEx even if it is already present.
    [switch]$ForceBepInEx
)

$ErrorActionPreference = 'Stop'
# The progress bar makes Invoke-WebRequest extremely slow on Windows PowerShell 5.1.
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$Repository = 'KiraFR/valheim-modpack'
$ArchiveName = 'valheim-modpack.zip'
$BepInExApi = 'https://thunderstore.io/api/experimental/package/denikson/BepInExPack_Valheim/'

# Mods to install on a dedicated server: StackMax (chests clamp stacks server-side), PortalMenu (only the server
# knows every portal in the world) and QuickBrew (the server permanently owns the barrels around the world spawn).
# The others are client-only.
$ServerMods = @('StackMax', 'PortalMenu', 'QuickBrew')

function Write-Step([string]$text) {
    Write-Host "==> $text" -ForegroundColor Cyan
}

function New-TempFolder {
    $path = Join-Path ([IO.Path]::GetTempPath()) ('valheim-modpack-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $path | Out-Null
    return $path
}

function Get-SteamLibraries {
    $roots = @()
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        $properties = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if ($properties.SteamPath) { $roots += $properties.SteamPath }
        if ($properties.InstallPath) { $roots += $properties.InstallPath }
    }
    $roots += Join-Path ${env:ProgramFiles(x86)} 'Steam'

    $libraries = @()
    foreach ($root in $roots) {
        $root = $root -replace '/', '\'
        if (-not (Test-Path $root)) { continue }
        $libraries += $root
        # Every drive where Steam installs games is listed in libraryfolders.vdf ("path" "D:\\SteamLibrary").
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $libraries += $m.Groups[1].Value -replace '\\\\', '\'
            }
        }
    }
    return $libraries | ForEach-Object { $_.TrimEnd('\') } | Sort-Object -Unique
}

function Find-Valheim([bool]$server) {
    if ($server) { $folder = 'Valheim dedicated server'; $exe = 'valheim_server.exe' }
    else { $folder = 'Valheim'; $exe = 'valheim.exe' }

    foreach ($library in Get-SteamLibraries) {
        $path = Join-Path $library "steamapps\common\$folder"
        if (Test-Path (Join-Path $path $exe)) { return $path }
    }
    return $null
}

function Install-BepInEx([string]$target) {
    Write-Step 'Downloading BepInExPack Valheim (Thunderstore)'
    $package = Invoke-RestMethod -Uri $BepInExApi -UseBasicParsing
    $temp = New-TempFolder
    try {
        $zip = Join-Path $temp 'BepInExPack_Valheim.zip'
        Invoke-WebRequest -Uri $package.latest.download_url -OutFile $zip -UseBasicParsing
        Expand-Archive -Path $zip -DestinationPath (Join-Path $temp 'x')

        # The content to copy into the game folder is the folder holding winhttp.dll (the Doorstop loader).
        $winhttp = Get-ChildItem (Join-Path $temp 'x') -Recurse -Filter 'winhttp.dll' | Select-Object -First 1
        if (-not $winhttp) { throw "Unexpected BepInExPack archive: winhttp.dll not found." }
        Copy-Item -Path (Join-Path $winhttp.DirectoryName '*') -Destination $target -Recurse -Force
        Write-Host "    BepInExPack Valheim $($package.latest.version_number) installed"
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Install-Mods([string]$target, [string]$zip, [bool]$server) {
    $temp = New-TempFolder
    try {
        if (-not $zip) {
            # The API returns the latest published release (drafts and pre-releases excluded) with its tag and files.
            if ($Version -eq 'latest') { $api = "https://api.github.com/repos/$Repository/releases/latest" }
            else { $api = "https://api.github.com/repos/$Repository/releases/tags/$Version" }
            try {
                $release = Invoke-RestMethod -Uri $api -UseBasicParsing
            }
            catch {
                $what = "release $Version"
                if ($Version -eq 'latest') { $what = 'no published release' }
                throw "Modpack not found ($what) on https://github.com/$Repository/releases: $($_.Exception.Message)"
            }
            $asset = $release.assets | Where-Object { $_.name -eq $ArchiveName } | Select-Object -First 1
            if (-not $asset) { throw "Release $($release.tag_name) does not contain $ArchiveName." }

            Write-Step "Downloading modpack $($release.tag_name)"
            $zip = Join-Path $temp $ArchiveName
            Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing
        }

        Expand-Archive -Path $zip -DestinationPath (Join-Path $temp 'x')
        $sources = Join-Path $temp 'x\BepInEx\plugins'
        if (-not (Test-Path $sources)) { throw "Unexpected modpack archive: BepInEx\plugins is missing." }

        $plugins = Join-Path $target 'BepInEx\plugins'
        New-Item -ItemType Directory -Force -Path $plugins | Out-Null

        Write-Step "Installing mods into $plugins"
        $installed = 0
        foreach ($mod in Get-ChildItem $sources -Directory) {
            if ($server -and $ServerMods -notcontains $mod.Name) { continue }

            # The whole mod folder is replaced so no file from an older version is left behind.
            $destination = Join-Path $plugins $mod.Name
            if (Test-Path $destination) { Remove-Item $destination -Recurse -Force }
            Copy-Item -Path $mod.FullName -Destination $destination -Recurse

            $dll = Join-Path $destination "$($mod.Name).dll"
            $modVersion = ''
            if (Test-Path $dll) { $modVersion = (Get-Item $dll).VersionInfo.FileVersion -replace '\.0$', '' }
            Write-Host ("    {0,-12} {1}" -f $mod.Name, $modVersion)
            $installed++
        }
        if ($installed -eq 0) { throw "No mod found in the archive." }
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ----- Main -----

if ($ValheimPath) {
    if (-not (Test-Path $ValheimPath -PathType Container)) { throw "Folder not found: $ValheimPath" }
    $target = (Resolve-Path $ValheimPath).Path
}
else {
    $target = Find-Valheim $Server.IsPresent
    if (-not $target) {
        $what = 'Valheim'
        if ($Server) { $what = 'Valheim dedicated server' }
        throw "$what not found in the Steam libraries. Pass the folder with -ValheimPath."
    }
}
Write-Step "Target folder: $target"

# Only a Valheim started from the target folder is a problem: winhttp.dll is locked there, and replaced mods
# would only load on the next launch anyway. An unreadable process path counts as blocking.
$running = Get-Process -Name 'valheim', 'valheim_server' -ErrorAction SilentlyContinue | Where-Object {
    -not $_.Path -or $_.Path.StartsWith($target.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}
if ($running) {
    throw "Close $($running[0].ProcessName) before installing: it is running from $target."
}

if ($ZipPath) { $ZipPath = (Resolve-Path $ZipPath).Path }

if ($ForceBepInEx -or -not (Test-Path (Join-Path $target 'BepInEx\core\BepInEx.dll'))) {
    Install-BepInEx $target
}
else {
    Write-Step 'BepInEx already installed'
}

if (-not $BepInExOnly) {
    Install-Mods $target $ZipPath $Server.IsPresent
}

Write-Step 'Done. Launch Valheim from Steam as usual.'
