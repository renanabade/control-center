# Configurar as lâmpadas

O app fala com as lâmpadas Tuya **direto pela rede local** (protocolos 3.3 e 3.5, TCP 6668) e, se o Wi-Fi de uma delas
estiver dormindo, pelo **Bluetooth**. Os dois caminhos exigem a `local_key` da lâmpada.

## Várias lâmpadas, em vários lugares

**O painel mostra só as lâmpadas da rede em que o PC está.** Na sua casa, aparecem as de lá; na casa de outra pessoa,
as de lá. As outras nem são consultadas, então não atrasam nada. Não há nada para escolher:

- Cada lâmpada aprende onde mora: fica registrada a rede em que ela foi adicionada e toda rede em que der sinal de vida.
- O lugar é o **roteador**, não o nome do Wi-Fi: 2,4 GHz, 5 GHz e cabo da mesma casa contam como um lugar só. VPN não muda de lugar.
- Lâmpada apagada ou dormindo continua aparecendo no lugar dela.
- Para corrigir manualmente, use **Pertence à rede em que o PC está agora** em Configurações → Lâmpada.

Com mais de uma no lugar:

| Onde | O que faz |
|---|---|
| Bloco Lâmpadas no painel | Com alguma acesa, apaga todas. Com todas apagadas, liga todas. |
| Seta do bloco | Abre os controles. O **nome** de cada lâmpada marca se ela entra nos ajustes, em qualquer combinação, e o **ícone** liga ou apaga só ela. **Todas** marca todas; com todas marcadas, um clique limpa a seleção. A escolha continua valendo até o app fechar. |
| Menu da bandeja | Tem o liga/desliga de cada uma. |
| Cenas e agenda | Valem para todas as daqui, com as [mesmas regras](../README.md#como-as-cenas-funcionam), lâmpada por lâmpada. |

**Como o app acha cada uma:** ele escuta os avisos UDP que elas mandam. Quem não avisa, e continua sem IP, é procurado
entre os aparelhos da rede local com a porta 6668 aberta, no máximo a cada 5 minutos. Só a lâmpada dona da chave consegue
responder, e a pergunta apenas lê o estado. Sem nenhuma lâmpada configurada, nada disso roda.

Tudo isto se preenche em **Configurações → Lâmpada** (bandeja → **Configurações**), que também testa a conexão. A chave
guardada nunca volta para a tela.

## O que precisa

| Campo | O que é | Como conseguir |
|---|---|---|
| `id` | ID do dispositivo. É o mesmo "ID virtual" que aparece no Smart Life. | `tools/pegar_chave.py` |
| `local_key` | Chave de 16 caracteres que cifra os comandos | `tools/pegar_chave.py` |
| `ip` | IP da lâmpada na rede | Pode ficar vazio: o app acha sozinho pelo aviso UDP que a lâmpada manda. |
| `version` | Versão do protocolo | `3.3` |
| `ble_mac` | MAC Bluetooth (opcional) | Veja abaixo |

## Buscar a chave (QR code, sem portal da Tuya)

Rode isto num terminal **normal** do Windows (PowerShell ou Terminal), não de dentro de outro app. Veja o
[motivo](problemas.md#nada-do-que-eu-mudo-tem-efeito).

```powershell
winget install Python.Python.3.13
py -m pip install -r tools\requirements.txt
py tools\pegar_chave.py
```

1. O script pede o **User Code**: no Smart Life, vá em **Eu → engrenagem → Conta e Segurança → User Code**.
2. Abre um QR code. No Smart Life, toque em **Eu → ícone de scan**, escaneie e confirme.
3. O script grava **todas as lâmpadas da conta** em `%APPDATA%\win-control\lamp_config.json`. As chaves não aparecem
   na tela e o login não fica salvo. Tem lâmpadas em outra conta? Rode de novo com o User Code dela: elas se somam.
   Rode o script **estando na rede dessas lâmpadas**, para elas ficarem no lugar certo.
4. Em **Configurações → Lâmpada**, clique em **Recarregar do arquivo**.

> A chave muda se a lâmpada for **removida e adicionada de novo** no app. Nesse caso o log mostra
> `Err 914 (local_key ou versao errada)`. Rode o script outra vez.

## Bluetooth (opcional, recomendado)

Lâmpadas G-Light/Tuya desligam o Wi-Fi para economizar energia depois de um tempo apagadas, e o Bluetooth fica
ligado o tempo todo. Com `ble_mac` configurado, quando ligar pelo Wi-Fi falha, o app:

1. procura a lâmpada pelo Bluetooth e manda "ligar", usando o protocolo Tuya BLE e a mesma `local_key`;
2. uns 2,5 s depois, quando o Wi-Fi volta, acerta brilho e temperatura por ele.

Enquanto a lâmpada está nesse estado, o painel mostra **"Dormindo (acorda pelo Bluetooth)"** em vez de "Sem conexão".

**Achar o MAC:** procure, num app de scan BLE (por exemplo, nRF Connect no celular), um dispositivo chamado `TY`
com o serviço `0xA201`. O MAC tem o formato `AA:BB:CC:11:22:33`.

O PC precisa ter Bluetooth ligado. Não é preciso parear: o Windows conecta direto.

## Fixar o IP

Mesmo podendo deixar `ip` vazio, é bom reservar o IP da lâmpada no roteador (DHCP estático pelo MAC Wi-Fi). Assim a
primeira conexão é imediata. Quando o IP muda, o app percebe depois de 3 falhas e atualiza o `lamp_config.json`
sozinho.
