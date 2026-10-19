param([Parameter(Mandatory)][string]$ProjectRoot)
$ErrorActionPreference = 'Stop'
$taskArtifacts = [System.IO.Path]::GetFullPath((Join-Path $ProjectRoot 'artifacts'))
$taskDirectory = Join-Path $taskArtifacts ('installer-monitor-check-' + [Guid]::NewGuid().ToString('N'))
$taskScope = [System.IO.Path]::GetFileName($taskDirectory)
$taskApp = Join-Path $ProjectRoot 'artifacts\publish\CompUsage.exe'
$taskCompiler = Join-Path $ProjectRoot 'artifacts\tools\nsis-3.13\makensis.exe'
$taskChild = $null
try {
    New-Item -ItemType Directory -Path $taskDirectory | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $taskDirectory 'configuration.json'), '{"Enabled":true}')
    # Compile the exact production stop macro with a private object-name scope.
    # No installation, startup registration or real desktop lock occurs in this check.
    $taskInstaller = Get-Content -LiteralPath (Join-Path $ProjectRoot 'installer\CompUsage.nsi') -Raw
    $taskMacro = [regex]::Match($taskInstaller, '(?ms)^!macro StopActivityMonitor\r?\n.*?^!macroend').Value
    if (-not $taskMacro) { throw 'The production collector-stop macro was not found.' }
    $taskMacro = $taskMacro.Replace('Local\CompUsage.Activity.', "Local\CompUsage.Activity.Test.$taskScope.")
    $taskScript = Join-Path $taskDirectory 'stop-check.nsi'
    $taskStopper = Join-Path $taskDirectory 'stop-check.exe'
    $taskMessages = @([regex]::Matches($taskInstaller, '(?m)^\s*MessageBox[^\r\n]*"([^"\r\n]*)"\s*$') | ForEach-Object { $_.Groups[1].Value })
    if (-not ($taskMessages | Where-Object { $_ -match '[^\x00-\x7F]' })) { throw 'No localized installer messages were found.' }
    $taskEncodedText = Join-Path $taskDirectory 'compiled-messages.txt'
    $taskTextCommands = @('  FileOpen $4 "' + $taskEncodedText + '" w')
    foreach ($taskMessage in $taskMessages) { $taskTextCommands += '  FileWriteUTF16LE $4 "' + $taskMessage + '$\r$\n"' }
    $taskTextCommands += '  FileClose $4'
    $taskSource = @('Unicode true', 'RequestExecutionLevel user', 'SilentInstall silent',
        ('OutFile "' + $taskStopper + '"'), '!include "LogicLib.nsh"', $taskMacro,
        'Section', '  !insertmacro StopActivityMonitor') + $taskTextCommands + @('SectionEnd')
    $taskSource = $taskSource -join [Environment]::NewLine
    [System.IO.File]::WriteAllText($taskScript, $taskSource, [System.Text.UTF8Encoding]::new($false))
    & $taskCompiler /INPUTCHARSET UTF8 /WX /V2 $taskScript
    if ($LASTEXITCODE -ne 0) { throw 'The isolated installer-stop check did not compile.' }
    $taskChild = Start-Process -FilePath $taskApp -ArgumentList @('--activity-monitor-check', ('"' + $taskDirectory + '"')) -WindowStyle Hidden -PassThru
    $taskDeadline = [System.Diagnostics.Stopwatch]::StartNew()
    $taskStatus = $null
    while ($taskDeadline.Elapsed.TotalSeconds -lt 10 -and -not $taskChild.HasExited) {
        $taskStatus = Get-ChildItem -LiteralPath $taskDirectory -Filter 'status-*.json' | Select-Object -First 1
        if ($taskStatus) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $taskStatus) { throw 'The isolated collector did not start.' }
    $taskStop = Start-Process -FilePath $taskStopper -WindowStyle Hidden -PassThru -Wait
    if ($taskStop.ExitCode -ne 0 -or -not $taskChild.WaitForExit(5000) -or $taskChild.ExitCode -ne 0) {
        throw 'The production installer macro failed to stop the isolated collector.'
    }
    $taskSaved = Get-Content -LiteralPath $taskStatus.FullName -Raw | ConvertFrom-Json
    if (-not $taskSaved.StoppedAt) { throw 'The collector did not save its graceful-stop marker.' }
    Write-Output 'PASS Installer stop macro shuts down an independently running collector without the main UI.'
    $taskDecodedText = [System.IO.File]::ReadAllText($taskEncodedText, [System.Text.Encoding]::Unicode)
    $taskExpectedText = ($taskMessages -join "`r`n") + "`r`n"
    if ($taskDecodedText -cne $taskExpectedText) { throw 'Compiled installer messages did not preserve their Unicode characters.' }
    Write-Output 'PASS Compiled installer preserves the localized message text, including Hungarian accents.'
} finally {
    if ($taskChild) {
        if (-not $taskChild.HasExited) { $taskChild.Kill(); $taskChild.WaitForExit() }
        $taskChild.Dispose()
    }
    $taskResolved = [System.IO.Path]::GetFullPath($taskDirectory)
    if (-not $taskResolved.StartsWith($taskArtifacts + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The test cleanup path is outside the build artifacts directory.'
    }
    if (Test-Path -LiteralPath $taskResolved) { Remove-Item -LiteralPath $taskResolved -Recurse -Force }
}
