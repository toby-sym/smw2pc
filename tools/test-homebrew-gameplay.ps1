param([string] $OutputDirectory = "artifacts/homebrew-gameplay")
$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$outputPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
dotnet run --project (Join-Path $PSScriptRoot "GameplayChecks/GameplayChecks.csproj") --configuration Release -- --prepare $outputPath
if ($LASTEXITCODE -ne 0) { throw "Homebrew reference replay failed." }
foreach ($region in @("ntsc", "pal")) {
    $fixture = Join-Path $outputPath $region
    $player = Join-Path $fixture "player"
    dotnet build (Join-Path $repositoryRoot "src/Smw2Pc.Desktop/Smw2Pc.Desktop.csproj") --configuration Release --output $player `
        "-p:CompiledRomPath=$(Join-Path $fixture 'homebrew.sfc')" "-p:RecompiledSourceDirectory=$(Join-Path $fixture 'source')"
    if ($LASTEXITCODE -ne 0) { throw "$region compiled player build failed." }
    dotnet (Join-Path $player "SMW.dll") --verify --report (Join-Path $fixture "proof-result.json")
    if ($LASTEXITCODE -ne 0) { throw "$region compiled differential proof failed." }
    dotnet (Join-Path $player "SMW.dll") --verify --replay (Join-Path $fixture "input.json") `
        --expect (Join-Path $fixture "expected.json") --report (Join-Path $fixture "replay-result.json") `
        --screenshot (Join-Path $fixture "final-frame.bmp")
    if ($LASTEXITCODE -ne 0) { throw "$region compiled gameplay replay failed." }
}
Write-Host "PAL and NTSC compiled gameplay match the committed state, video and audio baselines."
