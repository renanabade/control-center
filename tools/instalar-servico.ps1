#Requires -RunAsAdministrator
<#
  Instala a ControlCenter.Api como serviço do Windows, na conta LocalService (a de menos privilégio que ainda usa a rede).
  Rode num PowerShell como administrador. Na primeira vez, gera e mostra a senha da API uma única vez.
#>
param(
    [int]$Porta = 5080,
    [string]$Usuario = 'admin',
    [string]$Destino = "$env:ProgramFiles\ControlCenter\Api"
)
$ErrorActionPreference = 'Stop'
$servico = 'ControlCenterApi'
$raiz = Split-Path $PSScriptRoot -Parent
$dados = Join-Path $env:ProgramData 'ControlCenter'

if (Get-Service $servico -ErrorAction SilentlyContinue) { Stop-Service $servico -Force }

Write-Host 'Publicando a API...'
dotnet publish "$raiz\src\ControlCenter.Api" -c Release -r win-x64 --self-contained -o $Destino --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish falhou' }
$exe = Join-Path $Destino 'ControlCenter.Api.exe'

# a pasta guarda o hash da senha e a chave do JWT: fecha antes de criar os arquivos, sem herdar o "Users pode ler"
New-Item -ItemType Directory -Force $dados | Out-Null
$sids = '*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F', '*S-1-5-19:(OI)(CI)M'   # SYSTEM, Administradores, LocalService
icacls $dados /inheritance:r /grant:r @sids | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'icacls falhou' }

if (-not (Test-Path (Join-Path $dados 'api.settings.json'))) {
    & $exe setup --pasta $dados --usuario $Usuario
    if ($LASTEXITCODE -ne 0) { throw 'setup falhou' }
}
else { Write-Host 'Configuração já existe: a senha continua a mesma.' }

# sem a origem registrada, o serviço (sem direito de criá-la) não consegue escrever no Visualizador de Eventos
if (-not [Diagnostics.EventLog]::SourceExists('ControlCenter.Api')) {
    [Diagnostics.EventLog]::CreateEventSource('ControlCenter.Api', 'Application')
}

if (-not (Get-Service $servico -ErrorAction SilentlyContinue)) {
    New-Service -Name $servico -BinaryPathName "`"$exe`"" -DisplayName 'ControlCenter API' -StartupType Automatic `
        -Description 'API para controlar o control-center por outros aparelhos. Precisa do app da bandeja aberto.' | Out-Null
}
$wmi = Get-CimInstance Win32_Service -Filter "Name='$servico'"
$r = Invoke-CimMethod -InputObject $wmi -MethodName Change -Arguments @{ StartName = 'NT AUTHORITY\LocalService'; StartPassword = '' }
if ($r.ReturnValue -ne 0) { throw "Não consegui definir a conta do serviço (código $($r.ReturnValue))" }
sc.exe failure $servico reset= 86400 actions= restart/5000/restart/30000// | Out-Null

Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$servico" -Name Environment -Type MultiString `
    -Value @("Urls=http://0.0.0.0:$Porta", 'ASPNETCORE_ENVIRONMENT=Production')

# só em redes marcadas como particulares: em Wi-Fi público a porta continua fechada
$regra = 'ControlCenter API'
Get-NetFirewallRule -DisplayName $regra -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $regra -Direction Inbound -Protocol TCP -LocalPort $Porta -Action Allow -Profile Private | Out-Null

Start-Service $servico
$pronto = $null
foreach ($i in 1..20) {
    # 127.0.0.1, não localhost: o serviço só escuta em IPv4, e tentar o ::1 antes gasta quase todo o tempo de espera
    try { $pronto = Invoke-RestMethod "http://127.0.0.1:$Porta/health/pronto" -TimeoutSec 5; break } catch { Start-Sleep 1 }
}
if (-not $pronto) { throw 'O serviço não respondeu. Veja o Visualizador de Eventos, em Aplicativo.' }

Write-Host "`nServiço rodando. Estado: $pronto" -ForegroundColor Green
if ($pronto -ne 'Healthy') { Write-Host 'Degraded: abra o app da bandeja, é ele quem fala com os aparelhos.' -ForegroundColor Yellow }
Get-NetIPAddress -AddressFamily IPv4 -PrefixOrigin Dhcp, Manual -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "Swagger: http://$($_.IPAddress):$Porta/swagger" }
