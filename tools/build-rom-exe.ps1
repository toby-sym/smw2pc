param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Rom,

    [Parameter(Position = 1)]
    [string] $OutputDirectory = "artifacts\rom-exe",

    [string] $RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$romPath = (Resolve-Path -LiteralPath $Rom).Path
$outputPath = Join-Path $repositoryRoot $OutputDirectory
$projectPath = Join-Path $repositoryRoot "src\Smw2Pc.Desktop\Smw2Pc.Desktop.csproj"
$recompilerProjectPath = Join-Path $repositoryRoot "src\Smw2Pc.Recompiler\Smw2Pc.Recompiler.csproj"
$generatedBlocksPath = Join-Path $repositoryRoot "obj\recomp\GeneratedRomBlocks.g.cs"

if ([IO.Path]::GetExtension($romPath) -notin @(".sfc", ".smc")) {
    throw "ROM must be an .sfc or .smc file."
}

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $generatedBlocksPath) | Out-Null

dotnet run --project $recompilerProjectPath `
    --configuration Release `
    -- $romPath $generatedBlocksPath

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
    "-p:CompiledRomPath=$romPath" `
    "-p:CompiledBlocksPath=$generatedBlocksPath"

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$executableName = if ($RuntimeIdentifier.StartsWith("win-")) { "Smw2Pc.Desktop.exe" } else { "Smw2Pc.Desktop" }
Write-Host "Created ROM-specific executable: $(Join-Path $outputPath $executableName)"
Write-Host "Reachable ROM blocks are statically compiled into the executable. Indirect or writable-RAM code uses the runtime translator."
