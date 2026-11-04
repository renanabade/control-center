# control-center: notas para agentes

- Código e comentários em português, no estilo dos arquivos existentes (comentários curtos que explicam o porquê).
- Compilar: `dotnet build -c Release` em `src\WinControl`. Antes, feche o app (`Stop-Process -Name WinControl`),
  senão o `.exe` fica travado.
- **Nunca abra o app nem rode `tools/*.py` direto do shell do agente** quando ele roda dentro do Claude Desktop: o
  Windows redireciona `%APPDATA%` e o registro para `%LOCALAPPDATA%\Packages\Claude_*\LocalCache`, e o app passa a
  usar uma config diferente da do usuário. Use `explorer.exe "<caminho do exe>"` ou o terminal do usuário. Leituras
  e gravações em `%APPDATA%\win-control` feitas desse shell também podem cair na cópia isolada.
- A config do usuário (`%APPDATA%\win-control`) tem a `local_key` da lâmpada: nunca a imprima, nunca a commite.
- Instalar em outro PC e levar a configuração das lâmpadas: siga `docs/outro-pc.md`. O arquivo `.ccconfig` tem as chaves:
  nunca entra no repositório.
- O app precisa funcionar em qualquer PC: nada de modelo, monitor, caminho, atalho ou preferência de uma pessoa preso no
  código. O que varia vira opção em Configurações, com um padrão que não mexe em nada sozinho.
- `Lamp.*` são comandos para "todas as lâmpadas da rede atual"; `Lampada` é uma só. IDs, MACs e User Code reais nunca vão
  para o repositório, que é público.
- Regras das cenas (não mudar sem o usuário pedir): cena/agenda nunca acende lâmpada apagada; o ajuste da cena vale
  para um único "ligar"; ligar sem cena pendente mantém o último estado da lâmpada. Ver README.
- Teste rápido sem interface: `WinControl.exe --selftest` grava `selftest.txt` na pasta de config.
- API: `dotnet test --project tests\ControlCenter.Api.Tests` (sem hardware). Ela só alcança os aparelhos pelo pipe do
  app da bandeja (`ApiBridge.cs`); comandos novos entram lá e na `PonteSimulada`. Veja `docs/api.md`.
- A API é opcional e vem desligada: sem `api_ativa`, o app não abre o pipe e precisa funcionar como se ela não existisse.
