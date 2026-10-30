# Desempenho

O app fica o dia todo na bandeja, então parado ele não pode gastar quase nada. Esta página registra o que foi
feito para isso, as medições e o que ainda falta.

## Antes (medido em 30/10/2026, app aberto havia 3h30)
- 3–5% de um núcleo **o tempo todo**, 150 MB de memória privada, 105 threads.
- 545 dos 590 s de CPU vinham de uma thread só: a leitura do DualSense (`DualSense.Reader`). O controle por
  Bluetooth manda cerca de 250 relatórios/s e todos eram processados, mesmo sem controle virtual.
- O detector de jogos usava WMI `WITHIN 1` (duas consultas), o que faz o WmiPrvSE listar todos os processos duas
  vezes por segundo. Esse gasto aparece no WmiPrvSE, não no WinControl.
- O painel WinUI era criado na inicialização e nunca saía da memória.

## O que mudou
| Onde | Antes | Agora |
|---|---|---|
| `DualSense.Reader` | lia todo relatório | sem controle virtual: 20 leituras/s (atalho e bateria continuam funcionando); o controle virtual acorda na hora (`PadEvent`) |
| `DualSense.Forward` | `SubmitReport` a cada relatório | só quando sticks, gatilhos ou botões mudam (o giroscópio é ignorado) |
| Procura do controle | varria todos os HID a cada 2 s | só quando um aparelho entra ou sai (`DeviceList.Local.Changed`), mais uma checagem a cada 60 s |
| `Jogos.Watch` | WMI de processos a cada 1 s | `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`: analisa cada processo uma vez, quando ele vai para a frente; o fechamento é esperado pelo handle do processo |
| Luz Noturna (`Core.NightWatcher`) | lia o registro a cada 3 s | `RegNotifyChangeKeyValue` (`Theme.NightLightWatch`) |
| Periféricos | reenviava a cor a cada 30 s | ao plugar um aparelho, ao voltar da suspensão e a cada 10 min |
| Painel | criado no boot, eterno | criado ao abrir (`App.OpenPanel`) e fechado 3 min depois de escondido (`DispatcherShutdownMode.OnExplicitShutdown`) |
| GC | concorrente | `ConcurrentGarbageCollection=false` |

Mudança de comportamento: o modo automático agora percebe o jogo quando a **janela dele vem para a frente**, não no
instante em que o processo abre. Um jogo que já estava aberto antes do app também passa a ser detectado.

## Medido depois (build Release, aberto pelo Explorer)
- CPU parado: **cerca de 0,2%** de um núcleo (antes, 3–5%). A thread do DualSense praticamente não aparece mais.
- Memória privada: **cerca de 95 MB** (antes, 150 MB), com 67–72 threads. Atenção: o "antes" foi medido depois de
  3h30 aberto e o "agora" com o app recém-aberto.
- Painel: abre, fecha com Esc e, 3 min depois, a janela é destruída sem o app sair.

## Pendente
- **Soltar o painel não devolveu memória**: depois do fechamento a memória privada seguiu em cerca de 97 MB. O
  `Microsoft.UI.Xaml.dll` continua carregado e o framework XAML segura os recursos dele. O ganho real está na CPU (as
  páginas escondidas não se atualizam mais). Para cair abaixo disso, seria preciso tirar a interface do processo da
  bandeja (por exemplo, painel num processo separado), o que é uma mudança maior.
- Não testado ainda: emulação Xbox/PS4 jogando, troca de modo pelo atalho Mute+direcional, detecção automática de
  jogo, reenvio da cor depois da suspensão.
- Conferir o log (bandeja → **Abrir log**) depois de um dia de uso e procurar `ERRO`.
