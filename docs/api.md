# ControlCenter.Api

API REST para controlar o control-center por outros aparelhos da rede: celular, outro PC, automações.

## Como funciona

```
celular ── HTTP + JWT ──> ControlCenter.Api ── named pipe ──> WinControl (bandeja) ──> lâmpada, telas, tema, DualSense
                          serviço, sessão 0                   sessão do usuário
                               │
                               └── SQLite (histórico)
```

**Por que dois processos.** Um serviço do Windows roda na sessão 0, sem o `HKCU` do usuário (onde ficam o tema e a Luz
Noturna) e sem acesso aos monitores pelo DDC/CI. Por isso a API não fala com os aparelhos: ela pede ao app da bandeja,
que continua sendo o único dono deles e usa os mesmos métodos do painel. As regras das cenas valem igual.

**Se o app estiver fechado**, ninguém está logado ou o app não abriu: a API continua de pé, com login e histórico
funcionando. Os comandos respondem `503` e `/health/pronto` responde `Degraded`.

**O pipe** (`ControlCenter.Ponte.v1`) só aceita o usuário que abriu o app e a conta `LocalService`, e recusa conexões
pela rede. Parado, ele só espera uma conexão, sem thread própria nem consulta periódica.

## Rodar

### Sem hardware, para experimentar (qualquer sistema)

```powershell
dotnet run --project src\ControlCenter.Api
```

Abre o Swagger em `http://localhost:5080/swagger`, com aparelhos simulados. Usuário `admin`, senha `control-center-dev`:
ela é pública e só existe no ambiente de desenvolvimento, nunca é publicada junto com a API.

Com Docker, também simulado: copie `.env.example` para `.env`, preencha e rode `docker compose up --build`.

### De verdade, como serviço do Windows

Num PowerShell **como administrador**, com o app da bandeja já compilado:

```powershell
.\tools\instalar-servico.ps1
```

O script publica a API em `Program Files`, cria o serviço na conta `LocalService`, abre a porta 5080 **só em redes
particulares** e mostra a senha gerada, uma única vez. Para remover: `.\tools\remover-servico.ps1`.

Dados em `%ProgramData%\ControlCenter`: `api.settings.json` (hash da senha e chave do JWT) e `controlcenter.db`
(histórico). Para trocar a senha, rode `ControlCenter.Api.exe setup --substituir` e reinicie o serviço.

### Segurança na rede

A API usa **HTTP sem criptografia**: a senha e o token passam abertos pela rede local. Isso é aceitável numa rede de
casa em que você confia, e por isso o firewall fica fechado em redes públicas. **Não exponha a porta na internet.**
Para acessar de fora de casa, use uma VPN como o Tailscale. Para HTTPS, configure um certificado no
[Kestrel](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/endpoints) pelo `api.settings.json`.

## Usar

```bash
TOKEN=$(curl -s http://PC:5080/api/v1/auth/login -H 'Content-Type: application/json' \
  -d '{"usuario":"admin","senha":"..."}' | jq -r .token)

curl -X POST http://PC:5080/api/v1/cenas/Noite/aplicar -H "Authorization: Bearer $TOKEN"
```

| Método e rota (`/api/v1`) | O quê |
|---|---|
| `POST /auth/login` | Troca usuário e senha por um token (12 h). 5 tentativas por minuto, por IP. |
| `GET /estado` | Tudo de uma vez. |
| `GET /cenas` | Lista as cenas. |
| `POST /cenas/{nome}/aplicar` | Aplica uma cena. |
| `GET /dispositivos/lampada` | Estado da lâmpada. |
| `PUT /dispositivos/lampada/energia` | `{"ligada": true}` |
| `PATCH /dispositivos/lampada` | `{"brilho": 50, "temperatura": 30}` ou `{"matiz": 240, "saturacao": 1000}` |
| `PUT /dispositivos/lampada/timer` | `{"minutos": 30}` (0 cancela) |
| `GET /dispositivos/telas` | Monitores e brilho. |
| `PUT /dispositivos/telas/brilho` | Todas as telas: `{"valor": 40}` |
| `PUT /dispositivos/telas/{nome}/brilho` | Uma tela. |
| `GET /aparencia` | Tema e Luz Noturna. |
| `PUT /aparencia/tema` | `{"tema": "escuro"}` |
| `PUT /aparencia/luz-noturna` | `{"ligada": true}` |
| `GET /dispositivos/dualsense` | Conexão, bateria, luz e modo. |
| `PATCH /dispositivos/dualsense` | `{"cor": "#FF0000", "intensidade": 60, "modo": "xbox360", "automatico": false}` |
| `GET /historico` | Comandos executados. Parâmetros: `pagina`, `tamanhoPagina` (até 100), `acao`, `sucesso`, `de`, `ate`. |

Fora de `/api/v1`, e sem token: `/health` (o processo responde) e `/health/pronto` (banco e app da bandeja).

### Comportamentos que valem conhecer

- **Cena nunca acende lâmpada apagada.** O ajuste fica combinado para o próximo "ligar" e aparece como
  `lampada.ajustePendente: true`. Veja [as regras](../README.md#como-as-cenas-funcionam).
- **Ajustar uma lâmpada apagada responde `409`** em vez de acendê-la. No painel, o ajuste com ela apagada também não a acende.
- **Telas respondem `202`:** o DDC/CI é lento, então o brilho entra numa fila no app e pode ainda não ter chegado ao monitor.
- **Ligar a Luz Noturna** também leva a lâmpada acesa a 1%, como já acontece quando ela liga por qualquer outro caminho.

### Erros

Todo erro sai como [ProblemDetails](https://www.rfc-editor.org/rfc/rfc9457) (`application/problem+json`):

| Status | Quando |
|---|---|
| `400` | Pedido inválido. Em erros de validação, `errors` diz o campo. |
| `401` | Sem token, token vencido, ou login incorreto. |
| `404` | Cena ou tela não existe. |
| `409` | Lâmpada apagada ou ocupada com outro comando. |
| `429` | Pedidos demais. O cabeçalho `Retry-After` diz quanto esperar. |
| `503` | App da bandeja fechado, ou o aparelho não respondeu. |

### Histórico

Grava todo comando que chegou aos aparelhos, inclusive os que falharam, com usuário, duração e resultado. Leituras,
logins e pedidos recusados na validação não entram.

## Desenvolver

```powershell
dotnet test --project tests\ControlCenter.Api.Tests       # não precisa de hardware nem do app aberto
dotnet tool restore
dotnet ef migrations add NomeDaMudanca --project src\ControlCenter.Api --output-dir Dados\Migrations
```

Os testes sobem a API inteira com `WebApplicationFactory`, usando aparelhos simulados e um SQLite próprio em pasta
temporária. Os testes da ponte usam um named pipe de verdade, com um servidor de teste no lugar do app.

**O que eles não cobrem:** o lado do app (`ApiBridge.cs`) depende do Windows e do hardware. As regras reais das cenas
continuam sendo testadas pelo `WinControl.exe --selftest --selftest-scenes`.

| Projeto | Papel |
|---|---|
| `src/ControlCenter.Contracts` | Mensagens e protocolo do pipe, usados pelos dois lados. |
| `src/ControlCenter.Api` | A API. Multiplataforma; só a ponte real exige Windows. |
| `src/WinControl/Services/ApiBridge.cs` | Servidor do pipe, dentro do app da bandeja. |
| `tests/ControlCenter.Api.Tests` | xUnit + WebApplicationFactory. |
