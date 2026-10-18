param(
    [ValidateSet("dev", "release")]
    [string] $BuildType = "dev",
    [string] $TagName = "",
    [string] $RunNumber = $env:GITHUB_RUN_NUMBER,
    [string] $RunAttempt = $env:GITHUB_RUN_ATTEMPT
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$sourceCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw "Cannot resolve the source commit." }
if ($env:GITHUB_REF_TYPE -eq "tag") { $BuildType = "release"; $TagName = $env:GITHUB_REF_NAME }
if ($BuildType -eq "release") {
    if ($TagName -notmatch '^v((?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))$') {
        throw "A release tag must use vX.Y.Z, for example v0.1.0."
    }
    $version = $Matches[1]
    if (($version.Split('.') | Where-Object { [long] $_ -gt 65534 }).Count) { throw "Version components must be at most 65534." }
    $existingTag = & git -C $repositoryRoot tag --list -- $TagName
    if ($LASTEXITCODE -ne 0) { throw "Cannot inspect release tags." }
    if ($existingTag) {
        $sourceCommit = (& git -C $repositoryRoot rev-parse "refs/tags/$TagName^{commit}").Trim()
        if ($LASTEXITCODE -ne 0) { throw "Cannot resolve the release tag." }
    }
}
else {
    if ($RunNumber -notmatch '^[1-9]\d*$' -or $RunAttempt -notmatch '^[1-9]\d*$') {
        throw "Dev builds require a positive run number and attempt."
    }
    [xml] $versionFile = Get-Content -LiteralPath (Join-Path $repositoryRoot "Version.props") -Raw
    $baseVersion = [string] $versionFile.Project.PropertyGroup.VersionPrefix
    if ($baseVersion -notmatch '^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$') { throw "Invalid Version.props base version." }
    $version = "$baseVersion-dev.$RunNumber.$RunAttempt.g$($sourceCommit.Substring(0, 7))"
    $TagName = ""
}
$result = [ordered] @{
    version = $version
    tag = $TagName
    ref = $sourceCommit
    channel = $BuildType
    artifact = "SMW2PC-$version-win-x64"
}
if ($env:GITHUB_OUTPUT) {
    foreach ($entry in $result.GetEnumerator()) { "$($entry.Key)=$($entry.Value)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8 }
}
$result | ConvertTo-Json -Compress
