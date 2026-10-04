param([switch]$Publish, [switch]$Benchmark, [switch]$RecycleTest)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$projectRoot = $PSScriptRoot
$localSdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $localSdk)) {
    $toolsDir = Join-Path $projectRoot '.tools'
    New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null
    $installer = Join-Path $toolsDir 'dotnet-install.ps1'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
    & $installer -Version '10.0.401' -InstallDir (Join-Path $toolsDir 'dotnet') -NoPath
}
Push-Location $projectRoot
try {
    & $localSdk build 'DedupDesk.slnx' -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    $testArgs = @((Join-Path $projectRoot 'TestResults'))
    if ($Benchmark) { $testArgs += '--benchmark' }
    if ($RecycleTest) { $testArgs += '--recycle-test' }
    & $localSdk run --project 'tests\DedupDesk.Tests\DedupDesk.Tests.csproj' -c Release --no-build -- @testArgs
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    if ($Publish) {
        $output = Join-Path $projectRoot 'artifacts\DedupDesk-win-x64'
        & $localSdk publish 'src\DedupDesk.App\DedupDesk.App.csproj' -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $output
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
        Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $output -Force
        Copy-Item -LiteralPath (Join-Path $projectRoot 'VALIDATION.md') -Destination $output -Force
        $zip = Join-Path $projectRoot 'artifacts\DedupDesk-win-x64.zip'
        Compress-Archive -Path $output -DestinationPath $zip -Force
        Get-FileHash -LiteralPath $zip -Algorithm SHA256 | Format-List
        Write-Output ('Portable application: ' + (Join-Path $output 'DedupDesk.exe'))
    }
} finally { Pop-Location }
