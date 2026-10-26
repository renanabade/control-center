# control-center: notas para agentes

- Código e comentários em português, no estilo dos arquivos existentes (comentários curtos que explicam o porquê).
- Compilar: `dotnet build -c Release` em `src\WinControl`. Antes, feche o app (`Stop-Process -Name WinControl`),
  senão o `.exe` fica travado.
- **Nunca abra o app nem rode `tools/*.py` direto do shell do agente** quando ele roda dentro do Claude Desktop: o
  Windows redireciona `%APPDATA%` e o registro para `%LOCALAPPDATA%\Packages\Claude_*\LocalCache`, e o app passa a
  usar uma config diferente da do usuário. Use `explorer.exe "<caminho do exe>"` ou o terminal do usuário. Leituras
  e gravações em `%APPDATA%\win-control` feitas desse shell também podem cair na cópia isolada.
- A config do usuário (`%APPDATA%\win-control`) tem a `local_key` da lâmpada: nunca a imprima, nunca a commite.
- Regras das cenas (não mudar sem o usuário pedir): cena/agenda nunca acende lâmpada apagada; o ajuste da cena vale
  para um único "ligar"; ligar sem cena pendente mantém o último estado da lâmpada. Ver README.
- Teste rápido sem interface: `WinControl.exe --selftest` grava `selftest.txt` na pasta de config.
