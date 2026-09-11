#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$registration = Join-Path $PSScriptRoot 'Register-QwenInstallation.ps1'
$count = 0
foreach ($parameter in @('PythonPath', 'ModelPath', 'AppDataPath')) {
    foreach ($networkPath in @('//example.invalid/share/file', '\\example.invalid\share\file')) {
        $arguments = @{ PythonPath = 'C:\missing-python.exe'; ModelPath = 'C:\missing-model'; AppDataPath = 'C:\missing-appdata'; ValidateOnly = $true }
        $arguments[$parameter] = $networkPath
        $rejected = $false
        try { & $registration @arguments }
        catch {
            if ($_.Exception.Message -notlike '*absolute local paths*') { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw "Network $parameter was accepted." }
        $count++
    }
}
Write-Output "Passed $count registration path rejection checks before any filesystem or runtime access."
