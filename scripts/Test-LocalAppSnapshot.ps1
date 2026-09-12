#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('ptt-snapshot-tests-' + [Guid]::NewGuid().ToString('N'))
$restore = Join-Path $PSScriptRoot 'Restore-LocalAppSnapshot.ps1'
try {
    New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'app') -Force | Out-Null
    foreach ($name in @('PttDictation.exe','PttDictation.dll','PttDictation.Core.dll','System.Private.CoreLib.dll')) {
        [IO.File]::WriteAllText((Join-Path $fixtureRoot "app\$name"), $name)
    }
    [IO.File]::WriteAllText((Join-Path $fixtureRoot 'settings.json'), '{"selectedModelId":"previous"}')
    $hashes = @{}
    foreach ($file in Get-ChildItem -LiteralPath $fixtureRoot -Recurse -File) {
        $hashes[[IO.Path]::GetRelativePath($fixtureRoot, $file.FullName)] = (Get-FileHash -LiteralPath $file.FullName).Hash
    }
    $manifest = @{ Schema = 1; SourceCommit = 'fixture'; ExecutablePath = 'C:\Users\stewart\projects\par-win-ptt\publish\ptt-dictation-win-x64\PttDictation.exe'; Files = $hashes }
    $manifestPath = Join-Path $fixtureRoot 'snapshot.json'
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath
    $result = & $restore -SnapshotPath $fixtureRoot -VerifyOnly
    if ($result.FilesVerified -ne 5) { throw 'Valid snapshot was not verified.' }
    $cases = 1
    foreach ($relative in @('app\PttDictation.exe','settings.json')) {
        $path = Join-Path $fixtureRoot $relative
        $original = [IO.File]::ReadAllBytes($path)
        try {
            [IO.File]::WriteAllText($path, 'tampered')
            $rejected = $false
            try { & $restore -SnapshotPath $fixtureRoot -VerifyOnly | Out-Null }
            catch { if ($_.Exception.Message -notlike '*hash mismatch*') { throw }; $rejected = $true }
            if (-not $rejected) { throw 'Tampered snapshot accepted.' }
            $cases++
        } finally { [IO.File]::WriteAllBytes($path, $original) }
    }
    $extra = Join-Path $fixtureRoot 'unexpected.exe'
    [IO.File]::WriteAllText($extra, 'extra')
    $rejected = $false
    try { & $restore -SnapshotPath $fixtureRoot -VerifyOnly | Out-Null }
    catch { if ($_.Exception.Message -notlike '*file count*') { throw }; $rejected = $true }
    if (-not $rejected) { throw 'Extra snapshot file accepted.' }
    $cases++
    Remove-Item -LiteralPath $extra
    $manifest.ExecutablePath = 'C:\somewhere-else\PttDictation.exe'
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath
    $rejected = $false
    try { & $restore -SnapshotPath $fixtureRoot -VerifyOnly | Out-Null }
    catch { if ($_.Exception.Message -notlike '*not for this permanent*') { throw }; $rejected = $true }
    if (-not $rejected) { throw 'Wrong destination accepted.' }
    $cases++
    Write-Output "Passed $cases isolated snapshot verification cases. No app or settings were changed."
} finally {
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notlike 'ptt-snapshot-tests-*') { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
