# control-center

Central de controle do Windows que mora na bandeja, ao lado do relógio. Feita em C# com WinUI 3.

- **Lâmpada Tuya** (G-Light) pela rede local, sem nuvem: ligar/desligar, brilho, temperatura, cor e timer. Se o
  Wi-Fi dela dormir, acorda pelo **Bluetooth**.
- **Brilho dos monitores**: DDC/CI para monitores externos e WMI para a tela do notebook.
- **Tema claro/escuro** e **Luz Noturna**.
- **Cenas** (Trabalho, Noite, Filme...) que ajustam tudo de uma vez, e uma **agenda** por horário.
- **Projetar** (o mesmo do Win+P) e **girar um monitor** por atalho.
- **DualSense** por USB ou Bluetooth: cor e intensidade da luz, e emulação de **Xbox 360** ou **PS4** (ViGEmBus).

## Começo rápido

1. **Instale o [.NET 10 SDK](https://dotnet.microsoft.com/download).** Pelo winget:
   ```powershell
   winget install Microsoft.DotNet.SDK.10
   ```
2. **Compile:**
   ```powershell
   cd src\WinControl
   dotnet build -c Release
   ```
3. **Rode** `src\WinControl\bin\Release\net10.0-windows10.0.26100.0\win-x64\WinControl.exe` com dois cliques ou num
   terminal normal. Veja o [aviso sobre isolamento](docs/problemas.md#nada-do-que-eu-mudo-tem-efeito) antes de abrir
   o app de dentro de outro programa.
4. **Configure a lâmpada:** siga o [guia da lâmpada](docs/lampada.md). Para buscar a chave, basta rodar
   `tools/pegar_chave.py` e escanear um QR code no Smart Life.

Sem a lâmpada configurada, o resto (telas, tema, cenas, projetar) funciona normalmente. O painel avisa
"Não configurada" e diz o que falta.

## Usando

| Onde | O quê |
|---|---|
| Clique no ícone da bandeja | Abre o painel. Clicar fora ou apertar Esc fecha. |
| Botão direito no ícone | Menu completo: cenas, presets da lâmpada, timer, projetar, tema, agenda, configurações. |
| Botão direito numa cena (no painel) | Escolher o tema da cena, ligar ou não a Luz Noturna, ou **salvar o estado atual na cena**. |
| `Ctrl+Alt+R` | Gira o monitor configurado. |
| `Win+Ctrl+Shift+D` | Alterna o tema claro/escuro. |

### Como as cenas funcionam

- Uma cena aplica **tema**, **Luz Noturna**, **brilho das telas** e **lâmpada**. Cada parte é opcional.
- A cena **nunca acende uma lâmpada apagada**. Com a lâmpada ligada, ela ajusta na hora. Com a lâmpada apagada, o
  ajuste fica combinado para o **próximo "ligar"** e vale só para ele.
- Ligar a lâmpada sem cena pendente **mantém o último estado**: brilho, temperatura e cor de quando ela apagou.
- A **agenda** só aplica telas e tema. Nunca liga nem desliga a lâmpada.
- Quando uma cena muda a Luz Noturna, o "tema segue a Luz Noturna" não desfaz o tema escolhido pela cena.

Detalhes e formato dos arquivos: [docs/configuracao.md](docs/configuracao.md).

## Documentação

- [docs/lampada.md](docs/lampada.md): configurar a lâmpada (chave, IP, Bluetooth).
- [docs/configuracao.md](docs/configuracao.md): referência dos arquivos `lamp_config.json` e `conforto.json`.
- [docs/dualsense.md](docs/dualsense.md): controle DualSense, luz, emulação e drivers.
- [docs/problemas.md](docs/problemas.md): solução de problemas.
- [docs/arquitetura.md](docs/arquitetura.md): como o código está organizado e como testar.

## Onde ficam os dados

Tudo fica em `%APPDATA%\win-control`, **fora do repositório**, porque ali está a chave da lâmpada:

| Arquivo | Conteúdo |
|---|---|
| `lamp_config.json` | id, IP, chave e MAC Bluetooth da lâmpada |
| `conforto.json` | cenas, agenda e opções |
| `win-control.log` | log. A primeira linha de cada início diz **qual pasta de config está em uso**. |

O menu da bandeja tem **Abrir pasta de configuração** e **Abrir log**.
