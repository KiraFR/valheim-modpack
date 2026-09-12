<#
.SYNOPSIS
    Installe le modpack Valheim (BepInEx + mods) dans le dossier du jeu ou du serveur dedie.

.DESCRIPTION
    - Trouve Valheim tout seul via Steam (registre + toutes les bibliotheques de libraryfolders.vdf).
    - Installe BepInExPack Valheim depuis Thunderstore s'il est absent.
    - Telecharge valheim-modpack.zip depuis la derniere release GitHub et copie chaque mod dans
      BepInEx/plugins/<Mod>/, en remplacant l'ancienne version. Les autres mods et les .cfg ne sont pas touches.
    - Avec -Server, cible le serveur dedie et n'installe que les mods utiles cote serveur.

    Valheim doit etre ferme pendant l'installation.

.EXAMPLE
    irm https://raw.githubusercontent.com/KiraFR/valheim-modpack/main/install.ps1 | iex

.EXAMPLE
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/KiraFR/valheim-modpack/main/install.ps1))) -Server

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1 -ValheimPath "D:\Jeux\Valheim"
#>
[CmdletBinding()]
param(
    # Dossier de Valheim. Par defaut : detecte via Steam.
    [string]$ValheimPath,
    # Cible le serveur dedie (Valheim dedicated server) au lieu du jeu.
    [switch]$Server,
    # Release a installer, par exemple v1.2.0. Par defaut : la derniere.
    [string]$Version = 'latest',
    # Archive locale du modpack a utiliser au lieu de la telecharger.
    [string]$ZipPath,
    # N'installe que BepInEx, sans les mods.
    [switch]$BepInExOnly,
    # Reinstalle BepInEx meme s'il est deja present.
    [switch]$ForceBepInEx
)

$ErrorActionPreference = 'Stop'
# La barre de progression rend Invoke-WebRequest extremement lent sous Windows PowerShell 5.1.
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$Depot = 'KiraFR/valheim-modpack'
$NomArchive = 'valheim-modpack.zip'
$BepInExApi = 'https://thunderstore.io/api/experimental/package/denikson/BepInExPack_Valheim/'

# Mods a installer sur un serveur dedie : StackMax (les coffres rabotent les piles cote serveur) et PortalMenu
# (seul le serveur connait tous les portails du monde). Les autres sont purement clients.
$ModsServeur = @('StackMax', 'PortalMenu')

function Write-Etape([string]$texte) {
    Write-Host "==> $texte" -ForegroundColor Cyan
}

