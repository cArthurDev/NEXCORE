$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$wix = Join-Path $projectRoot '.tools\wix\wix.exe'
$buildDir = Join-Path $PSScriptRoot 'build'
$publishDir = Join-Path $buildDir 'app'
$outputDir = Join-Path $PSScriptRoot 'output'
New-Item -ItemType Directory -Path $buildDir,$publishDir,$outputDir -Force | Out-Null
Push-Location $projectRoot
try {
    if (-not (Test-Path -LiteralPath $wix)) {
        & $dotnet tool install wix --version 5.0.2 --tool-path (Join-Path $projectRoot '.tools\wix')
        if ($LASTEXITCODE -ne 0) { throw 'Não foi possível preparar o WiX local.' }
    }
    & $wix extension add WixToolset.UI.wixext/5.0.2 WixToolset.BootstrapperApplications.wixext/5.0.2
    if ($LASTEXITCODE -ne 0) { throw 'Não foi possível preparar as extensões do instalador.' }
    [xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'NEXCORE.csproj') -Raw -Encoding UTF8
    $version = [string]$project.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Versão inválida no projeto.' }
    & $dotnet publish (Join-Path $projectRoot 'NEXCORE.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $publishDir -nologo -v minimal
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $publishDir 'NEXCORE.exe'))) { throw 'Falha na publicação do aplicativo.' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LEIA-ME.txt') -Destination $publishDir

    # Compile a tiny PE resource container instead of putting an ICO in an advertised
    # shortcut's Icon table, or duplicating the entire 190 MB application there.
    $sdkList = @(& $dotnet --list-sdks)
    $sdkLine = $sdkList | Where-Object { $_ -match '^8\.' } | Select-Object -Last 1
    if ($sdkLine -notmatch '^(\S+)\s+\[(.+)\]$') { throw 'SDK .NET 8 não encontrado para gerar o recurso de ícone.' }
    $sdkVersion = $Matches[1]
    # Derive Unicode paths locally; Windows PowerShell may decode native stdout
    # using a different code page when the checkout contains characters such as Ç.
    $dotnetFolder = Split-Path $dotnet -Parent
    $sdkFolder = Join-Path $dotnetFolder 'sdk'
    $csc = Join-Path $sdkFolder ($sdkVersion + '\Roslyn\bincore\csc.dll')
    $referencePack = Get-ChildItem -LiteralPath (Join-Path $dotnetFolder 'packs\Microsoft.NETCore.App.Ref') -Directory | Where-Object { $_.Name -like '8.*' } | Sort-Object { [version]$_.Name } | Select-Object -Last 1
    if ($null -eq $referencePack) { throw 'Referências do .NET 8 não encontradas.' }
    $runtimeReference = Join-Path $referencePack.FullName 'ref\net8.0\System.Runtime.dll'
    $iconSource = Join-Path $buildDir 'IconResource.cs'
    [IO.File]::WriteAllText($iconSource, 'internal static class NexcoreIconResource {}')
    & $dotnet $csc /nologo /noconfig /nostdlib+ /target:library ("/reference:$runtimeReference") ("/win32icon:" + (Join-Path $projectRoot 'Assets\app.ico')) ("/out:" + (Join-Path $buildDir 'NexcoreShortcut.exe')) $iconSource
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao gerar o recurso de ícone do atalho.' }

    # Render the existing badge into the format required by the native setup UI.
    Add-Type -AssemblyName System.Drawing
    $badge = [Drawing.Image]::FromFile((Join-Path $projectRoot 'Assets\logo-badge.png'))
    $bitmap = New-Object Drawing.Bitmap(64,64)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([Drawing.Color]::White)
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $scale = [Math]::Min(64 / $badge.Width, 64 / $badge.Height)
        $width = [int]($badge.Width * $scale); $height = [int]($badge.Height * $scale)
        $graphics.DrawImage($badge, [int]((64-$width)/2), [int]((64-$height)/2), $width, $height)
        $bitmap.Save((Join-Path $buildDir 'InstallerLogo.bmp'), [Drawing.Imaging.ImageFormat]::Bmp)
    } finally { $graphics.Dispose(); $bitmap.Dispose(); $badge.Dispose() }

    $licenseText = [IO.File]::ReadAllText((Join-Path $projectRoot 'LICENSE'))
    $rtf = $licenseText.Replace('\','\\').Replace('{','\{').Replace('}','\}').Replace("`r",'').Replace("`n",'\par ')
    [IO.File]::WriteAllText((Join-Path $buildDir 'License.rtf'), '{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs18 ' + $rtf + '}', [Text.Encoding]::ASCII)

    # Preserve the package license metadata alongside the application license.
    $assets = Get-Content -LiteralPath (Join-Path $projectRoot 'obj\project.assets.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
    $notices = New-Object Text.StringBuilder
    [void]$notices.AppendLine('NEXCORE - Third-party package notices')
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package') { continue }
        foreach ($packageRoot in $packageRoots) {
            $packagePath = Join-Path $packageRoot $library.Value.path
            $nuspec = Get-ChildItem -LiteralPath $packagePath -Filter '*.nuspec' -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($null -eq $nuspec) { continue }
            [xml]$metadata = [IO.File]::ReadAllText($nuspec.FullName)
            $licenseNode = $metadata.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='license']")
            $urlNode = $metadata.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='projectUrl']")
            [void]$notices.AppendLine("`r`n--- $($library.Name) ---")
            if ($null -ne $urlNode) { [void]$notices.AppendLine($urlNode.InnerText) }
            if ($null -ne $licenseNode) {
                [void]$notices.AppendLine('License: ' + $licenseNode.InnerText)
                if ($licenseNode.type -eq 'file') {
                    $licensePath = Join-Path $packagePath $licenseNode.InnerText
                    if (Test-Path -LiteralPath $licensePath) { [void]$notices.AppendLine([IO.File]::ReadAllText($licensePath)) }
                }
            }
            foreach ($notice in Get-ChildItem -LiteralPath $packagePath -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE|THIRD-PARTY)' }) {
                [void]$notices.AppendLine([IO.File]::ReadAllText($notice.FullName))
            }
            break
        }
    }
    [IO.File]::WriteAllText((Join-Path $buildDir 'THIRD-PARTY-NOTICES.txt'), $notices.ToString(), (New-Object Text.UTF8Encoding($false)))

    $defines = @('-d',"ProjectRoot=$projectRoot",'-d',"BuildDir=$buildDir",'-d',"PublishDir=$publishDir",'-d',"AppVersion=$version")
    $msi = Join-Path $buildDir "NEXCORE-$version-x64.msi"
    & $wix build (Join-Path $PSScriptRoot 'Package.wxs') @defines -arch x64 -culture pt-BR -ext WixToolset.UI.wixext -pdbtype none -o $msi
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o pacote MSI.' }
    $setup = Join-Path $outputDir 'NEXCORE-Setup.exe'
    & $wix build (Join-Path $PSScriptRoot 'Bundle.wxs') @defines -arch x64 -ext WixToolset.BootstrapperApplications.wixext -pdbtype none -o $setup
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o Setup.exe.' }
    Copy-Item -LiteralPath $msi -Destination $outputDir
    $hash = Get-FileHash -LiteralPath $setup -Algorithm SHA256
    [IO.File]::WriteAllText((Join-Path $outputDir 'NEXCORE-Setup.sha256'), $hash.Hash + '  NEXCORE-Setup.exe' + [Environment]::NewLine)
    & (Join-Path $PSScriptRoot 'Test-Installer.ps1')
    Write-Host "Instalador pronto: $setup"
} finally { Pop-Location }
