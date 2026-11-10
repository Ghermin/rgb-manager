# Compile out/RgbManager.exe pour .NET Framework 4.8 (inclus dans Windows 10/11 : rien à installer pour l'exécuter).
# Compilateur : Roslyn, fourni avec PowerShell 7. Pas besoin de Visual Studio ni du .NET SDK.
#   pwsh -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = Join-Path $root 'src'
$out = Join-Path $root 'out'
New-Item -ItemType Directory -Force -Path $out | Out-Null

if ($PSVersionTable.PSVersion.Major -lt 7) { throw "Lance ce script avec PowerShell 7 (pwsh), qui fournit le compilateur C#." }
Add-Type -AssemblyName Microsoft.CodeAnalysis, Microsoft.CodeAnalysis.CSharp

$parse = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::new([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::Latest)
[Microsoft.CodeAnalysis.SyntaxTree[]]$trees = Get-ChildItem $src -Filter *.cs | ForEach-Object {
    [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($_.FullName), $parse, $_.FullName, [Text.Encoding]::UTF8, [Threading.CancellationToken]::None)
}

# Références : les assemblys de .NET Framework 4.8 installées avec Windows
$fx = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
[Microsoft.CodeAnalysis.MetadataReference[]]$refs = 'mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms', 'System.Security' |
    ForEach-Object { [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile("$fx\$_.dll") }

$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::WindowsApplication).
    WithPlatform([Microsoft.CodeAnalysis.Platform]::X64).
    WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)

$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('RgbManager', $trees, $refs, $options)

# Ressources Windows : manifeste (DPI, compatibilité) + icône de l'exe
$manifest = [IO.File]::OpenRead((Join-Path $src 'app.manifest'))
$icon = [IO.File]::OpenRead((Join-Path $root 'assets\icon.ico'))
try { $win32 = $compilation.CreateDefaultWin32Resources($true, $false, $manifest, $icon) }
finally { $manifest.Dispose(); $icon.Dispose() }

$exe = Join-Path $out 'RgbManager.exe'
$pe = [IO.File]::Create($exe)
try { $result = $compilation.Emit($pe, $null, $null, $win32) } finally { $pe.Dispose() }

$result.Diagnostics | Where-Object { $_.Severity -ge [Microsoft.CodeAnalysis.DiagnosticSeverity]::Warning } | ForEach-Object { Write-Host $_.ToString() }
if (-not $result.Success) { Remove-Item $exe -EA SilentlyContinue; throw "Échec de la compilation" }

Copy-Item (Join-Path $root 'vendor\pawnio\SmbusPIIX4.bin') $out -Force
Write-Host "OK : $exe"
