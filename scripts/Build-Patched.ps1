param([string]$TiaPortalDir='', [string]$TiaVersion='')
if ($TiaPortalDir) { $env:TIA_PORTAL_DIR=$TiaPortalDir }
& (Join-Path $PSScriptRoot '构建V4.3.ps1') -TiaVersion $TiaVersion
exit $LASTEXITCODE
