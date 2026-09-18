param(
    [string]$ProjectPath = "",
    [string]$TiaVersion = "",
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$src = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$csproj = Join-Path $src 'TiaMcpServer.csproj'

function Normalize-Version([string]$v) {
    if ($v -match 'V?\s*(\d{2})') { return 'V' + $Matches[1] }
    return ''
}
function Add-Candidate([System.Collections.Generic.List[object]]$list, [string]$path, [string]$version, [string]$source) {
    if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path $path)) { return }
    $v = Normalize-Version $version
    if (-not $v -and $path -match 'Portal\s+V(\d{2})') { $v = 'V' + $Matches[1] }
    if (-not $v) { return }
    $api = Join-Path $path "PublicAPI\$v\Siemens.Engineering.dll"
    $list.Add([pscustomobject]@{ Version=$v; Number=[int]($v.TrimStart('V')); Path=$path; HasOpenness=(Test-Path $api); Source=$source })
}

if (-not $TiaVersion -and $ProjectPath) {
    $ext = [IO.Path]::GetExtension($ProjectPath)
    if ($ext -match '^\.ap(\d+)$' -or $ext -match '^\.zap(\d+)$') { $TiaVersion = 'V' + $Matches[1] }
}
$TiaVersion = Normalize-Version $TiaVersion
$candidates = [System.Collections.Generic.List[object]]::new()

if ($env:TIA_PORTAL_DIR) { Add-Candidate $candidates $env:TIA_PORTAL_DIR $env:TIA_PORTAL_VERSION '环境变量' }
foreach ($view in @('Registry64','Registry32')) {
    foreach ($hive in @('LocalMachine','CurrentUser')) {
        try {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::$hive,[Microsoft.Win32.RegistryView]::$view)
            $key = $base.OpenSubKey('SOFTWARE\Siemens\Automation\Portal')
            if ($key) {
                foreach ($sub in $key.GetSubKeyNames()) {
                    $sk = $key.OpenSubKey($sub); $p = $sk.GetValue('Path'); $sk.Dispose()
                    if ($p) { Add-Candidate $candidates $p $sub '注册表' }
                }
                $key.Dispose()
            }
            $base.Dispose()
        } catch {}
    }
}
foreach ($drive in Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Root }) {

    Write-Host "[扫描] $($drive.Root)" -ForegroundColor DarkGray

    Get-ChildItem $drive.Root `
        -Directory `
        -Filter "Portal V*" `
        -Recurse `
        -ErrorAction SilentlyContinue |
    ForEach-Object {

        Add-Candidate `
            $candidates `
            $_.FullName `
            $_.Name `
            "递归磁盘扫描"

    }
}
$candidates = $candidates | Sort-Object Path -Unique
if (-not $candidates) { throw '未发现任何 TIA Portal。请先安装 TIA Portal + Openness，或设置 TIA_PORTAL_DIR。' }
$selected = if ($TiaVersion) { $candidates | Where-Object Version -eq $TiaVersion | Sort-Object HasOpenness -Descending | Select-Object -First 1 } else { $candidates | Sort-Object @{E='HasOpenness';Descending=$true}, @{E='Number';Descending=$true} | Select-Object -First 1 }
if (-not $selected) { throw "未找到匹配版本 $TiaVersion。已发现：$($candidates.Version -join '、')" }
if (-not $selected.HasOpenness) { throw "已找到 $($selected.Version)，但未发现 Openness PublicAPI：$($selected.Path)。请在 TIA 安装程序中勾选 TIA Portal Openness。" }

Write-Host "[V4.2] 选择版本: $($selected.Version)" -ForegroundColor Green
Write-Host "[V4.2] 安装目录: $($selected.Path)" -ForegroundColor Green
$args = @($csproj, '-c', 'Release', "-p:TiaPortalDir=$($selected.Path)", "-p:TiaVersion=$($selected.Version)")
if ($Publish) { & dotnet publish @args } else { & dotnet build @args }
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
