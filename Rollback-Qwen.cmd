@echo off
pwsh.exe -NoProfile -File "%~dp0scripts\Restore-LocalAppSnapshot.ps1" -SnapshotPath "%~dp0publish\rollback-before-qwen"
if errorlevel 1 (
  echo Rollback did not complete. Keep this window open to read the error.
  pause
)
