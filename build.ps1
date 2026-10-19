param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot 'artifacts\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot 'artifacts\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE" }
}

Invoke-Checked 'dotnet' @('restore', 'CompUsage.slnx', '--source', 'https://api.nuget.org/v3/index.json', '-r', 'win-x64', '-p:SelfContained=true')
if (-not $SkipTests) {
    Invoke-Checked 'dotnet' @('run', '--project', 'tests\CompUsage.Checks', '-c', 'Release', '--no-restore')
    Invoke-Checked 'npm.cmd' @('ci', '--cache', 'artifacts\npm-cache')
    Invoke-Checked 'npm.cmd' @('test')
}
Invoke-Checked 'dotnet' @('publish', 'src\CompUsage.App', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore', '-p:DebugType=None', '-o', 'artifacts\publish')
if (-not $SkipTests) {
    $monitorCheck = Start-Process -FilePath (Join-Path $projectRoot 'artifacts\publish\CompUsage.exe') -ArgumentList '--verify-activity-monitor' -WindowStyle Hidden -PassThru -Wait
    if ($monitorCheck.ExitCode -ne 0) { throw "Activity monitor verification failed with exit code $($monitorCheck.ExitCode)" }
}
Copy-Item -LiteralPath 'README.md', 'THIRD-PARTY-NOTICES.md' -Destination 'artifacts\publish'
$runtimeConfig = Get-Content -LiteralPath 'artifacts\publish\CompUsage.runtimeconfig.json' -Raw | ConvertFrom-Json
$runtimeVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
$runtimePackage = Join-Path $env:NUGET_PACKAGES "microsoft.netcore.app.runtime.win-x64\$runtimeVersion"
New-Item -ItemType Directory -Force -Path 'artifacts\publish\licenses' | Out-Null
Copy-Item -LiteralPath (Join-Path $runtimePackage 'LICENSE.TXT') -Destination 'artifacts\publish\licenses\dotnet-LICENSE.txt'
Copy-Item -LiteralPath (Join-Path $runtimePackage 'THIRD-PARTY-NOTICES.TXT') -Destination 'artifacts\publish\licenses\dotnet-THIRD-PARTY-NOTICES.txt'

$toolsDirectory = Join-Path $projectRoot 'artifacts\tools'
$dependenciesDirectory = Join-Path $projectRoot 'installer\dependencies'
$releaseDirectory = Join-Path $projectRoot 'artifacts\release'
New-Item -ItemType Directory -Force -Path $toolsDirectory, $dependenciesDirectory, $releaseDirectory | Out-Null
$compilerArchive = Join-Path $toolsDirectory 'nsis.zip'
$compilerHash = 'BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8'
$validArchive = (Test-Path -LiteralPath $compilerArchive) -and (Get-FileHash -LiteralPath $compilerArchive -Algorithm SHA256).Hash -eq $compilerHash
if (-not $validArchive) {
    # Some SourceForge gateways return an HTML mirror-selection page with HTTP 200.
    # Never extract that response; try another official mirror and require the same pinned hash.
    foreach ($mirror in @('downloads.sourceforge.net', 'master.dl.sourceforge.net', 'netix.dl.sourceforge.net', 'phoenixnap.dl.sourceforge.net')) {
        try {
            Invoke-Checked 'curl.exe' @('-fL', '--retry', '2', '--max-time', '120', "https://$mirror/project/nsis/NSIS%203/3.13/nsis-3.13.zip", '-o', "$compilerArchive.download")
            if ((Get-FileHash -LiteralPath "$compilerArchive.download" -Algorithm SHA256).Hash -eq $compilerHash) {
                Move-Item -LiteralPath "$compilerArchive.download" -Destination $compilerArchive -Force
                $validArchive = $true
                break
            }
            Write-Warning "The response from $mirror is not the pinned NSIS archive."
        } catch { Write-Warning $_ }
    }
    if (-not $validArchive) { throw 'Unable to download the NSIS archive with its pinned checksum.' }
}
$compiler = Join-Path $toolsDirectory 'nsis-3.13\makensis.exe'
if (-not (Test-Path -LiteralPath $compiler)) { Expand-Archive -LiteralPath $compilerArchive -DestinationPath $toolsDirectory }
$bootstrapper = Join-Path $dependenciesDirectory 'MicrosoftEdgeWebview2Setup.exe'
if (-not (Test-Path -LiteralPath $bootstrapper)) {
    Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $bootstrapper
}
$signature = Get-AuthenticodeSignature -LiteralPath $bootstrapper
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Microsoft Corporation') {
    throw 'The WebView2 bootstrapper must have a valid Microsoft signature.'
}

# Generate an explicit uninstall list so uninstall never recursively deletes unknown files.
$publishDirectory = (Resolve-Path -LiteralPath 'artifacts\publish').Path
$uninstallLines = [System.Collections.Generic.List[string]]::new()
foreach ($file in Get-ChildItem -LiteralPath $publishDirectory -File -Recurse) {
    $relative = [System.IO.Path]::GetRelativePath($publishDirectory, $file.FullName)
    $uninstallLines.Add('Delete "$INSTDIR\' + $relative + '"')
}
foreach ($directory in Get-ChildItem -LiteralPath $publishDirectory -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending) {
    $relative = [System.IO.Path]::GetRelativePath($publishDirectory, $directory.FullName)
    $uninstallLines.Add('RMDir "$INSTDIR\' + $relative + '"')
}
[System.IO.File]::WriteAllLines((Join-Path $projectRoot 'installer\uninstall-files.nsh'), $uninstallLines, [System.Text.UTF8Encoding]::new($false))
if (-not $SkipTests) { & (Join-Path $projectRoot 'tests\installer-monitor-check.ps1') -ProjectRoot $projectRoot }
Invoke-Checked $compiler @('/WX', '/V2', 'installer\CompUsage.nsi')
Compress-Archive -Path 'artifacts\publish\*' -DestinationPath 'artifacts\release\CompUsage-1.0.3-portable-x64.zip' -Force
Get-ChildItem -LiteralPath $releaseDirectory -File | Get-FileHash -Algorithm SHA256 | Format-Table
