#Requires -Version 7.0
[CmdletBinding()]
param()
& (Join-Path $PSScriptRoot 'Test-BrowserColdStart.ps1') -Browser Firefox
