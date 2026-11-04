# Arquitetura

App WinUI 3 sem pacote (`WindowsPackageType=None`, self-contained), com uma instância só (mutex
`WinControlSingleInstance`). Abrir o `.exe` de novo só mostra o painel da instância que já está rodando.

```
src/WinControl/
  App.xaml.cs          início, instância única, argumentos de linha de comando
  PanelWindow.xaml.cs  painel flutuante: posição, foco, fechar ao clicar fora, soltar depois de 3 min escondido
  SettingsWindow.xaml.cs  janela de configurações: normal, porque o painel some ao perder o foco
  Tray.cs              ícone e menu da bandeja (WinForms NotifyIcon)
  Pages/               MainPage (blocos, cenas, brilho), LampPage, ProjectPage, DualSensePage, PerifericosPage;
                       ConfigGeralPage, ConfigLampadaPage, ConfigCenasPage, ConfigAparelhosPage e ConfigApiPage (abas das configurações)
  SelfTest.cs          teste sem interface contra o hardware real
  Services/
    Common.cs          AppPaths (%APPDATA%\win-control), Log, Bus (aviso de "algo mudou")
    Core.cs            controles da lâmpada, poller, vigia da Luz Noturna, autostart
    Lamp.cs            Lampada (estado e comandos de uma, fallback Bluetooth) e Lamp (quais moram nesta rede, comandos de todas as daqui)
    Rede.cs            em que lugar o PC está, pelo MAC do roteador
    Tuya.cs            protocolo local Tuya 3.3 (TCP 6668, AES-ECB) e 3.5 (AES-GCM, chave por conexão); avisos UDP (6666/6667)
    Ble.cs             protocolo Tuya BLE (GATT 2b10/2b11, AES-CBC, chave de sessão) para acordar a lâmpada
    Conforto.cs        cenas, agenda, conforto.json
    Theme.cs           tema claro/escuro e Luz Noturna (registro + broadcast, aviso de mudança na chave)
    Screens.cs         brilho dos monitores (DDC/CI e WMI)
    DualSense.cs       DualSense por HID (USB 0x01/0x02, BT 0x31 com CRC32), luz, ViGEm (X360/DS4), HidHide
    Jogos.cs           modo automático: vigia a janela em primeiro plano (SetWinEventHook), categorias da Steam (57/58 = DualSense), jogos.json
    Perifericos.cs     luz do mouse Razer (Chroma) e do teclado Redragon (EVision), seguindo ou não o DualSense
    ApiBridge.cs       servidor do named pipe que atende a ControlCenter.Api; só abre com a API ativa
    ApiServico.cs      estado e controle do serviço da API (instalar, ligar, desligar), sempre pelo UAC
    DisplayProjection.cs, Rotation.cs, Hotkeys.cs
src/ControlCenter.Contracts/   mensagens e protocolo do pipe, usados pelo app e pela API
src/ControlCenter.Api/         API REST (ASP.NET Core): veja api.md
tests/ControlCenter.Api.Tests/ xUnit + WebApplicationFactory
tools/
  pegar_chave.py       busca id + local_key pelo login QR do Smart Life
  instalar-servico.ps1, remover-servico.ps1   a API como serviço do Windows
```

## Fluxo de dados

- Quem muda algo chama `Bus.Notify()`. O painel e a bandeja assinam e se redesenham.
- O **poller** lê as lâmpadas desta rede a cada 3 s com o painel aberto e a cada 30 s com ele fechado, em paralelo.
- `Lamp.*` significa "todas as lâmpadas daqui". Com uma só, cai direto nela, sem nenhuma diferença de comportamento.
- Os sliders e a roda de cores não falam direto com a lâmpada. Cada `Lampada` tem a própria **fila de envio**
  (`Lampada.Enviar`): uma lenta não segura as outras, só o último valor é mandado, e ele é repetido se falhar. Enquanto há
  envio em andamento, e por 1,5 s depois, o poller não sobrescreve o estado com o que leu.
- O painel só existe enquanto é usado: `App.OpenPanel` cria a janela e ela é fechada 3 min depois de escondida.
- O resto reage a eventos do Windows em vez de consultar em laço: Luz Noturna (`RegNotifyChangeKeyValue`), jogo em
  primeiro plano (`SetWinEventHook`), aparelhos HID entrando ou saindo. Detalhes e medições: [desempenho.md](desempenho.md).
- Tudo que fala com a rede ou com o registro roda fora da thread de interface. `Ui.Run` volta para ela.

## DPs da lâmpada

| DP | Significado |
|---|---|
| 20 | liga/desliga |
| 21 | modo (`white` / `colour`) |
| 22 | brilho branco (10-1000) |
| 23 | temperatura (0-1000) |
| 24 | cor HSV em hex (`hhhhssssvvvv`) |
| 26 | timer (segundos até inverter o estado) |

## Linha de comando

| Argumento | Efeito |
|---|---|
| `--background` | Inicia sem abrir o painel (é o que o "Iniciar com o Windows" usa). |
| `--show` | Abre o painel ao iniciar. |
| `--page=lamp` / `--page=project` / `--page=dualsense` / `--page=perifericos` | Abre direto numa subpágina (para desenvolvimento). |
| `--page=config` / `--page=config-lampada` / `--page=config-cenas` / `--page=config-aparelhos` / `--page=config-api` | Abre direto numa aba das configurações (para desenvolvimento). |
| `--selftest` | Lê lâmpada, telas, tema, projeção e agenda e grava `selftest.txt` na pasta de config. |
| `--selftest-write` | Também liga, muda e restaura a lâmpada. |
| `--selftest-scenes` | Testa as regras de cena da lâmpada. |
| `--selftest-rajada` | Reenvia às lâmpadas acesas o valor que elas já têm, em rajada, e mede quantos envios foram feitos e quantos falharam. Não muda nada visível. |
| `--selftest-theme` | Troca o tema e volta. |

## Compilar e testar

```powershell
cd src\WinControl
dotnet build -c Release
.\bin\Release\net10.0-windows10.0.26100.0\win-x64\WinControl.exe --selftest
type $env:APPDATA\win-control\selftest.txt
```

Antes de compilar, feche o app (bandeja → Sair). Com ele aberto, o `.exe` fica travado.
