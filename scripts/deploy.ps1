<#
.SYNOPSIS
  Build BTBridge and link it into the game's ModTek Mods folder.

.DESCRIPTION
  Mods\BTBridge is a directory junction to mod\BTBridge\bin\Release, so every
  rebuild is live on the next game launch. The game must be closed while
  building, or the DLL is locked.

  -Undeploy        remove the junction (leaves ModTek installed)
  -UninstallModTek remove the junction AND ModTek (winhttp.dll, doorstop_config.ini, Mods\ModTek)
#>
param(
    [string]$GameDir = $(if ($env:BATTLETECH_DIR) { $env:BATTLETECH_DIR } else { "F:\SteamLibrary\steamapps\common\BATTLETECH" }),
    [switch]$Undeploy,
    [switch]$UninstallModTek,
    [switch]$NoBuild
)
$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo "mod\BTBridge"
$output = Join-Path $project "bin\Release"
$link = Join-Path $GameDir "Mods\BTBridge"

function Remove-Link {
    if (Test-Path $link) {
        $item = Get-Item $link -Force
        if ($item.LinkType -ne "Junction") {
            throw "$link exists and is not a junction; refusing to delete it"
        }
        # Delete only the junction itself, never the build output it points to.
        [System.IO.Directory]::Delete($link)
        Write-Host "removed junction $link"
    }
}

if ($Undeploy -or $UninstallModTek) {
    Remove-Link
    if ($UninstallModTek) {
        foreach ($p in @("winhttp.dll", "doorstop_config.ini", "Mods\ModTek", "Mods\.modtek")) {
            $full = Join-Path $GameDir $p
            if (Test-Path $full) {
                Remove-Item $full -Recurse -Force
                Write-Host "removed $full"
            }
        }
    }
    return
}

if (-not (Test-Path (Join-Path $GameDir "winhttp.dll"))) {
    throw "ModTek is not installed in $GameDir (winhttp.dll missing)"
}

if (-not $NoBuild) {
    if (Get-Process BattleTech -ErrorAction SilentlyContinue) {
        throw "BattleTech is running; close it before building (the DLL is locked)"
    }
    dotnet build $project -c Release -nologo -v q -p:GameDir="$GameDir"
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

if (-not (Test-Path $link)) {
    New-Item -ItemType Junction -Path $link -Target $output | Out-Null
    Write-Host "linked $link -> $output"
} else {
    Write-Host "junction already present: $link"
}
