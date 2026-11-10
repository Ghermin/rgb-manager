# Installe out/ dans C:\Program Files\RgbManager, (re)crée la tâche de démarrage, relance l'appli
# et met à jour le raccourci du bureau. Demande les droits admin si besoin.
#   pwsh -File deploy.ps1            (après build.ps1)
#   pwsh -File deploy.ps1 -Build     (compile puis installe)
param([switch]$Build, [string]$Desktop)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if ($Build) { & (Join-Path $root 'build.ps1') }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    # Le bureau est celui de l'utilisateur qui lance le script
    $desk = [Environment]::GetFolderPath('Desktop')
    $p = Start-Process (Get-Process -Id $PID).Path -Verb RunAs -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Desktop', "`"$desk`"")
    $p.WaitForExit()
    exit $p.ExitCode
}

$exe = Join-Path $root 'out\RgbManager.exe'
if (-not (Test-Path $exe)) { throw "out\RgbManager.exe introuvable : lance d'abord build.ps1" }
$dir = Join-Path $env:ProgramW6432 'RgbManager'
if (-not $env:ProgramW6432) { $dir = 'C:\Program Files\RgbManager' }

Write-Host "Arrêt de l'appli…"
schtasks.exe /End /TN RgbManager 2>$null | Out-Null
Get-Process RgbManager -EA SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

Write-Host "Copie dans $dir…"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Get-ChildItem $dir | Remove-Item -Recurse -Force
Copy-Item (Join-Path $root 'out\*') $dir -Force

Write-Host "Tâche de démarrage + lancement…"
# --install recopie (déjà fait), recrée la tâche planifiée et lance l'appli installée
$p = Start-Process (Join-Path $dir 'RgbManager.exe') -ArgumentList '--install', '--no-elevate' -PassThru
$p.WaitForExit()

if (-not $Desktop) { $Desktop = [Environment]::GetFolderPath('Desktop') }
$lnk = Join-Path $Desktop 'Gestionnaire RGB.lnk'
$sh = New-Object -ComObject WScript.Shell
$s = $sh.CreateShortcut($lnk)
$s.TargetPath = Join-Path $dir 'RgbManager.exe'
$s.WorkingDirectory = $dir
$s.IconLocation = "$(Join-Path $dir 'RgbManager.exe'),0"
$s.Description = 'Gestionnaire RGB : carte mère, RAM, watercooling, boîtier'
$s.Save()
Write-Host "Raccourci : $lnk"
Write-Host "OK"
