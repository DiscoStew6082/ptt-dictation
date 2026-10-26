#Requires -Version 7.0
<# Compatibility entry point for staged local updates and snapshot restoration.
   Install-PttDictation.ps1 owns all installation, verification, and recovery logic. #>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Install')][string]$StagedPath,
    [Parameter(ParameterSetName = 'Install')][ValidateNotNullOrEmpty()][string]$SettingsSource,
    [Parameter(Mandatory, ParameterSetName = 'Verify')][switch]$VerifyOnly,
    [ValidateNotNullOrEmpty()]
    [string]$InstallDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\PttDictation')
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Install-PttDictation.ps1') @PSBoundParameters -NoShortcut
