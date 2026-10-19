param(
    [string] $Version = "",
    [string] $OutputDirectory = "artifacts/distribution",
    [string] $RuntimeIdentifier = ""
)

$ErrorActionPreference = "Stop"
$hostIsMacOS = $IsMacOS
$hostIsWindows = $IsWindows
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if (-not $Version) {
    [xml] $versionFile = Get-Content -LiteralPath (Join-Path $repositoryRoot "Version.props") -Raw
    $Version = $versionFile.Project.PropertyGroup.VersionPrefix
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw "Invalid build version: $Version" }
if (-not $RuntimeIdentifier) {
    if ($hostIsMacOS) { $RuntimeIdentifier = if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq [Runtime.InteropServices.Architecture]::Arm64) { "osx-arm64" } else { "osx-x64" } }
    else { $RuntimeIdentifier = "win-x64" }
}
if ($RuntimeIdentifier -notin @("win-x64", "osx-x64", "osx-arm64")) { throw "Unsupported package runtime identifier: $RuntimeIdentifier" }
$isMacOS = $RuntimeIdentifier.StartsWith("osx-")
if (($isMacOS -and -not $hostIsMacOS) -or (-not $isMacOS -and -not $hostIsWindows)) {
    throw "Package $RuntimeIdentifier on its matching native operating system."
}
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repositoryRoot $OutputDirectory }
$workPath = Join-Path $repositoryRoot ("artifacts/builder-package/" + [Guid]::NewGuid().ToString("N"))
$payloadPath = Join-Path $workPath "payload"
$templatePath = Join-Path $payloadPath "player"
$packagePath = Join-Path $workPath "distribution"
$builderPublishPath = Join-Path $workPath "builder-publish"
New-Item -ItemType Directory -Force -Path $templatePath, $packagePath, $builderPublishPath, $outputPath | Out-Null

dotnet publish (Join-Path $repositoryRoot "src/Smw2Pc.Desktop/Smw2Pc.Desktop.csproj") `
    --configuration Release --runtime $RuntimeIdentifier --self-contained true --output $templatePath `
    -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "Player template publishing failed." }

$dotnetRoot = Split-Path (Get-Command dotnet).Source
$referencePack = Get-ChildItem -LiteralPath (Join-Path $dotnetRoot "packs/Microsoft.NETCore.App.Ref") -Directory |
    Where-Object { $_.Name -match '^10\.0\.\d+$' } | Sort-Object { [Version] $_.Name } -Descending | Select-Object -First 1
if (-not $referencePack) { throw "The .NET 10 reference pack is missing." }
$referencesPath = Join-Path $payloadPath "references"
New-Item -ItemType Directory -Force -Path $referencesPath | Out-Null
Copy-Item -Path (Join-Path $referencePack.FullName "ref/net10.0/*.dll") -Destination $referencesPath
$runtimeConfig = Get-Content -LiteralPath (Join-Path $templatePath "SMW.runtimeconfig.json") -Raw | ConvertFrom-Json
$runtimeVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq "Microsoft.NETCore.App" }).version
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME ".nuget/packages" }
$crossgenProject = Join-Path $repositoryRoot "tools/CrossgenCompiler/CrossgenCompiler.csproj"
dotnet restore $crossgenProject "-p:CrossgenVersion=$runtimeVersion" "-p:CrossgenRuntimeIdentifier=$RuntimeIdentifier"
if ($LASTEXITCODE -ne 0) { throw "The .NET $runtimeVersion ReadyToRun compiler restore failed." }
$crossgenPackageName = "microsoft.netcore.app.crossgen2.$RuntimeIdentifier"
$crossgenPackage = Join-Path $nugetRoot (Join-Path $crossgenPackageName $runtimeVersion)
$crossgenExecutable = if ($isMacOS) { "crossgen2" } else { "crossgen2.exe" }
if (-not (Test-Path -LiteralPath (Join-Path $crossgenPackage "tools/$crossgenExecutable"))) {
    throw "The .NET $runtimeVersion ReadyToRun compiler package for $RuntimeIdentifier is missing."
}
$crossgenPayload = Join-Path $payloadPath "crossgen2"
New-Item -ItemType Directory -Force -Path $crossgenPayload | Out-Null
Copy-Item -Path (Join-Path $crossgenPackage "tools/*") -Destination $crossgenPayload -Recurse

