param([string] $FixtureDirectory = "artifacts/homebrew-gameplay/ntsc")
$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$fixture = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $FixtureDirectory))
dotnet run --project (Join-Path $PSScriptRoot "PlayerUiChecks/PlayerUiChecks.csproj") --configuration Release `
    "-p:CompiledRomPath=$(Join-Path $fixture 'homebrew.sfc')" "-p:RecompiledSourceDirectory=$(Join-Path $fixture 'source')" `
    -- (Join-Path $repositoryRoot "artifacts/player-ui-checks")
if ($LASTEXITCODE -ne 0) { throw "Player UI checks failed." }
