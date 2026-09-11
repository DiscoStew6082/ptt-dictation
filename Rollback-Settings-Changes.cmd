@echo off
pwsh.exe -NoProfile -File "%~dp0scripts\Restore-LocalAppSnapshot.ps1" -SnapshotPath "C:\Users\stewa\projects\par-win-ptt\publish\rollback-before-settings-recovery"
if errorlevel 1 (
  echo Rollback did not complete. Keep this window open to read the error.
  pause
)
