param(
    [string] $Version = "",
    [string] $OutputDirectory = "artifacts/distribution",
    [ValidateSet("win-x64", "linux-x64")]
    [string] $RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$isWindows = $RuntimeIdentifier.StartsWith("win-")
$currentPlatformMatches = ($isWindows -and $env:OS -eq "Windows_NT") -or (-not $isWindows -and $IsLinux)
if (-not $currentPlatformMatches) { throw "Build $RuntimeIdentifier packages on a matching Windows or Linux runner." }
if (-not $Version) {
    [xml] $versionFile = Get-Content -LiteralPath (Join-Path $repositoryRoot "Version.props") -Raw
    $Version = $versionFile.Project.PropertyGroup.VersionPrefix
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw "Invalid build version: $Version" }
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repositoryRoot $OutputDirectory }
$workPath = Join-Path (Join-Path $repositoryRoot "artifacts/builder-package") ([Guid]::NewGuid().ToString("N"))
$payloadPath = Join-Path $workPath "payload"
$templatePath = Join-Path $payloadPath "player"
$packagePath = Join-Path $workPath "distribution"
$builderName = if ($isWindows) { "SMW2PC.exe" } else { "SMW2PC" }
$playerName = if ($isWindows) { "SMW.exe" } else { "SMW" }
New-Item -ItemType Directory -Force -Path $templatePath, $packagePath, $outputPath | Out-Null

dotnet publish (Join-Path $repositoryRoot "src/Smw2Pc.Desktop/Smw2Pc.Desktop.csproj") `
    --configuration Release --runtime $RuntimeIdentifier --self-contained true --output $templatePath `
    -p:RuntimeIdentifier=$RuntimeIdentifier -p:PublishSingleFile=false -p:PublishReadyToRun=false `
    -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "Player template publishing failed." }

$sdkListing = @(& dotnet --list-sdks)
$sdkLine = $sdkListing | Select-Object -Last 1
if ($sdkLine -notmatch '\[([^\]]+)\]\s*$') { throw "Cannot locate the .NET SDK installation." }
$dotnetRoot = Split-Path $Matches[1] -Parent
$referencePack = Get-ChildItem -LiteralPath (Join-Path $dotnetRoot "packs/Microsoft.NETCore.App.Ref") -Directory |
    Where-Object { $_.Name -match '^10\.0\.\d+$' } | Sort-Object { [Version] $_.Name } -Descending | Select-Object -First 1
if (-not $referencePack) { throw "The .NET 10 reference pack is missing." }
$referencesPath = Join-Path $payloadPath "references"
New-Item -ItemType Directory -Force -Path $referencesPath | Out-Null
Copy-Item -Path (Join-Path $referencePack.FullName "ref/net10.0/*.dll") -Destination $referencesPath
$runtimeConfig = Get-Content -LiteralPath (Join-Path $templatePath "SMW.runtimeconfig.json") -Raw | ConvertFrom-Json
$runtimeVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq "Microsoft.NETCore.App" }).version
$userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $userProfile ".nuget/packages" }
$crossgenProject = Join-Path $repositoryRoot "tools/CrossgenCompiler/CrossgenCompiler.csproj"
dotnet restore $crossgenProject "-p:CrossgenVersion=$runtimeVersion" "-p:CrossgenRuntimeIdentifier=$RuntimeIdentifier"
if ($LASTEXITCODE -ne 0) { throw "The .NET $runtimeVersion ReadyToRun compiler restore failed." }
$crossgenPackage = Join-Path (Join-Path $nugetRoot "microsoft.netcore.app.crossgen2.$RuntimeIdentifier") $runtimeVersion
$crossgenName = if ($isWindows) { "crossgen2.exe" } else { "crossgen2" }
if (-not (Test-Path -LiteralPath (Join-Path $crossgenPackage "tools/$crossgenName"))) {
    throw "The .NET $runtimeVersion ReadyToRun compiler package is missing for $RuntimeIdentifier."
}
$crossgenPayload = Join-Path $payloadPath "crossgen2"
New-Item -ItemType Directory -Force -Path $crossgenPayload | Out-Null
Copy-Item -Path (Join-Path $crossgenPackage "tools/*") -Destination $crossgenPayload -Recurse
if (-not $isWindows) {
    $crossgenExecutable = Join-Path $crossgenPayload $crossgenName
    $mode = [IO.File]::GetUnixFileMode($crossgenExecutable)
    [IO.File]::SetUnixFileMode($crossgenExecutable,
        $mode -bor [IO.UnixFileMode]::UserExecute -bor [IO.UnixFileMode]::GroupExecute -bor [IO.UnixFileMode]::OtherExecute)
}
$hostCandidates = @(
    (Join-Path $dotnetRoot "packs/Microsoft.NETCore.App.Host.$RuntimeIdentifier/$runtimeVersion/runtimes/$RuntimeIdentifier/native/singlefilehost$(if ($isWindows) { '.exe' })"),
    (Join-Path $nugetRoot "microsoft.netcore.app.host.$RuntimeIdentifier/$runtimeVersion/runtimes/$RuntimeIdentifier/native/singlefilehost$(if ($isWindows) { '.exe' })")
)
$hostPath = $hostCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $hostPath) { throw "The single-file host for .NET $runtimeVersion ($RuntimeIdentifier) is missing." }
Copy-Item -LiteralPath $hostPath -Destination (Join-Path $payloadPath "apphost$(if ($isWindows) { '.exe' })")

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
    --configuration Release --runtime $RuntimeIdentifier --self-contained true --output $packagePath `
    -p:RuntimeIdentifier=$RuntimeIdentifier `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true `
    -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" "-p:BuilderPayloadPath=$payloadZip"
