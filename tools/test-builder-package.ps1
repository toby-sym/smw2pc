param(
    [Parameter(Mandatory = $true)] [string] $Archive,
    [Parameter(Mandatory = $true)] [string] $Version,
    [string] $RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$testDirectory = Join-Path $repositoryRoot ("artifacts/package-tests/" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $testDirectory | Out-Null

function Invoke-PackagedProcess([string] $Executable, [string[]] $Arguments, [string] $WorkingDirectory, [int] $TimeoutMs = 0) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "Could not start packaged executable: $Executable" }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if ($TimeoutMs -gt 0 -and -not $process.WaitForExit($TimeoutMs)) {
        $process.Kill($true)
        throw "Packaged executable timed out: $Executable"
    }
    $process.WaitForExit()
    return [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $stdoutTask.GetAwaiter().GetResult(); Stderr = $stderrTask.GetAwaiter().GetResult() }
}

function Get-PathHash([string] $Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
    $parts = Get-ChildItem -LiteralPath $Path -File -Recurse | Sort-Object FullName | ForEach-Object {
        "$(($_.FullName.Substring($Path.Length)).Replace('\', '/')):$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
    }
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($parts -join "`n"))))
}

if ($RuntimeIdentifier.StartsWith("osx-")) {
    & /usr/bin/ditto -x -k $Archive $testDirectory
    if ($LASTEXITCODE -ne 0) { throw "Could not extract the macOS package archive." }
    $builder = Join-Path $testDirectory "SMW2PC.app/Contents/MacOS/SMW2PC"
}
else {
    Expand-Archive -LiteralPath $Archive -DestinationPath $testDirectory
    $builder = Join-Path $testDirectory "SMW2PC.exe"
}
if (-not (Test-Path -LiteralPath $builder)) { throw "The archive does not contain the expected $RuntimeIdentifier builder." }
if ($RuntimeIdentifier -eq "win-x64") {
    if ((Get-Item -LiteralPath $builder).VersionInfo.ProductVersion.Split('+')[0] -ne $Version) { throw "Packaged executable version does not match the artifact." }
}
else {
    $versionCheck = Invoke-PackagedProcess $builder @("--version") $testDirectory
    if ($versionCheck.ExitCode -ne 0 -or $versionCheck.Stdout.Trim() -ne $Version) { throw "Packaged executable version does not match the artifact." }
    $mode = [IO.File]::GetUnixFileMode($builder)
    if (($mode -band [IO.UnixFileMode]::UserExecute) -eq 0) { throw "The builder app lost its executable permission in the archive." }
}
if (Get-ChildItem -LiteralPath $testDirectory -Recurse -File | Where-Object { $_.Extension -in @('.sfc', '.smc') }) { throw "A ROM was included in the distribution." }

# A generated infinite loop exercises compilation and bundling without game assets.
$rom = New-Object byte[] 0x8000
$rom[0] = 0x80; $rom[1] = 0xFE
[Text.Encoding]::ASCII.GetBytes("SMW2PC PACKAGE TEST   ").CopyTo($rom, 0x7FC0)
$rom[0x7FD5] = 0x20
$rom[0x7FDC] = 0x34; $rom[0x7FDD] = 0x12
$rom[0x7FDE] = 0xCB; $rom[0x7FDF] = 0xED
$rom[0x7FFC] = 0x00; $rom[0x7FFD] = 0x80
$romPath = Join-Path $testDirectory "synthetic fixture.sfc"
[IO.File]::WriteAllBytes($romPath, $rom)
$savedPath = $env:PATH
$savedRoot = $env:DOTNET_ROOT
$savedDyld = $env:DYLD_LIBRARY_PATH
try {
    if ($RuntimeIdentifier.StartsWith("osx-")) { $env:PATH = "/usr/bin:/bin" }
    else { $env:PATH = Join-Path $env:SystemRoot "System32" }
    $env:DOTNET_ROOT = Join-Path $testDirectory "no-installed-sdk"
    $env:DYLD_LIBRARY_PATH = ""
    $build = Invoke-PackagedProcess $builder @("--build", $romPath) $testDirectory 600000
    if ($build.ExitCode -ne 0) { throw "Packaged ROM compilation failed:`n$($build.Stdout)`n$($build.Stderr)" }
    if ($RuntimeIdentifier.StartsWith("osx-")) {
        $game = Join-Path $testDirectory "SMW.app/Contents/MacOS/SMW"
        if (-not (Test-Path -LiteralPath (Join-Path $testDirectory "SMW.app/Contents/Info.plist"))) { throw "The builder did not create a macOS app bundle." }
        $gameMode = [IO.File]::GetUnixFileMode($game)
        if (($gameMode -band [IO.UnixFileMode]::UserExecute) -eq 0) { throw "The game app lost its executable permission." }
    }
    else { $game = Join-Path $testDirectory "SMW.exe" }
    if (-not (Test-Path -LiteralPath $game)) { throw "The builder did not create the compiled game beside itself." }
    $native = Invoke-PackagedProcess $game @("--verify-native") $testDirectory 120000
    if ($native.ExitCode -ne 0) { throw "The generated game failed its native dependency check:`n$($native.Stdout)`n$($native.Stderr)" }
    $gameHash = Get-PathHash $game
    $invalidRom = Join-Path $testDirectory "invalid.sfc"
    [IO.File]::WriteAllBytes($invalidRom, (New-Object byte[] 1024))
    $rejected = Invoke-PackagedProcess $builder @("--build", $invalidRom) $testDirectory 120000
    if ($rejected.ExitCode -eq 0 -or (Get-PathHash $game) -ne $gameHash) {
        throw "An invalid ROM replaced the previous compiled game."
    }
    Write-Host "Packaged $RuntimeIdentifier builder created and verified the compiled game without an installed SDK."
}
finally { $env:PATH = $savedPath; $env:DOTNET_ROOT = $savedRoot; $env:DYLD_LIBRARY_PATH = $savedDyld }
