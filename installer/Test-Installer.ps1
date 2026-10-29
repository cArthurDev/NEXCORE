$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$wix = Join-Path $projectRoot '.tools\wix\wix.exe'
$buildDir = Join-Path $PSScriptRoot 'build'
$outputDir = Join-Path $PSScriptRoot 'output'
$verificationDir = Join-Path $buildDir ('verification-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $verificationDir -Force | Out-Null
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'NEXCORE.csproj') -Raw -Encoding UTF8
$version = [string]$project.Project.PropertyGroup.Version
$msi = Join-Path $outputDir "NEXCORE-$version-x64.msi"
$setup = Join-Path $outputDir 'NEXCORE-Setup.exe'
$source = Join-Path $verificationDir 'Package.wxs'
$files = Join-Path $verificationDir 'files'
& $wix msi decompile $msi -o $source -x $files
if ($LASTEXITCODE -ne 0) { throw 'Não foi possível inspecionar o MSI.' }
[xml]$package = Get-Content -LiteralPath $source -Raw -Encoding UTF8
$ns = New-Object Xml.XmlNamespaceManager($package.NameTable)
$ns.AddNamespace('w','http://wixtoolset.org/schemas/v4/wxs')
$root = $package.SelectSingleNode('/w:Wix/w:Package', $ns)
if ($root.Name -ne 'NEXCORE' -or $root.Manufacturer -ne 'cArthurDev (Arthur Souza)' -or $root.Version -ne $version -or $root.Language -ne '1046') { throw 'Metadados do MSI incorretos.' }
$launch = $package.SelectSingleNode('//w:Launch', $ns)
if ($launch.Condition -ne 'Installed OR (VersionNT64 AND NEXCORE_WINDOWS_BUILD >= 10240)') { throw 'Verificação de Windows incorreta.' }
$buildSearch = $package.SelectSingleNode('//w:Property[@Id="NEXCORE_WINDOWS_BUILD"]/w:RegistrySearch', $ns)
if ($null -eq $buildSearch -or $buildSearch.Name -ne 'CurrentBuildNumber' -or $buildSearch.Bitness -ne 'always64') { throw 'Detecção da versão real do Windows ausente.' }
if ($null -eq $package.SelectSingleNode('//w:StandardDirectory[@Id="ProgramFiles64Folder"]/w:Directory[@Id="INSTALLFOLDER"]', $ns)) { throw 'Pasta de instalação incorreta.' }
foreach ($shortcut in @('StartMenuShortcut','DesktopAppShortcut')) {
    $shortcutNode = $package.SelectSingleNode('//w:Shortcut[@Id="' + $shortcut + '"]', $ns)
    if ($null -eq $shortcutNode) { throw "Atalho ausente: $shortcut" }
    if ($shortcutNode.Icon -ne 'NexcoreShortcut.exe') { throw "Referência de ícone incorreta: $shortcut" }
}
function Assert-SameHash([string]$expected, [string]$actual) {
    if ((Get-FileHash -LiteralPath $expected -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $actual -Algorithm SHA256).Hash) { throw "Conteúdo diferente: $actual" }
}
Assert-SameHash (Join-Path $buildDir 'app\NEXCORE.exe') (Join-Path $files 'File\ApplicationExe')
Assert-SameHash (Join-Path $buildDir 'NexcoreShortcut.exe') (Join-Path $files 'Icon\NexcoreShortcut.exe')
$iconBytes = [IO.File]::ReadAllBytes((Join-Path $files 'Icon\NexcoreShortcut.exe'))
if ($iconBytes[0] -ne 0x4D -or $iconBytes[1] -ne 0x5A) { throw 'O ícone do atalho não está em formato PE.' }
Add-Type -AssemblyName System.Drawing
$nativeIcon = [Drawing.Icon]::ExtractAssociatedIcon((Join-Path $files 'Icon\NexcoreShortcut.exe'))
if ($null -eq $nativeIcon) { throw 'O Windows não conseguiu extrair o ícone do atalho.' }
$nativeIcon.Dispose()
if (-not ('NexcoreInstallerIconNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NexcoreInstallerIconNative {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint ExtractIconEx(string file, int index, IntPtr large, IntPtr small, uint count);
}
'@
}
if ([NexcoreInstallerIconNative]::ExtractIconEx((Join-Path $files 'Icon\NexcoreShortcut.exe'), -1, [IntPtr]::Zero, [IntPtr]::Zero, 0) -lt 1) { throw 'Recurso de ícone não reconhecido pelo Shell do Windows.' }
foreach ($name in @('LICENSE','THIRD-PARTY-NOTICES.txt','LEIA-ME.txt')) {
    $node = $package.SelectSingleNode('//w:File[@Name="' + $name + '"]', $ns)
    if ($null -eq $node) { throw "Documento ausente: $name" }
    $expected = switch ($name) {
        'LICENSE' { Join-Path $projectRoot 'LICENSE' }
        'LEIA-ME.txt' { Join-Path $projectRoot 'LEIA-ME.txt' }
        default { Join-Path $buildDir $name }
    }
    Assert-SameHash $expected (Join-Path $files ('File\' + $node.Id))
}
# These operations extract data; they do not execute or install the package.
$payloads = Join-Path $verificationDir 'bundle'
$ui = Join-Path $verificationDir 'ui'
& $wix burn extract $setup -o $payloads -oba $ui
if ($LASTEXITCODE -ne 0) { throw 'Não foi possível extrair o Setup para validação.' }
$embeddedMsi = Get-ChildItem -LiteralPath $payloads -Recurse -File -Filter '*.msi' | Select-Object -First 1
if ($null -eq $embeddedMsi) { throw 'O Setup não contém o MSI.' }
Assert-SameHash $msi $embeddedMsi.FullName
$localization = Get-ChildItem -LiteralPath $ui -Recurse -File -Filter '*.wxl' | Where-Object { [IO.File]::ReadAllText($_.FullName).Contains('Aceito os termos da licença') } | Select-Object -First 1
if ($null -eq $localization) { throw 'Interface em português ausente do Setup.' }
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
if (-not ([IO.File]::ReadAllText((Join-Path $outputDir 'NEXCORE-Setup.sha256')).StartsWith($hash))) { throw 'Hash do instalador incorreto.' }
Write-Host 'Validação OK: aplicativo, ícone, autoria, licença, documentos, atalhos, MSI embarcado e interface em português.'
