param(
    [string]$ProjectPath = '',
    [string]$TiaVersion = '',
    [string]$TiaPortalDir = '',
    [switch]$Publish
)
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
& (Join-Path $root '..\BUILD-V4.4.4.ps1') @PSBoundParameters
