$ErrorActionPreference = "Stop"
$savedOutput = $env:GITHUB_OUTPUT
$savedRefType = $env:GITHUB_REF_TYPE
$env:GITHUB_OUTPUT = ""
$env:GITHUB_REF_TYPE = "branch"
$createdTag = ""
try {
    $resolver = Join-Path $PSScriptRoot "resolve-build-version.ps1"
    $dev = & $resolver -BuildType dev -RunNumber 42 -RunAttempt 2 | ConvertFrom-Json
    if ($dev.version -notmatch '^\d+\.\d+\.\d+-dev\.42\.2\.g[0-9a-f]{7}$' -or $dev.tag -ne "" -or $dev.channel -ne "dev") { throw "Dev version resolution failed." }
    $release = & $resolver -BuildType release -TagName v1.2.3 | ConvertFrom-Json
    if ($release.version -ne "1.2.3" -or $release.tag -ne "v1.2.3" -or $release.ref -notmatch '^[0-9a-f]{40}$') { throw "Release version resolution failed." }
    foreach ($invalid in @("1.2.3", "v01.2.3", "v1.2", "v1.2.3-beta.1", "v1.2.3`nextra")) {
        $rejected = $false
        try { & $resolver -BuildType release -TagName $invalid | Out-Null }
        catch { $rejected = $true }
        if (-not $rejected) { throw "Invalid release tag was accepted: $invalid" }
    }
    $repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
    $previousCommit = (& git -C $repositoryRoot rev-parse HEAD~1).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Version tests require at least two commits of history." }
    do { $testTag = "v0.0.$(Get-Random -Minimum 50000 -Maximum 65534)" }
    while (& git -C $repositoryRoot tag --list -- $testTag)
    & git -C $repositoryRoot tag $testTag $previousCommit
    if ($LASTEXITCODE -ne 0) { throw "Could not create a temporary version-test tag." }
    $createdTag = $testTag
    $pinned = & $resolver -BuildType release -TagName $testTag | ConvertFrom-Json
    if ($pinned.ref -ne $previousCommit) { throw "Existing release tags must build their tagged commit, not HEAD." }
    Write-Host "Build version checks passed."
}
finally {
    if ($createdTag) { & git -C $repositoryRoot tag -d -- $createdTag | Out-Null }
    $env:GITHUB_OUTPUT = $savedOutput
    $env:GITHUB_REF_TYPE = $savedRefType
}
