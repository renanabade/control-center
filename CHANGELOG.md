# Histórico de versões

Os pacotes estão em [Releases](https://github.com/renanabade/control-center/releases). O que está em aberto fica nas
[issues](https://github.com/renanabade/control-center/issues).

## 1.1.0 (2026-11-04)

O que varia de um PC para outro passou a se ajustar nas configurações.

- **Girar monitor:** escolha qual monitor e o sentido em **Configurações → Aparelhos**. Com mais de uma tela, é preciso
  escolher uma vez.
- **Atalhos de teclado:** podem ser trocados ou desligados. O atalho oculto `Ctrl+Alt+O` saiu.
- **Cenas, agenda e predefinições:** editor na aba **Cenas e agenda**.
- **Agenda:** em instalação nova, vem desligada e vazia. Antes, ela punha as telas a 0% às 18:30.
- **Luz Noturna:** dá para escolher se ela ajusta as lâmpadas acesas, e para quanto.
- **Periféricos:** dá para adicionar outro modelo de mesmo protocolo pelo identificador.
- **Jogos:** outras pastas e o modo padrão ficam na tela.

## 1.0.1 (2026-11-04)

- **Log:** tentativas de conexão que passavam do limite não aparecem mais como erro.
- **Instalador da API:** não diz mais que o serviço não respondeu quando ele está no ar, e os acentos aparecem certos.
- **Pacote:** passa a incluir o arquivo `LICENSE` (MIT).

## 1.0.0 (2026-11-04)

Primeira versão fechada.

- **Várias lâmpadas Tuya**, protocolos 3.3 e 3.5: o painel mostra só as da rede em que o PC está, com seleção de quais
  controlar.
- **Configurações** numa janela com guia, e **arquivo com senha** para levar tudo a outro PC.
- **API REST opcional** em ASP.NET Core, desligada por padrão.
- **DualSense:** luz, emulação de Xbox 360 e PS4, modo automático por jogo.
- **Periféricos:** Razer DeathAdder V2 e Redragon Kumara V2 e Pro com controle direto. Os outros ficam com a Iluminação
  Dinâmica do Windows.
