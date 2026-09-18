param([string]$TiaPortalDir='', [string]$Configuration='Release', [string]$TiaVersion='')
if ($TiaPortalDir) { $env:TIA_PORTAL_DIR=$TiaPortalDir }
& (Join-Path $PSScriptRoot '构建V4.4.4.ps1') -TiaVersion $TiaVersion
exit $LASTEXITCODE
