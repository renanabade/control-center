# Solução de problemas

Comece pelo log: bandeja → **Abrir log** (`%APPDATA%\win-control\win-control.log`). Cada erro aparece uma vez,
com uma dica entre parênteses.

## Nada do que eu mudo tem efeito

Sintoma: você edita a config, a chave ou as cenas, mas o app age como se nada tivesse mudado. Ou o tema e a Luz
Noturna mudam "no app" mas não no Windows.

Causa: o app foi aberto **de dentro de outro app empacotado** (por exemplo, um terminal ou agente rodando dentro do
Claude Desktop, ou outro app da Microsoft Store). Nesse caso o Windows redireciona `%APPDATA%` e parte do registro
para uma cópia isolada, em `%LOCALAPPDATA%\Packages\<app>\LocalCache\Roaming\...`. O app lê e grava nessa cópia, não
na pasta real.

Como confirmar: a primeira linha de cada início no log diz `config em ...`. Se o log que você está lendo não tem a
linha do início mais recente, você está olhando o arquivo errado.

Solução: abra o `WinControl.exe` pelo Explorer, pelo menu Iniciar ou por um terminal normal. Para abrir pelo
Explorer a partir de um script:

```powershell
explorer.exe "C:\caminho\para\WinControl.exe"
```

Ferramentas como `tools/pegar_chave.py` também precisam rodar num terminal normal.

## Lâmpada

| O painel/log diz | Causa | O que fazer |
|---|---|---|
| `Não configurada (falta a local_key)` | Config sem chave | [Buscar a chave](lampada.md#buscar-a-chave-qr-code-sem-portal-da-tuya) |
| `Err 901/905 inacessivel` | IP errado, lâmpada sem energia ou **VPN bloqueando a rede local** | No Proton VPN e similares, ative "Allow LAN connections". |
| `Nenhuma nesta rede` | Nenhuma lâmpada foi vista nesta rede | Normal fora de casa. Se ela é daqui, marque **Pertence à rede em que o PC está agora** em Configurações → Lâmpada. |
| `Busca na rede: sem resposta de...` | Lâmpada apagada no interruptor, dormindo ou fora da rede | Normal com lâmpada apagada. Ela aparece sozinha quando voltar. |
| Lâmpada de outro lugar aparece aqui | Foi adicionada estando nesta rede | Desmarque **Pertence à rede em que o PC está agora** em Configurações → Lâmpada. |
| `Err 914` | Chave errada: a lâmpada foi readicionada no app | Rode `tools/pegar_chave.py` de novo e clique em **Recarregar do arquivo** em Configurações → Lâmpada. |
| `Dormindo (acorda pelo Bluetooth)` | Wi-Fi da lâmpada em economia | Normal. Clicar em ligar acorda a lâmpada pelo Bluetooth. |
| `BLE: lampada nao encontrada` | Bluetooth do PC desligado ou longe demais | Ligue o Bluetooth do PC e confira o `ble_mac`. |

## Telas

`sem resposta do DDC/CI`: ative **DDC/CI** no menu do próprio monitor. Alguns monitores só respondem pela entrada
DisplayPort/HDMI que está ativa.

## Girar monitor

| Mensagem | O que fazer |
|---|---|
| `Escolha qual monitor girar...` | Com mais de uma tela, escolha o monitor em **Configurações → Aparelhos**. |
| `O monitor escolhido nao esta ativo...` | Ligue esse monitor ou escolha outro. O app não gira outra tela no lugar dele. |

## Atalhos de teclado

- **Não disparam:** confira em **Configurações → Aparelhos** se o atalho não está desligado. Ele só funciona com o app aberto.
- **Disparam junto com outro programa:** o app não bloqueia a tecla, então o outro programa também a recebe. Escolha
  outra combinação.

## Levar a configuração para outro PC

| Mensagem | Causa |
|---|---|
| `Senha errada, ou o arquivo foi alterado.` | A senha é a criada ao exportar. Sem ela, não há como recuperar: exporte de novo. |
| `Este arquivo não é uma configuração do Control Center.` | Escolha o `.ccconfig` gerado por **Exportar**. |

## Periféricos

- **O bloco não aparece:** o aparelho não foi reconhecido. Veja como
  [adicionar outro modelo](dualsense.md#periféricos-mouse-e-teclado).
- **A cor volta sozinha:** feche o Synapse ou o programa da Redragon, que disputam a cor com o app.

## API

- **`503` nos comandos:** o app da bandeja está fechado, ou a API não está ativa nele.
- **Outro aparelho não alcança a API:** o firewall só abre a porta em redes particulares. A aba **API remota** avisa
  quando a rede é pública.

## Aviso do Windows ao abrir

O executável não é assinado, então o SmartScreen pode avisar na primeira vez: **Mais informações → Executar assim mesmo**.
Baixe só da página de [Releases](https://github.com/renanabade/control-center/releases).

## Painel

- **Abre por trás de outra janela:** o app força o painel para a frente ao abrir pelo ícone. Se ainda acontecer,
  mande o log.
- **Não fecha ao clicar fora:** o painel fecha quando outra janela recebe o foco. Os menus do próprio app não contam
  como "fora".
