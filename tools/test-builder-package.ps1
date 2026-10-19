param(
    [Parameter(Mandatory = $true)] [string] $Archive,
    [Parameter(Mandatory = $true)] [string] $Version,
    [ValidateSet("win-x64", "linux-x64")]
    [string] $RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$testDirectory = Join-Path $repositoryRoot ("artifacts/package-tests/" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $testDirectory | Out-Null
if ($Archive.EndsWith(".tar.gz", [StringComparison]::OrdinalIgnoreCase)) {
    & tar -xzf $Archive -C $testDirectory
    if ($LASTEXITCODE -ne 0) { throw "Could not extract the Linux builder package." }
} else {
    Expand-Archive -LiteralPath $Archive -DestinationPath $testDirectory
}

$targetIsWindows = $RuntimeIdentifier.StartsWith("win-")
$builderName = if ($targetIsWindows) { "SMW2PC.exe" } else { "SMW2PC" }
$gameName = if ($targetIsWindows) { "SMW.exe" } else { "SMW" }
$builder = Join-Path $testDirectory $builderName
if (-not (Test-Path -LiteralPath $builder)) { throw "Builder executable is missing from the package." }
$builderVersion = if ($targetIsWindows) {
    (Get-Item -LiteralPath $builder).VersionInfo.ProductVersion.Split('+')[0]
} else {
    (& $builder --version).Trim()
}
$versionCheckSucceeded = $targetIsWindows -or $LASTEXITCODE -eq 0
if (-not $versionCheckSucceeded -or $builderVersion -ne $Version) { throw "Packaged executable version does not match the artifact." }
if (Get-ChildItem -LiteralPath $testDirectory -Recurse -File | Where-Object { $_.Extension -in @('.sfc', '.smc') }) { throw "A ROM was included in the distribution." }

function Invoke-PackagedProcess([string] $FilePath, [string[]] $Arguments, [int] $TimeoutSeconds = 600) {
    $start = [Diagnostics.ProcessStartInfo]::new($FilePath)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $testDirectory
    $argumentListProperty = $start.GetType().GetProperty("ArgumentList")
    if ($argumentListProperty) {
        $argumentList = $argumentListProperty.GetValue($start)
        foreach ($argument in $Arguments) { $argumentList.Add($argument) }
    } else {
        $start.Arguments = ($Arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' '
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "Could not start $FilePath." }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $killTreeMethod = $process.GetType().GetMethod("Kill", [type[]] @([bool]))
        if ($killTreeMethod) { $killTreeMethod.Invoke($process, @($true)) } else { $process.Kill() }
        throw "Timed out running $FilePath."
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw "Command failed ($($process.ExitCode)): $FilePath`n$stdout`n$stderr" }
    return $stdout
}

# A generated infinite loop exercises ROM analysis, source generation, compilation and bundling.
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
try {
    $env:PATH = if ($targetIsWindows) { Join-Path $env:SystemRoot "System32" } else { "/usr/bin:/bin" }
    $env:DOTNET_ROOT = Join-Path $testDirectory "no-installed-sdk"
    Invoke-PackagedProcess $builder @('--build', $romPath, '--output', $testDirectory) | Out-Null
    $game = Join-Path $testDirectory $gameName
    if (-not (Test-Path -LiteralPath $game)) { throw "The builder did not create $gameName beside itself." }
    Invoke-PackagedProcess $game @('--verify-native') | Out-Null
    $gameHash = (Get-FileHash -LiteralPath $game -Algorithm SHA256).Hash
    $invalidRom = Join-Path $testDirectory "invalid.sfc"
    [IO.File]::WriteAllBytes($invalidRom, [byte[]]::new(1024))
    try {
        Invoke-PackagedProcess $builder @('--build', $invalidRom, '--output', $testDirectory) | Out-Null
        throw "An invalid ROM was unexpectedly accepted."
    } catch {
        if ($_.Exception.Message -eq "An invalid ROM was unexpectedly accepted.") { throw }
    }
    if ((Get-FileHash -LiteralPath $game -Algorithm SHA256).Hash -ne $gameHash) {
        throw "An invalid ROM replaced the previous compiled game."
    }
    Write-Host "Packaged builder created and verified $gameName without an installed SDK."
}
finally {
    $env:PATH = $savedPath
    $env:DOTNET_ROOT = $savedRoot
}
