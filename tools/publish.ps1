# 发布脚本：win-x64 框架依赖单文件 + 数据文件(models/GLOSSARY/scene-refs.json/layout-presets.json)随附，产物压成 zip。
# scene-refs.json 是场景识别的出厂参考，由 tools/SceneRefGen 生成——csproj 里已配 CopyToOutputDirectory，随 publish 自动带上。
# 用法: pwsh -File tools/publish.ps1 [-Version 0.4.0]
param([string]$Version = "0.4.0")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src/ScrTranslator.App/ScrTranslator.App.csproj'
$out  = Join-Path $root "publish/ScrTranslator-$Version-win-x64"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish $proj -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:Version=$Version -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

$zip = "$out.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip

Write-Host "`n产物: $out"
Write-Host "压缩包: $zip"
Get-ChildItem $out | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,2)}}
