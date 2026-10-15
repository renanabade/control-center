# win-control

Central de controle do Windows na bandeja, feita em C# com WinUI 3: lâmpada Tuya pela rede local, brilho dos monitores
(DDC/CI e WMI), tema claro/escuro e Luz Noturna, cenas, agenda, projetar (Win+P) e giro de um monitor por atalho.

Configuração e chaves ficam em `%APPDATA%\win-control` (nunca no repositório). Veja `lamp_config.example.json`.

Compilar: `dotnet build -c Release` em `src\WinControl`.
