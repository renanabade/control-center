# DualSense

O control-center fala direto com o DualSense (e o DualSense Edge), por USB ou Bluetooth. Abra pelo bloco
**Controle** no painel ou pelo menu da bandeja → **Controle**.

- **Luz:** cor (roda) e intensidade (0 a 100%). O DualSense não tem brilho próprio na barra: a intensidade escala o
  RGB. 0% apaga.
- **LED de jogador:** liga ou desliga o LED do meio, abaixo do touchpad.
- **Emular como:**
  - **DualSense:** nada é emulado. O jogo vê o controle original (bom para jogos com suporte nativo e gatilhos adaptativos).
  - **Xbox 360:** cria um controle Xbox virtual. Touchpad clicado vira **Back**. A vibração do jogo chega no DualSense.
  - **PS4:** cria um DualShock 4 virtual, com ícones PlayStation nos jogos que suportam.

## Atalho no controle

Segure **Mute** e aperte o direcional, sem sair do jogo:

| Combinação | Modo |
|---|---|
| Mute + ↑ | DualSense (nativo) |
| Mute + ← | Xbox 360 |
| Mute + → | PS4 |
| Mute + ↓ | liga/desliga o automático |

A confirmação é uma vibração curta. Em toda troca de modo a barra apaga por 1,5 s e acende respirando na cor do
novo modo (com **Cor por modo** ligado, cada modo tem a sua: `cor`, `cor_xbox360`, `cor_ps4`). Com o automático
ligado e um jogo aberto, a troca fica gravada para aquele jogo. O LED do Mute fica sempre apagado. Emulando, o direcional não chega ao jogo enquanto o Mute está segurado.

## Automático por jogo

Com **Automático por jogo** ligado, o control-center escolhe o modo sozinho quando um jogo abre e volta ao nativo
quando ele fecha:

1. **Percebe o jogo** (WMI, até ~1 s depois de abrir). Conta como jogo um `.exe` dentro de `steamapps\common`
   (qualquer disco), das pastas da Epic/Ubisoft/GOG/EA, das suas `pastas_extras`, da lista de jogos do Windows
   (Game Bar), ou que carregue uma biblioteca de controle (XInput/DirectInput/GameInput).
2. **Consulta a loja da Steam** uma vez: jogo com a categoria *DualSense Controller Support* → **nativo**; sem ela →
   **PS4**. Jogos fora da Steam são buscados pelo nome da pasta. Sem resposta, usa o `padrao`.
3. **Aprende:** trocar o modo com o jogo aberto grava a escolha para aquele jogo. O botão ↻ na página esquece o jogo
   e consulta de novo.

A primeira vez que um jogo é reconhecido só pela biblioteca de controle, ele pode já ter achado o controle no modo
antigo: reinicie o jogo uma vez.

**Steam Input:** desligue para controles PlayStation (Steam → Configurações → Controle → *Suporte a controles
PlayStation*: desativado), ou por jogo (Propriedades → Controle). Com ele ligado, a Steam captura o DualSense e entrega
um Xbox ao jogo, desfazendo a escolha do control-center.

`%APPDATA%\win-control\jogos.json`:

```json
{
  "pastas_extras": ["C:\\games"],
  "padrao": "ps4",
  "jogos": {
    "d:\\steamlibrary\\steamapps\\common\\cyberpunk 2077": { "nome": "Cyberpunk 2077", "appid": 1091500, "modo": "dual_sense", "origem": "steam" }
  }
}
```

## Drivers para a emulação

Só o modo DualSense funciona sem nada instalado. Para Xbox 360 e PS4:

1. **ViGEmBus** cria o controle virtual. O projeto foi arquivado, mas o driver ainda funciona no Windows 11:
   ```powershell
   winget install ViGEm.ViGEmBus
   ```
2. **HidHide** (recomendado) esconde o DualSense real dos jogos enquanto emula, senão eles veem dois controles:
   ```powershell
   winget install Nefarius.HidHide
   ```
   O control-center se registra sozinho na lista de apps permitidos do HidHide e esconde/mostra o controle ao trocar
   de modo. Ao sair pelo menu da bandeja, o controle volta a aparecer para os jogos.

Reinicie o PC depois de instalar os dois.

## Configuração

`%APPDATA%\win-control\dualsense.json`:

```json
{
  "cor": "#0050FF",
  "intensidade": 60,
  "modo": "xbox360",
  "leds_jogador": true
}
```

`modo`: `dual_sense`, `xbox360` ou `ps4`.

## Problemas

- **"ViGEmBus não instalado"** no status: instale o ViGEmBus e reinicie.
- **Jogo com controles duplicados:** instale o HidHide. Se o app foi encerrado à força, abra o *HidHide Configuration
  Client* e desmarque o DualSense.
- **Luz não muda por Bluetooth:** desligue e ligue o controle (PS por 10 s) para ele reconectar.

## Periféricos (mouse e teclado)

Bloco **Periféricos** no painel. Funciona sem Synapse e sem o programa da Redragon (feche-os, senão eles brigam pela
cor):

- **Razer DeathAdder V2** (`1532:0084`): logo e roda, protocolo Chroma (relatório de recurso de 90 bytes).
- **Redragon Kumara V2** (`320F:5000`, chip EVision): modo estático, relatório 0x04 na interface 1.

Cada um pode **seguir a cor do controle** (inclui cor por modo, cor do jogo no PS4 e a transição) ou ter cor própria,
com brilho de 0 a 100%. Config em `%APPDATA%\win-control\perifericos.json`. Protocolos tirados do OpenRGB.
