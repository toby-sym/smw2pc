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

if ([IO.Path]::GetExtension($romPath) -notin @(".sfc", ".smc")) {
    throw "ROM must be an .sfc or .smc file."
}

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

dotnet publish $projectPath `
    --configuration Release `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $outputPath `
    "-p:PublishSingleFile=true" `
    "-p:IncludeNativeLibrariesForSelfExtract=true" `
    "-p:CompiledRomPath=$romPath"

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Write-Host "Created ROM-specific executable: $(Join-Path $outputPath 'Smw2Pc.Desktop.exe')"
Write-Host "This prototype packages the ROM and runs it with the existing emulator and runtime CPU translator."
