param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    # 本机自己解出的立绘目录（游戏素材，不随仓库分发）：编译时用 -p:FftAssets=<目录> 传入
    [Parameter(Mandatory = $true)]
    [string]$AssetsDirectory
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $AssetsDirectory 'portraits'
if (Test-Path -LiteralPath $source -PathType Container) {
    $destination = Join-Path $OutputDirectory 'assets\portraits'
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    foreach ($name in @('face', 'job', 'monster', 'portraits.json')) {
        $item = Join-Path $source $name
        if (Test-Path -LiteralPath $item) {
            Copy-Item -LiteralPath $item -Destination $destination -Recurse -Force
        }
    }
}

