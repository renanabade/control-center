# Referência dos arquivos de configuração

Os dois arquivos ficam em `%APPDATA%\win-control`. Pela bandeja: **Abrir pasta de configuração**.

## `lamp_config.json`

```json
{
  "id": "eb98c0b4...",
  "ip": "192.168.100.28",
  "local_key": "16 caracteres",
  "version": 3.3,
  "ble_mac": "42:07:67:06:8A:51"
}
```

Veja [lampada.md](lampada.md). Se o arquivo não existir, o app cria um modelo na primeira vez que abrir.

## `conforto.json`

Cenas, agenda e opções. Você pode editar pelo painel (botão direito numa cena) ou à mão (bandeja → **Editar cenas e
agenda**). O app percebe quando o arquivo muda e recarrega sozinho.

```json
{
  "tema_automatico": true,
  "telas_juntas": false,
  "agenda_ativa": true,
  "agenda": [
    { "hora": "05:00", "telas": 85 },
    { "hora": "18:30", "telas": 0 },
    { "hora": "22:00", "cena": "Noite" }
  ],
  "cenas": [
    { "nome": "Trabalho", "lampada": { "ligada": true, "brilho": 50, "temperatura": 50 },
      "telas": 85, "tema": "escuro", "luz_noturna": false },
    { "nome": "Noite", "lampada": { "ligada": true, "brilho": 1, "temperatura": 0 },
      "telas": 0, "tema": "escuro", "luz_noturna": true },
    { "nome": "Filme", "lampada": { "ligada": false }, "telas": 40, "tema": "escuro" }
  ]
}
```

### Opções

| Chave | Efeito |
|---|---|
| `tema_automatico` | O tema acompanha a Luz Noturna: escuro quando ela liga, claro quando desliga. Se a mudança da Luz Noturna veio de uma cena, vale o tema da cena. |
| `telas_juntas` | Mexer no brilho de uma tela mexe em todas. |
| `agenda_ativa` | Liga ou desliga a agenda. |

### Partes de uma cena ou item da agenda

Todas são opcionais. O que não aparece não é alterado.

| Chave | Valores | Observação |
|---|---|---|
| `lampada` | `{"ligada": true, "brilho": 1-100, "temperatura": 0-100}` ou `{"ligada": false}` | Temperatura: 0 é a mais quente (2700K), 100 a mais fria (6500K). Nunca acende uma lâmpada apagada: guarda o ajuste para o próximo "ligar". `ligada: false` apaga só quando você escolhe a cena, nunca pela agenda. |
| `telas` | `40` (todas) ou `{"LG ULTRAGEAR": 40, "Notebook": 20}` | O nome é o que aparece no painel. |
| `tema` | `"escuro"` ou `"claro"` | |
| `luz_noturna` | `true` ou `false` | Liga ou desliga a Luz Noturna do Windows. |
| `cena` | nome de uma cena | Só na agenda: aplica a cena inteira. |

No painel aparecem as 4 primeiras cenas. No menu da bandeja aparecem todas. Os ícones conhecidos são Trabalho,
Noite e Filme; as outras cenas usam um ícone genérico.
