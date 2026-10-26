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
| `A lampada esta anunciando em X, mas nao responde ao PC` | Firewall ou VPN | Libere a porta TCP 6668 na rede local. |
| `Err 914` | Chave errada: a lâmpada foi readicionada no app | Rode `tools/pegar_chave.py` de novo. |
| `Dormindo (acorda pelo Bluetooth)` | Wi-Fi da lâmpada em economia | Normal. Clicar em ligar acorda a lâmpada pelo Bluetooth. |
| `BLE: lampada nao encontrada` | Bluetooth do PC desligado ou longe demais | Ligue o Bluetooth do PC e confira o `ble_mac`. |

## Telas

`sem resposta do DDC/CI`: ative **DDC/CI** no menu do próprio monitor. Alguns monitores só respondem pela entrada
DisplayPort/HDMI que está ativa.

## Painel

- **Abre por trás de outra janela:** o app força o painel para a frente ao abrir pelo ícone. Se ainda acontecer,
  mande o log.
- **Não fecha ao clicar fora:** o painel fecha quando outra janela recebe o foco. Os menus do próprio app não contam
  como "fora".
