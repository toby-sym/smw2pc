param(
    [Parameter(Mandatory = $true)] [string] $Archive,
    [Parameter(Mandatory = $true)] [string] $Version
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$testDirectory = Join-Path $repositoryRoot ("artifacts\package-tests\" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $testDirectory | Out-Null
Expand-Archive -LiteralPath $Archive -DestinationPath $testDirectory
$builder = Join-Path $testDirectory "SMW2PC.exe"
if ((Get-Item -LiteralPath $builder).VersionInfo.ProductVersion.Split('+')[0] -ne $Version) { throw "Packaged executable version does not match the artifact." }
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
try {
    $env:PATH = Join-Path $env:SystemRoot "System32"
    $env:DOTNET_ROOT = Join-Path $testDirectory "no-installed-sdk"
    $process = Start-Process -FilePath $builder -ArgumentList @('--build', ('"' + $romPath + '"')) `
        -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testDirectory "build.stdout.log") `
        -RedirectStandardError (Join-Path $testDirectory "build.stderr.log")
    $processHandle = $process.Handle
    if (-not $process.WaitForExit(600000)) { $process.Kill(); throw "Packaged builder timed out." }
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        Get-Content -LiteralPath (Join-Path $testDirectory "build.stderr.log")
        throw "Packaged ROM compilation failed."
    }
    $game = Join-Path $testDirectory "SMW.exe"
    if (-not (Test-Path -LiteralPath $game)) { throw "The builder did not create SMW.exe beside itself." }
    $reportPath = Join-Path $testDirectory "SMW-compatibility.json"
    if (-not (Test-Path -LiteralPath (Join-Path $testDirectory "SMW-compatibility.html"))) {
        throw "The builder did not export its compatibility report."
    }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($report.BuildStatus -notlike 'Passed*' -or $report.AddressCoverage.Count -eq 0) {
        throw "The compatibility report is missing verification status or discovered code coverage."
    }
    $verified = Start-Process -FilePath $game -ArgumentList "--verify-native" -WindowStyle Hidden -Wait -PassThru
    if ($verified.ExitCode -ne 0) { throw "The generated SMW.exe failed its native dependency check." }
    $gameHash = (Get-FileHash -LiteralPath $game -Algorithm SHA256).Hash
    $invalidRom = Join-Path $testDirectory "invalid.sfc"
    [IO.File]::WriteAllBytes($invalidRom, (New-Object byte[] 1024))
    $rejected = Start-Process -FilePath $builder -ArgumentList @('--build', ('"' + $invalidRom + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($rejected.ExitCode -eq 0 -or (Get-FileHash -LiteralPath $game -Algorithm SHA256).Hash -ne $gameHash) {
        throw "An invalid ROM replaced the previous compiled game."
    }
    Write-Host "Packaged builder created and verified SMW.exe without an installed SDK."
}
finally { $env:PATH = $savedPath; $env:DOTNET_ROOT = $savedRoot }
