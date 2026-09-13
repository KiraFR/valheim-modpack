<#
.SYNOPSIS
    Installs, updates or uninstalls the Valheim modpack (BepInEx + mods) in the Valheim game folder.

.DESCRIPTION
    Run in a console without -Mods, -Uninstall or -BepInExOnly, the script shows menus navigated with the arrow keys:
    - Install or update: checkboxes for every mod of the modpack (already installed mods are checked; on a first
      install, every mod except experimental ones). Only checked mods are installed; unchecking an installed mod
      removes it. A mod is experimental when its description starts with "Experimental".
    - Uninstall: checkboxes for the installed mods of the modpack, then whether to delete their settings and to
      remove BepInEx itself.

    - Finds Valheim through Steam (registry + every library listed in libraryfolders.vdf).
    - Installs BepInExPack Valheim from Thunderstore if it is missing.
    - Downloads valheim-modpack.zip from the latest GitHub release and copies the chosen mods into
      BepInEx/plugins/<Mod>/, replacing the previous version. Mods from elsewhere and .cfg files are left untouched.

    Outside an interactive console (CI, redirected input, PowerShell ISE), nothing is asked: the parameters decide,
    and the default is to install every mod except experimental ones.
    Valheim must be closed. For a dedicated server, use install-server.ps1.

.EXAMPLE
    irm https://raw.githubusercontent.com/KiraFR/valheim-modpack/main/install.ps1 | iex

.EXAMPLE
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/KiraFR/valheim-modpack/main/install.ps1))) -Mods StackMax, ChestCraft

.EXAMPLE
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/KiraFR/valheim-modpack/main/install.ps1))) -Uninstall -RemoveBepInEx
#>
[CmdletBinding()]
param(
    # Valheim game folder. Default: detected through Steam.
    [string]$ValheimPath,
    # Release to install, for example v1.2.0. Default: the latest one.
    [string]$Version = 'latest',
    # Local modpack archive to use instead of downloading it.
    [string]$ZipPath,
    # Mods to install, or to uninstall with -Uninstall (for example -Mods StackMax, ChestCraft). No menu.
    [string[]]$Mods,
    # Uninstalls the mods of the modpack: every installed one, or only -Mods. No menu.
    [switch]$Uninstall,
    # With -Uninstall: also deletes the settings of the uninstalled mods (BepInEx/config/valheim.<mod>.*).
    [switch]$RemoveConfig,
    # With -Uninstall: also removes BepInEx, with every other BepInEx mod and setting in the folder.
    [switch]$RemoveBepInEx,
    # Installs BepInEx only, without the mods.
    [switch]$BepInExOnly,
    # Reinstalls BepInEx even if it is already present.
    [switch]$ForceBepInEx
)

# ===== BEGIN SHARED BLOCK: identical in install.ps1 and install-server.ps1, checked by CI =====
# Each script works on its own through irm | iex, so the functions are shared by copy. Change both files together.

