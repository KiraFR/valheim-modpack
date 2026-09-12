<#
.SYNOPSIS
    Prints the game version built into assembly_valheim.dll, e.g. "1.0.12 (network 40)".

.DESCRIPTION
    assembly_valheim.dll carries no file or assembly version (both are 0.0.0.0). The game version is set in the
    static constructor of the Version class, `CurrentVersion { get; } = new GameVersion(1, 0, 12)`, so this script
    reads that IL with System.Reflection.Metadata, without loading the assembly. c_networkVersion is a real
    constant and is read from the metadata. Requires PowerShell 7.

.EXAMPLE
    ./Get-ValheimVersion.ps1 "C:\...\valheim_server_Data\Managed\assembly_valheim.dll"
#>
param(
    [Parameter(Mandatory)]
    [string]$Path
)

$ErrorActionPreference = 'Stop'

# Reads an int32 load opcode (ldc.i4.m1, ldc.i4.0-8, ldc.i4.s, ldc.i4) at $pos. Returns @(value, nextPos) or $null.
function Read-IntLoad([byte[]]$il, [int]$pos) {
    if ($pos -ge $il.Length) { return $null }
    $op = $il[$pos]
    if ($op -eq 0x15) { return @(-1, ($pos + 1)) }
    if ($op -ge 0x16 -and $op -le 0x1E) { return @(($op - 0x16), ($pos + 1)) }
    if ($op -eq 0x1F -and $pos + 1 -lt $il.Length) { return @([int][sbyte]$il[$pos + 1], ($pos + 2)) }
    if ($op -eq 0x20 -and $pos + 4 -lt $il.Length) { return @([BitConverter]::ToInt32($il, $pos + 1), ($pos + 5)) }
    return $null
}

$stream = [IO.File]::OpenRead((Resolve-Path $Path).Path)
try {
    $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
    $md = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)

    $type = $null
    foreach ($handle in $md.TypeDefinitions) {
        $candidate = $md.GetTypeDefinition($handle)
        if ($md.GetString($candidate.Name) -eq 'Version' -and $md.GetString($candidate.Namespace) -eq '') { $type = $candidate; break }
    }
    if (-not $type) { throw "Class Version not found in $Path" }

    $network = $null
    $backingToken = $null
    foreach ($handle in $type.GetFields()) {
        $field = $md.GetFieldDefinition($handle)
        switch ($md.GetString($field.Name)) {
            'c_networkVersion' {
                $bytes = $md.GetBlobBytes($md.GetConstant($field.GetDefaultValue()).Value)
                $network = [BitConverter]::ToUInt32($bytes, 0)
            }
            '<CurrentVersion>k__BackingField' {
                $backingToken = [Reflection.Metadata.Ecma335.MetadataTokens]::GetToken([Reflection.Metadata.EntityHandle]$handle)
            }
        }
    }
    if ($null -eq $backingToken) { throw 'Version.CurrentVersion backing field not found' }

    $il = $null
    foreach ($handle in $type.GetMethods()) {
        $method = $md.GetMethodDefinition($handle)
        if ($md.GetString($method.Name) -eq '.cctor') { $il = [Reflection.Metadata.PEReaderExtensions]::GetMethodBody($pe, $method.RelativeVirtualAddress).GetILBytes(); break }
    }
    if (-not $il) { throw 'Version static constructor not found' }

    # Looks for: 3 int loads, newobj GameVersion::.ctor (0x73 + token), stsfld <CurrentVersion>k__BackingField (0x80 + token).
    $version = $null
    for ($i = 0; $i -lt $il.Length -and -not $version; $i++) {
        $pos = $i
        $parts = @()
        for ($n = 0; $n -lt 3; $n++) {
            $load = Read-IntLoad $il $pos
            if (-not $load) { break }
            $parts += $load[0]
            $pos = $load[1]
        }
        if ($parts.Count -ne 3 -or $pos + 9 -ge $il.Length) { continue }
        if ($il[$pos] -ne 0x73 -or $il[$pos + 5] -ne 0x80) { continue }
        if ([BitConverter]::ToInt32($il, $pos + 6) -ne $backingToken) { continue }
        $version = $parts -join '.'
    }
    if (-not $version) { throw 'Version.CurrentVersion initializer not found in the static constructor' }

    "$version (network $network)"
}
finally {
    $stream.Dispose()
}
