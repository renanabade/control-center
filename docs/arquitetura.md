# Arquitetura

App WinUI 3 sem pacote (`WindowsPackageType=None`, self-contained), com uma instância só (mutex
`WinControlSingleInstance`). Abrir o `.exe` de novo só mostra o painel da instância que já está rodando.

```
src/WinControl/
  App.xaml.cs          início, instância única, argumentos de linha de comando
  PanelWindow.xaml.cs  painel flutuante: posição, foco, fechar ao clicar fora
  Tray.cs              ícone e menu da bandeja (WinForms NotifyIcon)
  Pages/               MainPage (blocos, cenas, brilho), LampPage, ProjectPage
  SelfTest.cs          teste sem interface contra o hardware real
  Services/
    Common.cs          AppPaths (%APPDATA%\win-control), Log, Bus (aviso de "algo mudou")
    Core.cs            threads de fundo: envio coalescido p/ lâmpada, poller, vigia da Luz Noturna, autostart
    Lamp.cs            estado e comandos da lâmpada (DPs Tuya), fallback Bluetooth, redescoberta de IP
    Tuya.cs            protocolo local Tuya 3.3 (TCP 6668, AES-ECB) e descoberta UDP (6666/6667)
    Ble.cs             protocolo Tuya BLE (GATT 2b10/2b11, AES-CBC, chave de sessão) para acordar a lâmpada
    Conforto.cs        cenas, agenda, conforto.json
    Theme.cs           tema claro/escuro e Luz Noturna (registro + broadcast)
    Screens.cs         brilho dos monitores (DDC/CI e WMI)
    DualSense.cs       DualSense por HID (USB 0x01/0x02, BT 0x31 com CRC32), luz, ViGEm (X360/DS4), HidHide
    Jogos.cs           modo automático: vigia processos (WMI), categorias da Steam (57/58 = DualSense), jogos.json
    Perifericos.cs     luz do mouse Razer (Chroma) e do teclado Redragon (EVision), seguindo ou não o DualSense
    DisplayProjection.cs, Rotation.cs, Hotkeys.cs
tools/
  pegar_chave.py       busca id + local_key pelo login QR do Smart Life
```

## Fluxo de dados

- Quem muda algo chama `Bus.Notify()`. O painel e a bandeja assinam e se redesenham.
- O **poller** lê a lâmpada a cada 3 s com o painel aberto e a cada 30 s com ele fechado.
- Os sliders não falam direto com a lâmpada. Eles marcam o pedido e o **envio coalescido** (`Core.SendWorker`)
  manda só o último valor.
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
| `--page=lamp` / `--page=project` / `--page=dualsense` | Abre direto numa subpágina (para desenvolvimento). |
| `--selftest` | Lê lâmpada, telas, tema, projeção e agenda e grava `selftest.txt` na pasta de config. |
| `--selftest-write` | Também liga, muda e restaura a lâmpada. |
| `--selftest-scenes` | Testa as regras de cena da lâmpada. |
| `--selftest-theme` | Troca o tema e volta. |

## Compilar e testar

```powershell
cd src\WinControl
dotnet build -c Release
.\bin\Release\net10.0-windows10.0.26100.0\win-x64\WinControl.exe --selftest
type $env:APPDATA\win-control\selftest.txt
```

Antes de compilar, feche o app (bandeja → Sair). Com ele aberto, o `.exe` fica travado.