$hostPackPath = Join-Path $dotnetRoot "packs/Microsoft.NETCore.App.Host.$RuntimeIdentifier/$runtimeVersion/runtimes/$RuntimeIdentifier/native"
$singleFileHostName = if ($isMacOS) { "singlefilehost" } else { "singlefilehost.exe" }
$singleFileHostCandidates = @(
    (Join-Path $hostPackPath $singleFileHostName),
    (Join-Path $nugetRoot "microsoft.netcore.app.host.$RuntimeIdentifier/$runtimeVersion/runtimes/$RuntimeIdentifier/native/$singleFileHostName")
)
$singleFileHost = $singleFileHostCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $singleFileHost) { throw "The .NET $runtimeVersion single-file host for $RuntimeIdentifier is missing." }
$appHostName = if ($isMacOS) { "apphost" } else { "apphost.exe" }
Copy-Item -LiteralPath $singleFileHost -Destination (Join-Path $payloadPath $appHostName)
if ($isMacOS) { & chmod +x (Join-Path $crossgenPayload $crossgenExecutable) (Join-Path $payloadPath $appHostName) }

foreach ($project in @(@{ Name = "Smw2Pc.Core"; Folder = "core" }, @{ Name = "Smw2Pc.Desktop"; Folder = "desktop" })) {
    $sourceRoot = Join-Path $repositoryRoot ("src/" + $project.Name)
    foreach ($source in (Get-ChildItem -LiteralPath $sourceRoot -Filter "*.cs" -File -Recurse)) {
        $relative = $source.FullName.Substring($sourceRoot.Length).TrimStart([char[]] '\/')
        if ($relative -match '^(bin|obj)[\\/]' -or $source.Name -eq "NativeBlockTranslator.cs") { continue }
        $destination = Join-Path (Join-Path $payloadPath ("source/" + $project.Folder)) $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
        Copy-Item -LiteralPath $source.FullName -Destination $destination
    }
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot "THIRD-PARTY-NOTICES.md") -Destination $templatePath
Add-Type -AssemblyName System.IO.Compression.FileSystem
$payloadZip = Join-Path $workPath "payload.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($payloadPath, $payloadZip, [IO.Compression.CompressionLevel]::Optimal, $false)

