# ============================================================
#  一键打包：发布 + 生成安装包
#
#  用法（在项目根目录）：
#      powershell -ExecutionPolicy Bypass -File .\build-setup.ps1
#
#  产物：
#      dist\Windowsyikefu.exe      发布出来的程序（自包含单文件，对方不用装 .NET）
#      installer\kefu_setup.exe    给别人安装用的安装包
#
#  发新版时只要改 src\Windowsyikefu.App\Windowsyikefu.App.csproj 里的 <Version>，
#  本脚本会自动把版本号同步到安装包上。
# ============================================================

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$proj = Join-Path $root 'src\Windowsyikefu.App\Windowsyikefu.App.csproj'
$iss = Join-Path $root 'installer\Windowsyikefu.iss'
$dist = Join-Path $root 'dist'

# ---------- 1. 找 Inno Setup 的编译器 ----------
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Host "找不到 Inno Setup（ISCC.exe）。" -ForegroundColor Yellow
    Write-Host "先装一下：winget install --id JRSoftware.InnoSetup -e" -ForegroundColor Yellow
    exit 1
}

# ---------- 2. 从 csproj 读版本号 ----------
$m = Select-String -Path $proj -Pattern '<Version>([^<]+)</Version>' -Encoding UTF8 | Select-Object -First 1
$version = if ($m) { $m.Matches[0].Groups[1].Value } else { '1.0.0' }
Write-Host "版本号：$version" -ForegroundColor Cyan

# ---------- 3. 发布 ----------
Write-Host "`n[1/2] 正在发布（Release，自包含单文件）…" -ForegroundColor Cyan

# 正在运行的旧版本会占用 exe，先请它退掉，否则发布 / 打包会失败
$running = Get-Process Windowsyikefu -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "      检测到正在运行的 易客服，先关闭它…" -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 1200
}

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

dotnet publish $proj -c Release -o $dist --nologo -v q
if ($LASTEXITCODE -ne 0) { throw '发布失败' }

$exe = Join-Path $dist 'Windowsyikefu.exe'
$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "      完成：dist\Windowsyikefu.exe（$sizeMb MB）" -ForegroundColor Green

# ---------- 4. 生成安装包 ----------
Write-Host "`n[2/2] 正在生成安装包…" -ForegroundColor Cyan
& $iscc "/DAppVersion=$version" $iss | Select-Object -Last 3

$setup = Join-Path $root 'installer\kefu_setup.exe'
if (-not (Test-Path $setup)) { throw '安装包生成失败' }

$setupMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host "`n全部完成！" -ForegroundColor Green
Write-Host "  安装包：installer\kefu_setup.exe（$setupMb MB）" -ForegroundColor Green
Write-Host "  把这个文件发给别人双击安装即可；也可以上传到你的服务器，" -ForegroundColor Gray
Write-Host "  再把下载地址填进后台「客服APP更新」，客户端就能自动提示更新。" -ForegroundColor Gray
