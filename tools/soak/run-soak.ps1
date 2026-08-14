# M7 纵切稳定第一批：一键运行 MingSim 长跑测试（tests/Ming.SoakTests）。
# 用法：powershell -ExecutionPolicy Bypass -File tools/soak/run-soak.ps1
# 说明：Release 构建（0 警告 0 错误）+ 依次运行 90 日 ×20 种子确定性重放与一年合成世界长跑；
#       输出每次推进 CPU 时间分布与内存量级摘要（doc 11 §10 只记录量级，不写死性能门槛）。
$ErrorActionPreference = "Stop"
$env:MSBUILDDISABLENODEREUSE = "1"

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Push-Location $root
try {
    dotnet build tests\Ming.SoakTests\Ming.SoakTests.csproj -c Release -m:1 -p:NuGetAudit=false --nologo
    if ($LASTEXITCODE -ne 0) { throw "构建失败，退出码 $LASTEXITCODE" }
    dotnet run --project tests\Ming.SoakTests\Ming.SoakTests.csproj -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "长跑测试失败，退出码 $LASTEXITCODE" }
}
finally {
    Pop-Location
}
