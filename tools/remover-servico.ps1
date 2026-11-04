#Requires -RunAsAdministrator
# Remove o serviço e a regra do firewall. O histórico e a senha ficam, a não ser que se peça -ApagarDados.
param(
    [switch]$ApagarDados,
    [string]$Destino = "$env:ProgramFiles\ControlCenter\Api"
)
$ErrorActionPreference = 'Stop'
$servico = 'ControlCenterApi'

if (Get-Service $servico -ErrorAction SilentlyContinue) {
    Stop-Service $servico -Force
    sc.exe delete $servico | Out-Null
}
Get-NetFirewallRule -DisplayName 'ControlCenter API' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if (Test-Path $Destino) { Remove-Item $Destino -Recurse -Force }
if ($ApagarDados) {
    $dados = Join-Path $env:ProgramData 'ControlCenter'
    if (Test-Path $dados) { Remove-Item $dados -Recurse -Force }
}
Write-Host 'Serviço removido.'
