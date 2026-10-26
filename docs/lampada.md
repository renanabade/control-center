# Configurar a lâmpada

O app fala com a lâmpada Tuya **direto pela rede local** (protocolo 3.3, TCP 6668) e, se o Wi-Fi dela estiver
dormindo, pelo **Bluetooth**. Os dois caminhos exigem a `local_key` da lâmpada.

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
3. O script grava `id` e `local_key` em `%APPDATA%\win-control\lamp_config.json`. A chave não aparece na tela e o
   login não fica salvo.
4. Reinicie o Control Center.

> A chave muda se a lâmpada for **removida e adicionada de novo** no app. Nesse caso o log mostra
> `Err 914 (local_key ou versao errada)`. Rode o script outra vez.

## Bluetooth (opcional, recomendado)

Lâmpadas G-Light/Tuya desligam o Wi-Fi para economizar energia depois de um tempo apagadas, e o Bluetooth fica
ligado o tempo todo. Com `ble_mac` configurado, quando ligar pelo Wi-Fi falha, o app:

1. procura a lâmpada pelo Bluetooth e manda "ligar", usando o protocolo Tuya BLE e a mesma `local_key`;
2. uns 2,5 s depois, quando o Wi-Fi volta, acerta brilho e temperatura por ele.

Enquanto a lâmpada está nesse estado, o painel mostra **"Dormindo (acorda pelo Bluetooth)"** em vez de "Sem conexão".

**Achar o MAC:** procure, num app de scan BLE (por exemplo, nRF Connect no celular), um dispositivo chamado `TY`
com o serviço `0xA201`. O MAC tem o formato `42:07:67:06:8A:51`.

O PC precisa ter Bluetooth ligado. Não é preciso parear: o Windows conecta direto.

## Fixar o IP

Mesmo podendo deixar `ip` vazio, é bom reservar o IP da lâmpada no roteador (DHCP estático pelo MAC Wi-Fi). Assim a
primeira conexão é imediata. Quando o IP muda, o app percebe depois de 3 falhas e atualiza o `lamp_config.json`
sozinho.
