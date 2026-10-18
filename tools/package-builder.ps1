param(
    [string] $Version = "",
    [string] $OutputDirectory = "artifacts\distribution"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if (-not $Version) {
    [xml] $versionFile = Get-Content -LiteralPath (Join-Path $repositoryRoot "Version.props") -Raw
    $Version = $versionFile.Project.PropertyGroup.VersionPrefix
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw "Invalid build version: $Version" }
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repositoryRoot $OutputDirectory }
$workPath = Join-Path $repositoryRoot ("artifacts\builder-package\" + [Guid]::NewGuid().ToString("N"))
$payloadPath = Join-Path $workPath "payload"
$templatePath = Join-Path $payloadPath "player"
$packagePath = Join-Path $workPath "distribution"
New-Item -ItemType Directory -Force -Path $templatePath, $packagePath, $outputPath | Out-Null

dotnet publish (Join-Path $repositoryRoot "src\Smw2Pc.Desktop\Smw2Pc.Desktop.csproj") `
    --configuration Release --runtime win-x64 --self-contained true --output $templatePath `
    -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "Player template publishing failed." }

$dotnetRoot = Split-Path (Get-Command dotnet).Source
$referencePack = Get-ChildItem -LiteralPath (Join-Path $dotnetRoot "packs\Microsoft.NETCore.App.Ref") -Directory |
    Where-Object { $_.Name -match '^10\.0\.\d+$' } | Sort-Object { [Version] $_.Name } -Descending | Select-Object -First 1
if (-not $referencePack) { throw "The .NET 10 reference pack is missing." }
$referencesPath = Join-Path $payloadPath "references"
New-Item -ItemType Directory -Force -Path $referencesPath | Out-Null
Copy-Item -Path (Join-Path $referencePack.FullName "ref\net10.0\*.dll") -Destination $referencesPath
$runtimeConfig = Get-Content -LiteralPath (Join-Path $templatePath "SMW.runtimeconfig.json") -Raw | ConvertFrom-Json
$runtimeVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq "Microsoft.NETCore.App" }).version
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget\packages" }
$hostCandidates = @(
    (Join-Path $dotnetRoot "packs\Microsoft.NETCore.App.Host.win-x64\$runtimeVersion\runtimes\win-x64\native\singlefilehost.exe"),
    (Join-Path $nugetRoot "microsoft.netcore.app.host.win-x64\$runtimeVersion\runtimes\win-x64\native\singlefilehost.exe")
)
$hostPath = $hostCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $hostPath) { throw "The Windows single-file host for .NET $runtimeVersion is missing." }
Copy-Item -LiteralPath $hostPath -Destination (Join-Path $payloadPath "apphost.exe")

foreach ($project in @(@{ Name = "Smw2Pc.Core"; Folder = "core" }, @{ Name = "Smw2Pc.Desktop"; Folder = "desktop" })) {
    $sourceRoot = Join-Path $repositoryRoot ("src\" + $project.Name)
    foreach ($source in (Get-ChildItem -LiteralPath $sourceRoot -Filter "*.cs" -File -Recurse)) {
        $relative = $source.FullName.Substring($sourceRoot.Length).TrimStart([char[]] '\/')
        if ($relative -match '^(bin|obj)[\\/]' -or $source.Name -eq "NativeBlockTranslator.cs") { continue }
        $destination = Join-Path (Join-Path $payloadPath ("source\" + $project.Folder)) $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
        Copy-Item -LiteralPath $source.FullName -Destination $destination
    }
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot "THIRD-PARTY-NOTICES.md") -Destination $templatePath
Add-Type -AssemblyName System.IO.Compression.FileSystem
$payloadZip = Join-Path $workPath "payload.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($payloadPath, $payloadZip, [IO.Compression.CompressionLevel]::Optimal, $false)

dotnet publish (Join-Path $repositoryRoot "src\Smw2Pc.Builder\Smw2Pc.Builder.csproj") `
    --configuration Release --runtime win-x64 --self-contained true --output $packagePath `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true `
    -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" "-p:BuilderPayloadPath=$payloadZip"
if ($LASTEXITCODE -ne 0) { throw "ROM builder publishing failed." }

$builderPath = Join-Path $packagePath "SMW2PC.exe"
$nativeCheck = Start-Process -FilePath $builderPath -ArgumentList "--verify-native" -WindowStyle Hidden -Wait -PassThru
if ($nativeCheck.ExitCode -ne 0) { throw "The packaged builder failed its SDL dependency check." }
Copy-Item -LiteralPath (Join-Path $repositoryRoot "THIRD-PARTY-NOTICES.md") -Destination $packagePath
New-Item -ItemType Directory -Force -Path (Join-Path $packagePath "licenses") | Out-Null
Copy-Item -LiteralPath (Join-Path $repositoryRoot "licenses\Microsoft-MIT.txt") -Destination (Join-Path $packagePath "licenses")
Copy-Item -LiteralPath (Join-Path $repositoryRoot "assets\fonts\OFL.txt") -Destination (Join-Path $packagePath "licenses")
@"
SMW2PC $Version for Windows x64

1. Extract this folder and open SMW2PC.exe.
2. Click Add ROM and select your Super Mario World .sfc or .smc file.
3. Wait for SMW.exe to appear beside SMW2PC.exe, then run it.

No .NET SDK, source checkout, or internet connection is required.
SMW.exe contains your compiled game and the libraries it needs.
Build errors are recorded in %LOCALAPPDATA%\SMW2PC\build.log.
"@ | Set-Content -LiteralPath (Join-Path $packagePath "README.txt") -Encoding UTF8
$archivePath = Join-Path $outputPath "SMW2PC-$Version-win-x64.zip"
if (Test-Path -LiteralPath $archivePath) { throw "Package already exists: $archivePath" }
[IO.Compression.ZipFile]::CreateFromDirectory($packagePath, $archivePath, [IO.Compression.CompressionLevel]::Optimal, $false)
$checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$checksum  $([IO.Path]::GetFileName($archivePath))" | Set-Content -LiteralPath "$archivePath.sha256" -Encoding ASCII
Write-Host "Created $archivePath"
Write-Host "Builder: $builderPath"