dotnet publish (Join-Path $repositoryRoot "src/Smw2Pc.Builder/Smw2Pc.Builder.csproj") `
    --configuration Release --runtime $RuntimeIdentifier --self-contained true --output $builderPublishPath `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true `
    -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" "-p:BuilderPayloadPath=$payloadZip"
if ($LASTEXITCODE -ne 0) { throw "ROM builder publishing failed." }

if ($isMacOS) {
    $appPath = Join-Path $packagePath "SMW2PC.app"
    $contentsPath = Join-Path $appPath "Contents"
    $macOSPath = Join-Path $contentsPath "MacOS"
    $macVersion = $Version.Split('-')[0]
    New-Item -ItemType Directory -Force -Path $macOSPath | Out-Null
    Copy-Item -Path (Join-Path $builderPublishPath "*") -Destination $macOSPath -Recurse
    $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key><string>SMW2PC</string>
  <key>CFBundleIdentifier</key><string>com.smw2pc.builder</string>
  <key>CFBundleName</key><string>SMW2PC</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$macVersion</string>
  <key>CFBundleVersion</key><string>$macVersion</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
"@
    Set-Content -LiteralPath (Join-Path $contentsPath "Info.plist") -Value $plist -Encoding UTF8
    $builderPath = Join-Path $macOSPath "SMW2PC"
    & chmod +x $builderPath
    if ($LASTEXITCODE -ne 0) { throw "Could not mark the macOS builder executable." }
    & codesign --force --deep --sign - $appPath
    if ($LASTEXITCODE -ne 0) { throw "Could not apply an ad-hoc signature to the macOS builder." }
}
else {
    Copy-Item -Path (Join-Path $builderPublishPath "*") -Destination $packagePath -Recurse
    $builderPath = Join-Path $packagePath "SMW2PC.exe"
}

& $builderPath --verify-native
if ($LASTEXITCODE -ne 0) { throw "The packaged builder failed its SDL dependency check." }
Copy-Item -LiteralPath (Join-Path $repositoryRoot "THIRD-PARTY-NOTICES.md") -Destination $packagePath
New-Item -ItemType Directory -Force -Path (Join-Path $packagePath "licenses") | Out-Null
Copy-Item -LiteralPath (Join-Path $repositoryRoot "licenses/Microsoft-MIT.txt") -Destination (Join-Path $packagePath "licenses")
Copy-Item -LiteralPath (Join-Path $crossgenPackage "LICENSE.TXT") -Destination (Join-Path $packagePath "licenses/Crossgen2-MIT.txt")
Copy-Item -LiteralPath (Join-Path $crossgenPackage "THIRD-PARTY-NOTICES.TXT") -Destination (Join-Path $packagePath "licenses/Crossgen2-THIRD-PARTY-NOTICES.TXT")
Copy-Item -LiteralPath (Join-Path $repositoryRoot "assets/fonts/OFL.txt") -Destination (Join-Path $packagePath "licenses")
if ($isMacOS) {
    @"
SMW2PC $Version for macOS ($($RuntimeIdentifier.Replace('osx-', '')))

1. Extract this archive and open SMW2PC.app.
2. Click Add ROM and select your Super Mario World .sfc or .smc file.
3. Wait for SMW.app to appear beside SMW2PC.app, then open it.

The builder includes its compiler and runtime. No .NET SDK, source checkout, or internet connection is needed.
The app is ad-hoc signed for testing and is not notarized for public distribution.
Build errors are recorded in ~/Library/Application Support/SMW2PC/build.log.
"@ | Set-Content -LiteralPath (Join-Path $packagePath "README.txt") -Encoding UTF8
}
else {
    @"
SMW2PC $Version for Windows x64

1. Extract this folder and open SMW2PC.exe.
2. Click Add ROM and select your Super Mario World .sfc or .smc file.
3. Wait for SMW.exe to appear beside SMW2PC.exe, then run it.

No .NET SDK, source checkout, or internet connection is required.
SMW.exe contains your compiled game and the libraries it needs.
Build errors are recorded in %LOCALAPPDATA%\SMW2PC\build.log.
"@ | Set-Content -LiteralPath (Join-Path $packagePath "README.txt") -Encoding UTF8
}

$archivePath = Join-Path $outputPath "SMW2PC-$Version-$RuntimeIdentifier.zip"
if (Test-Path -LiteralPath $archivePath) { throw "Package already exists: $archivePath" }
if ($isMacOS) {
    & /usr/bin/ditto -c -k --sequesterRsrc $packagePath $archivePath
    if ($LASTEXITCODE -ne 0) { throw "Could not create the macOS archive." }
}
else {
    [IO.Compression.ZipFile]::CreateFromDirectory($packagePath, $archivePath, [IO.Compression.CompressionLevel]::Optimal, $false)
}
$checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$checksum  $([IO.Path]::GetFileName($archivePath))" | Set-Content -LiteralPath "$archivePath.sha256" -Encoding ASCII
Write-Host "Created $archivePath"
Write-Host "Builder: $builderPath"