$ErrorActionPreference = 'Stop'
# The progress bar makes Invoke-WebRequest extremely slow on Windows PowerShell 5.1.
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$Repository = 'KiraFR/valheim-modpack'
$ArchiveName = 'valheim-modpack.zip'
$BepInExApi = 'https://thunderstore.io/api/experimental/package/denikson/BepInExPack_Valheim/'

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
        # Every drive where Steam installs games is listed in libraryfolders.vdf ("path" "D:\\SteamLibrary"). The list
        # keeps libraries whose drive is gone (unplugged external disk, removed drive): they are skipped here, since
        # Join-Path throws DriveNotFoundException on a missing drive while Test-Path just returns false.
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $library = $m.Groups[1].Value -replace '\\\\', '\'
                if (Test-Path -LiteralPath $library) { $libraries += $library }
            }
        }
    }
    return $libraries | ForEach-Object { $_.TrimEnd('\') } | Sort-Object -Unique
}

# [IO.Path]::Combine rather than Join-Path: it does not require the drive to exist.
function Find-SteamApp([string]$folder, [string]$exe) {
    foreach ($library in Get-SteamLibraries) {
        $path = [IO.Path]::Combine($library, 'steamapps', 'common', $folder)
        if (Test-Path -LiteralPath ([IO.Path]::Combine($path, $exe))) { return $path }
    }
    return $null
}

# Asks for the folder when Steam detection fails. A path copied from the Explorer address bar or "Copy as path" (with
# quotes) is accepted. Returns $null when the answer is empty. A missing drive or characters invalid in a path are
# answered like a wrong folder instead of stopping the script.
function Read-TargetFolder([string]$what, [string]$exe) {
    Write-Host "  $what was not found in the Steam libraries." -ForegroundColor Yellow
    while ($true) {
        $answer = ([string](Read-Host "  Folder holding $exe (empty to cancel)")).Trim().Trim('"')
        if (-not $answer) { return $null }
        $found = $false
        try { $found = Test-Path -LiteralPath ([IO.Path]::Combine($answer, $exe)) } catch { }
        if ($found) { return (Resolve-Path -LiteralPath $answer).Path }
        Write-Host "  $exe is not in $answer" -ForegroundColor Yellow
    }
}

# Only a process started from the target folder is a problem: winhttp.dll is locked there, and replaced mods would
# only load on the next launch anyway. An unreadable process path counts as blocking.
function Assert-NotRunning([string]$target, [string]$processName) {
    $running = Get-Process -Name $processName -ErrorAction SilentlyContinue | Where-Object {
        -not $_.Path -or $_.Path.StartsWith($target.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
    }
    if ($running) { throw "Close $processName first: it is running from $target." }
}

# Menus read keys one by one and redraw in place, which needs a real console. Anywhere else (CI, redirected input,
# PowerShell ISE) nothing is asked and the parameters decide.
function Test-Interactive {
    if ($env:CI -or -not [Environment]::UserInteractive) { return $false }
    if ($Host.Name -ne 'ConsoleHost') { return $false }
    try { return -not [Console]::IsInputRedirected } catch { return $false }
}

# Menu drawn in place in the console: Up/Down move, Enter confirms, Esc cancels. With $multi, each line has a checkbox:
# Space toggles the current one, A checks all, N checks none. $label receives (index, checked) and returns the text of a
# line, so a line can say what will happen to it. Returns the checked indexes (with $multi) or the current index, or
# $null when cancelled.
function Show-Menu([string]$title, [int]$count, [scriptblock]$label, [bool]$multi, [bool[]]$checked) {
    if (-not $checked) { $checked = New-Object bool[] $count }
    if ($multi) { $help = 'Up/Down: move   Space: check/uncheck   A: all   N: none   Enter: confirm   Esc: cancel' }
    else { $help = 'Up/Down: move   Enter: choose   Esc: cancel' }
    $lines = $count + 2

    # Writes the title and reserves the menu lines, then records where they start. Reserving first means that if the
    # console has to scroll, it does so now and $top stays valid for every redraw. Dot-sourced so $top is set here.
    $startFrame = {
        param([bool]$clear)
        if ($clear) { [Console]::Clear() }
        Write-Host ''
        Write-Host "  $title" -ForegroundColor Cyan
        for ($row = 0; $row -lt $lines; $row++) { Write-Host '' }
        $top = [Console]::CursorTop - $lines
    }

    $current = 0
    $done = $false
    $cancelled = $false
    $dirty = $true
    $size = "$([Console]::WindowWidth)x$([Console]::WindowHeight)"
    $resizedAt = $null
    $cursorVisible = [Console]::CursorVisible
    [Console]::CursorVisible = $false
    . $startFrame $false
    try {
        while (-not $done) {
            if ($dirty) {
                $dirty = $false
                # Measured at every draw: every line is padded to the current width to erase the previous text, and
                # cut before it, since a wrapped line would shift the lines below.
                $width = [Math]::Max(20, [Console]::WindowWidth - 1)
                for ($row = 0; $row -lt $count; $row++) {
                    $prefix = '    '
                    if ($row -eq $current) { $prefix = '  > ' }
                    if ($multi) {
                        if ($checked[$row]) { $prefix += '[x] ' } else { $prefix += '[ ] ' }
                    }
                    $text = $prefix + [string](& $label $row $checked[$row])
                    if ($text.Length -gt $width) { $text = $text.Substring(0, $width - 3) + '...' }
                    [Console]::SetCursorPosition(0, $top + $row)
                    if ($row -eq $current) { Write-Host $text.PadRight($width) -NoNewline -ForegroundColor Yellow }
                    else { Write-Host $text.PadRight($width) -NoNewline }
                }
                $text = '  ' + $help
                if ($text.Length -gt $width) { $text = $text.Substring(0, $width) }
                [Console]::SetCursorPosition(0, $top + $count + 1)
                Write-Host $text.PadRight($width) -NoNewline -ForegroundColor DarkGray
            }

            # Keys are polled rather than awaited: resizing the window sends no key, and the terminal reflows the lines
            # already drawn, which leaves $top pointing at the wrong line. Once the size has been stable for a moment,
            # the screen is cleared and the whole menu drawn again.
            if (-not [Console]::KeyAvailable) {
                Start-Sleep -Milliseconds 50
                $seen = "$([Console]::WindowWidth)x$([Console]::WindowHeight)"
                if ($seen -ne $size) {
                    $size = $seen
                    $resizedAt = [DateTime]::UtcNow
                }
                elseif ($resizedAt -and ([DateTime]::UtcNow - $resizedAt).TotalMilliseconds -ge 150) {
                    $resizedAt = $null
                    . $startFrame $true
                    $dirty = $true
                }
                continue
            }

            $key = [Console]::ReadKey($true)
            $dirty = $true
            switch ($key.Key) {
                'UpArrow' { if ($current -gt 0) { $current-- } else { $current = $count - 1 } }
                'DownArrow' { if ($current -lt $count - 1) { $current++ } else { $current = 0 } }
                'Spacebar' { if ($multi) { $checked[$current] = -not $checked[$current] } }
                'A' { if ($multi) { for ($row = 0; $row -lt $count; $row++) { $checked[$row] = $true } } }
                'N' { if ($multi) { for ($row = 0; $row -lt $count; $row++) { $checked[$row] = $false } } }
                'Enter' { $done = $true }
                'Escape' { $done = $true; $cancelled = $true }
            }
        }
    }
    finally {
        [Console]::SetCursorPosition(0, [Math]::Min($top + $lines, [Console]::BufferHeight - 1))
        [Console]::CursorVisible = $cursorVisible
    }

    if ($cancelled) { return $null }
    if (-not $multi) { return , @($current) }
    $result = @()
    for ($row = 0; $row -lt $count; $row++) { if ($checked[$row]) { $result += $row } }
    # The comma keeps an empty selection as an empty array, distinct from $null (cancelled).
    return , $result
}

function Read-YesNo([string]$question, [bool]$default) {
    $hint = '[y/N]'
    if ($default) { $hint = '[Y/n]' }
    while ($true) {
        $answer = ([string](Read-Host "  $question $hint")).Trim().ToLowerInvariant()
        if ($answer -eq '') { return $default }
        if ($answer -eq 'y' -or $answer -eq 'yes') { return $true }
        if ($answer -eq 'n' -or $answer -eq 'no') { return $false }
    }
}

# Downloads and extracts BepInExPack Valheim. The content to copy into the target folder is the folder holding
# winhttp.dll (the Doorstop loader).
function Get-BepInExPack([string]$temp) {
    Write-Step 'Downloading BepInExPack Valheim (Thunderstore)'
    $package = Invoke-RestMethod -Uri $BepInExApi -UseBasicParsing
    $zip = Join-Path $temp 'BepInExPack_Valheim.zip'
    Invoke-WebRequest -Uri $package.latest.download_url -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath (Join-Path $temp 'bepinex')

    $winhttp = Get-ChildItem (Join-Path $temp 'bepinex') -Recurse -Filter 'winhttp.dll' | Select-Object -First 1
    if (-not $winhttp) { throw "Unexpected BepInExPack archive: winhttp.dll not found." }
    return [pscustomobject]@{ Folder = $winhttp.DirectoryName; Version = $package.latest.version_number }
}

function Install-BepInEx([string]$target) {
    $temp = New-TempFolder
    try {
        $pack = Get-BepInExPack $temp
        Copy-Item -Path (Join-Path $pack.Folder '*') -Destination $target -Recurse -Force
        Write-Host "    BepInExPack Valheim $($pack.Version) installed"
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Removes what Install-BepInEx copied: the top-level entries of the BepInExPack archive (winhttp.dll, the doorstop files,
# the BepInEx folder with every plugin and setting in it). The list comes from the archive itself, so no game file is
# ever touched.
function Remove-BepInEx([string]$target) {
    $temp = New-TempFolder
    try {
        $pack = Get-BepInExPack $temp
        Write-Step "Removing BepInEx from $target"
        foreach ($entry in Get-ChildItem $pack.Folder -Force) {
            $path = Join-Path $target $entry.Name
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Recurse -Force
                Write-Host "    $($entry.Name)"
            }
        }
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Version and description of a mod DLL, from its version resource (the .csproj Version and Description).
function Get-ModInfo([string]$dll) {
    if (-not (Test-Path -LiteralPath $dll)) { return [pscustomobject]@{ Version = ''; Description = '' } }
    $info = (Get-Item -LiteralPath $dll).VersionInfo
    return [pscustomobject]@{ Version = ([string]$info.FileVersion -replace '\.0$', ''); Description = [string]$info.Comments }
}

# Downloads (or takes -ZipPath) and extracts the modpack archive. Returns its tag and one entry per mod.
function Get-Modpack([string]$temp) {
    $zip = $ZipPath
    $tag = 'from local archive'
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
        $tag = $release.tag_name
    }

    Expand-Archive -Path $zip -DestinationPath (Join-Path $temp 'modpack')
    $sources = Join-Path $temp 'modpack\BepInEx\plugins'
    if (-not (Test-Path $sources)) { throw "Unexpected modpack archive: BepInEx\plugins is missing." }

    $list = @()
    foreach ($folder in Get-ChildItem $sources -Directory | Sort-Object Name) {
        $info = Get-ModInfo (Join-Path $folder.FullName "$($folder.Name).dll")
        # A mod whose description (its .csproj Description) starts with "Experimental" is only installed when chosen:
        # unchecked by default in the menu, and left out of the install without a menu unless named in -Mods.
        $list += [pscustomobject]@{
            Name = $folder.Name; Folder = $folder.FullName; Version = $info.Version; Description = $info.Description
            Experimental = ($info.Description -match '^\s*Experimental\b')
        }
    }
    if ($list.Count -eq 0) { throw "Unexpected modpack archive: no mod in BepInEx\plugins." }
    return [pscustomobject]@{ Tag = $tag; Mods = $list }
}

# -Mods A,B passed through powershell -File arrives as the single string "A,B", hence the split. Names become folder
# names under BepInEx\plugins, so anything that could point outside of it is refused.
function Split-ModNames([string[]]$values) {
    $names = @()
    foreach ($value in $values) {
        foreach ($part in ($value -split ',')) {
            $part = $part.Trim()
            if (-not $part) { continue }
            if ($part -notmatch '^[A-Za-z0-9_][A-Za-z0-9_.-]*$') { throw "Invalid mod name: $part" }
            if ($names -notcontains $part) { $names += $part }
        }
    }
    return $names
}

# Mods of $names whose folder exists in BepInEx\plugins, in the order of $names.
function Get-InstalledMods([string]$target, [string[]]$names) {
    $plugins = Join-Path $target 'BepInEx\plugins'
    foreach ($name in $names) {
        if (Test-Path -LiteralPath (Join-Path $plugins $name)) { $name }
    }
}

function Get-InstalledVersion([string]$target, [string]$name) {
    return (Get-ModInfo (Join-Path $target "BepInEx\plugins\$name\$name.dll")).Version
}

function Install-Mods([string]$target, [object]$pack, [string[]]$names) {
    $plugins = Join-Path $target 'BepInEx\plugins'
    New-Item -ItemType Directory -Force -Path $plugins | Out-Null

    Write-Step "Installing mods $($pack.Tag) into $plugins"
    foreach ($mod in $pack.Mods) {
        if ($names -notcontains $mod.Name) { continue }

        # The whole mod folder is replaced so no file from an older version is left behind.
        $destination = Join-Path $plugins $mod.Name
        if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
        Copy-Item -LiteralPath $mod.Folder -Destination $destination -Recurse
        Write-Host ("    {0,-12} {1}" -f $mod.Name, $mod.Version)
    }
}

function Uninstall-Mods([string]$target, [string[]]$names, [bool]$removeConfig) {
    $plugins = Join-Path $target 'BepInEx\plugins'
    $config = Join-Path $target 'BepInEx\config'

    Write-Step "Removing mods from $plugins"
    foreach ($name in $names) {
        $folder = Join-Path $plugins $name
        if (Test-Path -LiteralPath $folder) {
            Remove-Item -LiteralPath $folder -Recurse -Force
            Write-Host "    $name removed"
        }
        else {
            Write-Host "    $name was not installed"
        }

        if ($removeConfig -and (Test-Path $config)) {
            # Every file a mod writes there starts with its GUID, valheim.<mod>. (its .cfg, StackMax's item list).
            foreach ($file in Get-ChildItem $config -File -Filter "valheim.$($name.ToLowerInvariant()).*") {
                Remove-Item -LiteralPath $file.FullName -Force
                Write-Host "      $($file.Name) deleted"
            }
        }
    }
}

function Invoke-Install([string]$target, [string[]]$scope, [bool]$interactive, [string]$doneMessage) {
    $hasBepInEx = Test-Path (Join-Path $target 'BepInEx\core\BepInEx.dll')
    if ($BepInExOnly) {
        if ($ForceBepInEx -or -not $hasBepInEx) { Install-BepInEx $target }
        else { Write-Step 'BepInEx already installed' }
        Write-Step 'Done.'
        return
    }

    $temp = New-TempFolder
    try {
        $pack = Get-Modpack $temp
        $allNames = @($pack.Mods | ForEach-Object { $_.Name })
        # $scope limits what is offered (the dedicated server's mods); empty means every mod of the archive.
        $offered = @($pack.Mods | Where-Object { $scope.Count -eq 0 -or $scope -contains $_.Name })
        if ($offered.Count -eq 0) { throw "None of the expected mods is in the modpack $($pack.Tag)." }
        $offeredNames = @($offered | ForEach-Object { $_.Name })
        $installed = @(Get-InstalledMods $target $offeredNames)
        $remove = @()

        if ($Mods) {
            $selected = @()
            foreach ($name in Split-ModNames $Mods) {
                $match = $allNames | Where-Object { $_ -eq $name } | Select-Object -First 1
                if (-not $match) { Write-Warning "$name is not in the modpack, ignored."; continue }
                if ($offeredNames -notcontains $match) { Write-Warning "$match is not needed here, installing it anyway." }
                $selected += $match
            }
            if ($selected.Count -eq 0) { throw 'None of the mods given with -Mods is in the modpack.' }
        }
        elseif ($interactive) {
            # Installed mods are checked, so an update keeps the same set; on a first install, every offered mod except
            # the experimental ones is.
            $checked = New-Object bool[] $offered.Count
            $versions = @{}
            for ($index = 0; $index -lt $offered.Count; $index++) {
                $name = $offered[$index].Name
                $checked[$index] = ($installed -contains $name) -or ($installed.Count -eq 0 -and -not $offered[$index].Experimental)
                $versions[$name] = Get-InstalledVersion $target $name
            }
            $label = {
                param($index, $isChecked)
                $mod = $offered[$index]
                $old = $versions[$mod.Name]
                $state = ''
                if ($installed -contains $mod.Name) {
                    if (-not $isChecked) { $state = 'will be removed' }
                    elseif (-not $old -or $old -eq $mod.Version) { $state = 'installed' }
                    else { $state = "update from $old" }
                }
                elseif ($isChecked) { $state = 'will be installed' }
                '{0,-12} {1,-8} {2,-17} {3}' -f $mod.Name, $mod.Version, $state, $mod.Description
            }.GetNewClosure()

            $chosen = Show-Menu "Mods to install, modpack $($pack.Tag) (unchecking an installed mod removes it)" $offered.Count $label $true $checked
            if ($null -eq $chosen) { Write-Step 'Cancelled, nothing changed.'; return }
            $selected = @($chosen | ForEach-Object { $offered[$_].Name })
            $remove = @($installed | Where-Object { $selected -notcontains $_ })
            if ($selected.Count -eq 0 -and $remove.Count -eq 0) { Write-Step 'No mod checked, nothing changed.'; return }
        }
        else {
            # Without a menu, experimental mods are only installed when named in -Mods.
            $selected = @($offered | Where-Object { -not $_.Experimental } | ForEach-Object { $_.Name })
        }

        if ($selected.Count -gt 0) {
            if ($ForceBepInEx -or -not $hasBepInEx) { Install-BepInEx $target }
            else { Write-Step 'BepInEx already installed' }
        }
        if ($remove.Count -gt 0) { Uninstall-Mods $target $remove $false }
        if ($selected.Count -gt 0) { Install-Mods $target $pack $selected }
        Write-Step "Done. $doneMessage"
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-Uninstall([string]$target, [bool]$interactive) {
    $removeConfig = [bool]$RemoveConfig
    $removeBepInEx = [bool]$RemoveBepInEx
    $descriptions = @{}

    if ($Mods) {
        $names = @(Split-ModNames $Mods)
    }
    else {
        # The names come from the modpack archive, so mods installed from elsewhere are never touched.
        $temp = New-TempFolder
        try {
            $pack = Get-Modpack $temp
            foreach ($mod in $pack.Mods) { $descriptions[$mod.Name] = $mod.Description }
            $names = @(Get-InstalledMods $target @($pack.Mods | ForEach-Object { $_.Name }))
        }
        finally {
            Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    if ($interactive) {
        if ($names.Count -gt 0) {
            $versions = @{}
            foreach ($name in $names) { $versions[$name] = Get-InstalledVersion $target $name }
            $label = {
                param($index, $isChecked)
                $name = $names[$index]
                $state = ''
                if ($isChecked) { $state = 'will be removed' }
                '{0,-12} {1,-8} {2,-16} {3}' -f $name, $versions[$name], $state, $descriptions[$name]
            }.GetNewClosure()
            $all = New-Object bool[] $names.Count
            for ($index = 0; $index -lt $names.Count; $index++) { $all[$index] = $true }

            $chosen = Show-Menu 'Installed mods of the modpack to uninstall' $names.Count $label $true $all
            if ($null -eq $chosen) { Write-Step 'Cancelled, nothing changed.'; return }
            $names = @($chosen | ForEach-Object { $names[$_] })
            if ($names.Count -gt 0) { $removeConfig = Read-YesNo 'Also delete their settings (.cfg files)?' $false }
        }
        else {
            Write-Step 'No mod of the modpack is installed here.'
        }

        if (Test-Path (Join-Path $target 'BepInEx')) {
            $plugins = Join-Path $target 'BepInEx\plugins'
            $others = @(Get-ChildItem $plugins -Directory -ErrorAction SilentlyContinue |
                Where-Object { $names -notcontains $_.Name } | ForEach-Object { $_.Name })
            if ($others.Count -gt 0) {
                Write-Host "  Removing BepInEx would also remove these mods: $($others -join ', ')" -ForegroundColor Yellow
            }
            $removeBepInEx = Read-YesNo 'Also remove BepInEx itself (the mod loader, with every mod and setting it holds)?' $false
        }

        if ($names.Count -eq 0 -and -not $removeBepInEx) { Write-Step 'Nothing changed.'; return }
        if (-not (Read-YesNo 'Proceed?' $true)) { Write-Step 'Cancelled, nothing changed.'; return }
    }

    if ($names.Count -gt 0) { Uninstall-Mods $target $names $removeConfig }
    elseif (-not $removeBepInEx) { Write-Step 'No mod of the modpack is installed here, nothing to remove.' }
    if ($removeBepInEx) { Remove-BepInEx $target }
    Write-Step 'Done.'
}

# Entry point shared by both scripts. $scope: mods offered for installation (empty = every mod of the archive).
function Invoke-Modpack([string]$target, [string]$processName, [string[]]$scope, [string]$doneMessage) {
    Assert-NotRunning $target $processName
    if ($ZipPath) { $script:ZipPath = (Resolve-Path $ZipPath).Path }

    $interactive = (-not ($Uninstall -or $BepInExOnly -or $Mods)) -and (Test-Interactive)
    $uninstalling = [bool]$Uninstall
    if ($interactive) {
        $actions = @('Install or update mods', 'Uninstall')
        $chosen = Show-Menu 'What do you want to do?' $actions.Count { param($index, $isChecked) $actions[$index] }.GetNewClosure() $false $null
        if ($null -eq $chosen) { Write-Step 'Cancelled, nothing changed.'; return }
        $uninstalling = ($chosen[0] -eq 1)
    }

    if ($uninstalling) { Invoke-Uninstall $target $interactive }
    else { Invoke-Install $target $scope $interactive $doneMessage }
}

# ===== END SHARED BLOCK =====

# ----- Main -----

if ($ValheimPath) {
    if (-not (Test-Path $ValheimPath -PathType Container)) { throw "Folder not found: $ValheimPath" }
    $target = (Resolve-Path $ValheimPath).Path
}
else {
    $target = Find-SteamApp 'Valheim' 'valheim.exe'
    if (-not $target -and (Test-Interactive)) { $target = Read-TargetFolder 'Valheim' 'valheim.exe' }
    if (-not $target) { throw "Valheim not found in the Steam libraries. Pass the game folder with -ValheimPath." }
}
if (Test-Path (Join-Path $target 'valheim_server.exe')) {
    throw "$target is a dedicated server folder: use install-server.ps1 instead."
}
Write-Step "Game folder: $target"

Invoke-Modpack $target 'valheim' @() 'Launch Valheim from Steam as usual.'
