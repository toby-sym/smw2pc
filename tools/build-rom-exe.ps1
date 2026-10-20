param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Rom,

    [Parameter(Position = 1)]
    [string] $OutputDirectory = "artifacts\rom-exe",

    [string] $RuntimeIdentifier = "win-x64",

    [ValidateRange(60, 36000)]
    [int] $ProfileFrames = 3600,

    [switch] $SkipVerification
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$romPath = (Resolve-Path -LiteralPath $Rom).Path
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    Join-Path $repositoryRoot $OutputDirectory
}
$projectPath = Join-Path $repositoryRoot "src\Smw2Pc.Desktop\Smw2Pc.Desktop.csproj"
$recompilerProjectPath = Join-Path $repositoryRoot "src\Smw2Pc.Recompiler\Smw2Pc.Recompiler.csproj"
$generatedDirectory = Join-Path $repositoryRoot "artifacts\recomp\source"

if ([IO.Path]::GetExtension($romPath) -notin @(".sfc", ".smc")) {
    throw "ROM must be an .sfc or .smc file."
}

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
New-Item -ItemType Directory -Force -Path $generatedDirectory | Out-Null

dotnet run --project $recompilerProjectPath `
    --configuration Release `
    -- $romPath $generatedDirectory --frames $ProfileFrames

if ($LASTEXITCODE -ne 0) {
    throw "Static ROM analysis failed with exit code $LASTEXITCODE."
}

dotnet publish $projectPath `
    --configuration Release `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $outputPath `
    "-p:PublishSingleFile=true" `
    "-p:IncludeNativeLibrariesForSelfExtract=true" `
    "-p:IncludeAllContentForSelfExtract=true" `
    "-p:PublishReadyToRun=true" `
    "-p:PublishReadyToRunComposite=true" `
    "-p:DebugType=None" `
    "-p:DebugSymbols=false" `
    "-p:CompiledRomPath=$romPath" `
    "-p:RecompiledSourceDirectory=$generatedDirectory"

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$executableName = if ($RuntimeIdentifier.StartsWith("win-")) { "SMW.exe" } else { "SMW" }
$executablePath = Join-Path $outputPath $executableName
Copy-Item -LiteralPath (Join-Path $generatedDirectory "manifest.json") -Destination (Join-Path $outputPath "manifest.json")
Copy-Item -LiteralPath (Join-Path $generatedDirectory "proof.json") -Destination (Join-Path $outputPath "proof.json")
Copy-Item -LiteralPath (Join-Path $generatedDirectory "compatibility.html") -Destination (Join-Path $outputPath "compatibility.html")
Copy-Item -LiteralPath (Join-Path $repositoryRoot "THIRD-PARTY-NOTICES.md") -Destination $outputPath

if (-not $SkipVerification) {
    & $executablePath --verify-native
    if ($LASTEXITCODE -ne 0) {
        throw "The portable player's bundled native dependencies failed verification."
    }
    & $executablePath --verify --report (Join-Path $outputPath "verification.json")
    if ($LASTEXITCODE -ne 0) {
        throw "The compiled player failed its differential proof. See the error above; the artifact is not verified."
    }
}

Write-Host "Created portable recompiled executable: $executablePath"
Write-Host "Both CPU execution paths use generated host code. Uncompiled paths stop with a coverage error; no runtime compiler or CPU interpreter fallback is included."