if ($LASTEXITCODE -ne 0) { throw "ROM builder publishing failed." }

$builderPath = Join-Path $packagePath $builderName
if (-not $isWindows) {
    [IO.File]::SetUnixFileMode($builderPath,
        [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute -bor
        [IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::GroupExecute -bor
        [IO.UnixFileMode]::OtherRead -bor [IO.UnixFileMode]::OtherExecute)
}
& $builderPath --verify-native
if ($LASTEXITCODE -ne 0) { throw "The packaged builder failed its SDL dependency check." }
Copy-Item -LiteralPath (Join-Path $repositoryRoot "THIRD-PARTY-NOTICES.md") -Destination $packagePath
New-Item -ItemType Directory -Force -Path (Join-Path $packagePath "licenses") | Out-Null
Copy-Item -LiteralPath (Join-Path $repositoryRoot "licenses/Microsoft-MIT.txt") -Destination (Join-Path $packagePath "licenses")
Copy-Item -LiteralPath (Join-Path $crossgenPackage "LICENSE.TXT") -Destination (Join-Path $packagePath "licenses/Microsoft-Crossgen2-MIT.txt")
Copy-Item -LiteralPath (Join-Path $crossgenPackage "THIRD-PARTY-NOTICES.TXT") -Destination (Join-Path $packagePath "licenses/Crossgen2-THIRD-PARTY-NOTICES.TXT")
Copy-Item -LiteralPath (Join-Path $repositoryRoot "assets/fonts/OFL.txt") -Destination (Join-Path $packagePath "licenses")
@"
SMW2PC $Version for $RuntimeIdentifier

1. Extract this folder and open $builderName.
2. Click Add ROM and select your Super Mario World .sfc or .smc file.
3. Wait for $playerName to appear beside the builder, then run it.

No .NET SDK, source checkout, or internet connection is required.
$playerName contains your compiled game and the libraries it needs.
Build errors are recorded in the SMW2PC build log in your user data folder.
"@ | Set-Content -LiteralPath (Join-Path $packagePath "README.txt") -Encoding UTF8

$archiveExtension = if ($isWindows) { "zip" } else { "tar.gz" }
$archivePath = Join-Path $outputPath "SMW2PC-$Version-$RuntimeIdentifier.$archiveExtension"
if (Test-Path -LiteralPath $archivePath) { throw "Package already exists: $archivePath" }
if ($isWindows) {
    [IO.Compression.ZipFile]::CreateFromDirectory($packagePath, $archivePath, [IO.Compression.CompressionLevel]::Optimal, $false)
} else {
    & tar -czf $archivePath -C $packagePath .
    if ($LASTEXITCODE -ne 0) { throw "Linux archive creation failed." }
}
$checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$checksum  $([IO.Path]::GetFileName($archivePath))" | Set-Content -LiteralPath "$archivePath.sha256" -Encoding ASCII
Write-Host "Created $archivePath"
Write-Host "Builder: $builderPath"