function New-DossierTemp {
    $chemin = Join-Path ([IO.Path]::GetTempPath()) ('valheim-modpack-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $chemin | Out-Null
    return $chemin
}

function Get-BibliothequesSteam {
    $racines = @()
    foreach ($cle in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        $proprietes = Get-ItemProperty -Path $cle -ErrorAction SilentlyContinue
        if ($proprietes.SteamPath) { $racines += $proprietes.SteamPath }
        if ($proprietes.InstallPath) { $racines += $proprietes.InstallPath }
    }
    $racines += Join-Path ${env:ProgramFiles(x86)} 'Steam'

    $bibliotheques = @()
    foreach ($racine in $racines) {
        $racine = $racine -replace '/', '\'
        if (-not (Test-Path $racine)) { continue }
        $bibliotheques += $racine
        # Chaque disque ou Steam installe des jeux est liste dans libraryfolders.vdf ("path" "D:\\SteamLibrary").
        $vdf = Join-Path $racine 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $bibliotheques += $m.Groups[1].Value -replace '\\\\', '\'
            }
        }
    }
    return $bibliotheques | ForEach-Object { $_.TrimEnd('\') } | Sort-Object -Unique
}

function Find-Valheim([bool]$serveur) {
    if ($serveur) { $dossier = 'Valheim dedicated server'; $exe = 'valheim_server.exe' }
    else { $dossier = 'Valheim'; $exe = 'valheim.exe' }

    foreach ($bibliotheque in Get-BibliothequesSteam) {
        $chemin = Join-Path $bibliotheque "steamapps\common\$dossier"
        if (Test-Path (Join-Path $chemin $exe)) { return $chemin }
    }
    return $null
}

function Install-BepInEx([string]$cible) {
    Write-Etape 'Telechargement de BepInExPack Valheim (Thunderstore)'
    $paquet = Invoke-RestMethod -Uri $BepInExApi -UseBasicParsing
    $temp = New-DossierTemp
    try {
        $zip = Join-Path $temp 'BepInExPack_Valheim.zip'
        Invoke-WebRequest -Uri $paquet.latest.download_url -OutFile $zip -UseBasicParsing
        Expand-Archive -Path $zip -DestinationPath (Join-Path $temp 'x')

        # Le contenu a copier dans le dossier du jeu est celui qui contient winhttp.dll (le chargeur Doorstop).
        $winhttp = Get-ChildItem (Join-Path $temp 'x') -Recurse -Filter 'winhttp.dll' | Select-Object -First 1
        if (-not $winhttp) { throw "Archive BepInExPack inattendue : winhttp.dll introuvable." }
        Copy-Item -Path (Join-Path $winhttp.DirectoryName '*') -Destination $cible -Recurse -Force
        Write-Host "    BepInExPack Valheim $($paquet.latest.version_number) installe"
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Install-Mods([string]$cible, [string]$zip, [bool]$serveur) {
    $temp = New-DossierTemp
    try {
        if (-not $zip) {
            # L'API renvoie la derniere release publiee (hors brouillons et pre-releases) avec son tag et ses fichiers.
            if ($Version -eq 'latest') { $api = "https://api.github.com/repos/$Depot/releases/latest" }
            else { $api = "https://api.github.com/repos/$Depot/releases/tags/$Version" }
            try {
                $release = Invoke-RestMethod -Uri $api -UseBasicParsing
            }
            catch {
                $quoi = "la release $Version"
                if ($Version -eq 'latest') { $quoi = 'aucune release publiee' }
                throw "Modpack introuvable ($quoi) sur https://github.com/$Depot/releases : $($_.Exception.Message)"
            }
            $asset = $release.assets | Where-Object { $_.name -eq $NomArchive } | Select-Object -First 1
            if (-not $asset) { throw "La release $($release.tag_name) ne contient pas $NomArchive." }

            Write-Etape "Telechargement du modpack $($release.tag_name)"
            $zip = Join-Path $temp $NomArchive
            Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing
        }

        Expand-Archive -Path $zip -DestinationPath (Join-Path $temp 'x')
        $sources = Join-Path $temp 'x\BepInEx\plugins'
        if (-not (Test-Path $sources)) { throw "Archive du modpack inattendue : BepInEx\plugins absent." }

        $plugins = Join-Path $cible 'BepInEx\plugins'
        New-Item -ItemType Directory -Force -Path $plugins | Out-Null

        Write-Etape "Installation des mods dans $plugins"
        $installes = 0
        foreach ($mod in Get-ChildItem $sources -Directory) {
            if ($serveur -and $ModsServeur -notcontains $mod.Name) { continue }

            # Le dossier du mod est remplace en entier pour ne pas laisser de fichier d'une ancienne version.
            $destination = Join-Path $plugins $mod.Name
            if (Test-Path $destination) { Remove-Item $destination -Recurse -Force }
            Copy-Item -Path $mod.FullName -Destination $destination -Recurse

            $dll = Join-Path $destination "$($mod.Name).dll"
            $version = ''
            if (Test-Path $dll) { $version = (Get-Item $dll).VersionInfo.FileVersion -replace '\.0$', '' }
            Write-Host ("    {0,-12} {1}" -f $mod.Name, $version)
            $installes++
        }
        if ($installes -eq 0) { throw "Aucun mod trouve dans l'archive." }
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ----- Programme -----

if ($ValheimPath) {
    if (-not (Test-Path $ValheimPath -PathType Container)) { throw "Dossier introuvable : $ValheimPath" }
    $cible = (Resolve-Path $ValheimPath).Path
}
else {
    $cible = Find-Valheim $Server.IsPresent
    if (-not $cible) {
        $quoi = 'Valheim'
        if ($Server) { $quoi = 'Valheim dedicated server' }
        throw "$quoi introuvable dans les bibliotheques Steam. Indiquez le dossier avec -ValheimPath."
    }
}
Write-Etape "Dossier cible : $cible"

# Seul un Valheim lance depuis le dossier cible gene : winhttp.dll y est verrouille, et les mods remplaces ne
# seraient de toute facon charges qu'au prochain lancement. Un chemin illisible compte comme bloquant.
$enCours = Get-Process -Name 'valheim', 'valheim_server' -ErrorAction SilentlyContinue | Where-Object {
    -not $_.Path -or $_.Path.StartsWith($cible.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}
if ($enCours) {
    throw "Fermez $($enCours[0].ProcessName) avant d'installer : il tourne depuis $cible."
}

if ($ZipPath) { $ZipPath = (Resolve-Path $ZipPath).Path }

if ($ForceBepInEx -or -not (Test-Path (Join-Path $cible 'BepInEx\core\BepInEx.dll'))) {
    Install-BepInEx $cible
}
else {
    Write-Etape 'BepInEx deja installe'
}

if (-not $BepInExOnly) {
    Install-Mods $cible $ZipPath $Server.IsPresent
}

Write-Etape 'Termine. Lancez Valheim normalement depuis Steam.'
