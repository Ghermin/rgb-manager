# Installe out\RgbManager.exe comme le ferait l'exe téléchargé, sans aucun message (mode mise à jour) :
# ferme la version installée, copie dans C:\Program Files\RgbManager, tâche de démarrage, raccourcis, relance.
# Une fenêtre admin (UAC) s'ouvre si besoin.
#   pwsh -File deploy.ps1            (après build.ps1)
#   pwsh -File deploy.ps1 -Build     (compile puis installe)
param([switch]$Build)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if ($Build) { & (Join-Path $root 'build.ps1') }

$exe = Join-Path $root 'out\RgbManager.exe'
if (-not (Test-Path $exe)) { throw "out\RgbManager.exe introuvable : lance d'abord build.ps1" }

Start-Process $exe -ArgumentList '--updated'
Write-Host "Installation lancée : journal dans C:\ProgramData\RgbManager\log.txt"
