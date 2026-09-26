param([string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Subnautica')
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$managed = Join-Path $GamePath 'Subnautica_Data\Managed'
$core = Join-Path $GamePath 'BepInEx\core'
$destination = Join-Path $PSScriptRoot '..\BepInEx\plugins\CreatureMorph'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$arguments = @('/nologo', '/target:library', '/optimize+', '/utf8output', ('/out:' + (Join-Path $destination 'CreatureMorph.dll')))
$references = @('Assembly-CSharp.dll', 'Assembly-CSharp-firstpass.dll', 'FMODUnity.dll', 'UnityEngine.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.AnimationModule.dll', 'UnityEngine.PhysicsModule.dll', 'UnityEngine.IMGUIModule.dll', 'UnityEngine.InputLegacyModule.dll', 'UnityEngine.UI.dll', 'UnityEngine.UIModule.dll', 'UnityEngine.TextRenderingModule.dll', 'UnityEngine.AudioModule.dll')
foreach ($reference in $references) { $arguments += '/reference:' + (Join-Path $managed $reference) }
$netstandard = Join-Path $managed 'netstandard.dll'
if (Test-Path $netstandard) { $arguments += '/reference:' + $netstandard }
$arguments += '/reference:' + (Join-Path $core 'BepInEx.dll')
$arguments += '/reference:' + (Join-Path $core '0Harmony.dll')
$arguments += Join-Path $PSScriptRoot 'CreatureMorph.cs'
$arguments += Join-Path $PSScriptRoot 'HealthPolicy.cs'
$arguments += Join-Path $PSScriptRoot 'AnimationPolicy.cs'
$arguments += Join-Path $PSScriptRoot 'CameraPolicy.cs'
$arguments += Join-Path $PSScriptRoot 'PrefabPaths.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Échec de compilation.' }
Write-Host "Compilation réussie : $destination\CreatureMorph.dll"
