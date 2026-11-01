# 一键推送 InvoiceMerge 到 GitHub
# 用法：右键此文件 → 使用 PowerShell 运行
#      或命令行： powershell -ExecutionPolicy Bypass -File push.ps1 -User 你的GitHub用户名

param(
    [string]$User = "",
    [string]$Repo = "InvoiceMerge"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $Root

Write-Host ""
Write-Host "  InvoiceMerge -> GitHub" -ForegroundColor Cyan
Write-Host "  ------------------------------------------"

if (-not (Test-Path (Join-Path $Root ".git"))) {
    Write-Host "  [错误] 当前目录不是 git 仓库: $Root" -ForegroundColor Red
    Read-Host "按回车退出"; exit 1
}

if ([string]::IsNullOrWhiteSpace($User)) {
    Write-Host "  请输入你的 GitHub 用户名（例如 Conley2322）：" -ForegroundColor Yellow
    $User = (Read-Host "  用户名").Trim()
}
if ([string]::IsNullOrWhiteSpace($User)) {
    Write-Host "  [错误] 用户名为空" -ForegroundColor Red
    Read-Host "按回车退出"; exit 1
}

$url = "https://github.com/$User/$Repo.git"
Write-Host "  目标仓库: $url" -ForegroundColor Gray
Write-Host ""

# 确认远端仓库是否存在（HEAD 请求，不下载正文）
$exists = $true
try {
    $resp = Invoke-WebRequest -Uri "https://github.com/$User/$Repo" -Method Head -UseBasicParsing -TimeoutSec 20
} catch {
    if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { $exists = $false }
    else {
        Write-Host "  [提示] 无法确认仓库是否存在（网络问题），仍继续尝试推送" -ForegroundColor Yellow
    }
}
if (-not $exists) {
    Write-Host "  [错误] 仓库不存在: https://github.com/$User/$Repo" -ForegroundColor Red
    Write-Host ""
    Write-Host "  请先建一个空仓库（30 秒）：" -ForegroundColor Yellow
    Write-Host "    1. 打开 https://github.com/new"
    Write-Host "    2. Repository name 填: $Repo"
    Write-Host "    3. 选 Public，下面的 Add README / .gitignore / license 全部不要勾"
    Write-Host "    4. 点 Create repository，然后重新运行本脚本"
    Write-Host ""
    Start-Process "https://github.com/new"
    Read-Host "按回车退出"; exit 1
}

# 设置远端
$remotes = (git remote) -split "`n" | Where-Object { $_ -eq "origin" }
if ($remotes) {
    git remote set-url origin $url
    Write-Host "  已更新 origin 地址" -ForegroundColor Gray
} else {
    git remote add origin $url
    Write-Host "  已添加 origin" -ForegroundColor Gray
}

Write-Host ""
Write-Host "  正在推送（首次会弹出 GitHub 登录窗口，浏览器里点授权即可）..." -ForegroundColor Yellow
Write-Host ""

git push -u origin main
$code = $LASTEXITCODE

Write-Host ""
if ($code -eq 0) {
    Write-Host "  [成功] 已推送到 https://github.com/$User/$Repo" -ForegroundColor Green
    Write-Host "  仓库首页: https://github.com/$User/$Repo"
    Start-Process "https://github.com/$User/$Repo"
} else {
    Write-Host "  [失败] 退出码 $code，常见原因：" -ForegroundColor Red
    Write-Host "    · 登录窗口被关闭 → 重新运行，选择浏览器授权"
    Write-Host "    · 仓库已存在同名文件 → 确认建仓库时没有勾选 Add README"
    Write-Host "    · 密码方式已停用 → 需用浏览器登录，或使用 Personal Access Token"
}
Write-Host ""
Read-Host "按回车退出"
