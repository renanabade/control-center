# Instalar em outro PC

## 1. No PC que já está configurado

Em **Configurações → Geral → Levar para outro PC**, clique em **Exportar** e crie uma senha. Fica na Área de Trabalho o
arquivo `control-center.ccconfig`, com lâmpadas (IDs, chaves, IPs, Bluetooth e redes), cenas, agenda, controle e
periféricos. A senha da API e o log ficam de fora, porque são de cada PC.

Leve o arquivo por pen drive ou por uma pasta de nuvem sua. **Nunca o coloque no repositório**, que é público: ele está
cifrado, mas continua contendo as chaves das lâmpadas.

## 2. No outro PC

Baixe a versão em [Releases](https://github.com/renanabade/control-center/releases), extraia e abra `WinControl.exe`.
Não precisa instalar o .NET. Para a API, que é opcional, é preciso a pasta do projeto e o .NET 10 SDK.

Ou compile a partir do código, seguindo o [começo rápido](../README.md#começo-rápido-a-partir-do-código).

Depois, em **Configurações → Geral → Importar**, escolha o arquivo e digite a senha. Na **mesma rede**, as lâmpadas
aparecem sozinhas, porque o lugar é reconhecido pelo roteador. Se o IP de alguma tiver mudado, o app acha de novo.

Por fim, feche e abra o app, para aplicar também o controle e os periféricos.

## Para agentes

Se o usuário pedir para instalar e configurar as lâmpadas neste PC:

1. Compile e abra o app como descrito em `CLAUDE.md`.
2. Peça o caminho do arquivo `.ccconfig`. A senha é só do usuário: ele a digita na tela de **Importar**. Não peça a senha
   na conversa, não copie o arquivo para dentro do repositório e não abra o conteúdo decifrado.
3. Sem o arquivo, a alternativa é `tools/pegar_chave.py` com o User Code do Smart Life, numa janela do próprio usuário.
4. Confira com `WinControl.exe --selftest`: a linha `REDE` deve mostrar as lâmpadas em `aqui`. Não imprima as chaves.
