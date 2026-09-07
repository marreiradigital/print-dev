# Print Dev

Capturador de tela para quem programa. Substitui o `PrtSc` do Windows por um fluxo que resolve o
problema real de um dev: **uma captura, um `Ctrl+V`, e o destino certo recebe o que sabe consumir.**

> Campo que aceita imagem (WhatsApp Web, Word, Figma, Discord) recebe **a imagem**.
> Campo que só aceita texto (terminal, `textarea`, editor de código) recebe **o caminho do arquivo**.
> Sem escolher nada, sem salvar na mão, sem caçar a pasta.

O arquivo é **sempre** salvo em disco antes de ir para a área de transferência.

## Status

Em construção. O que já existe está marcado; o resto é alvo declarado, não promessa cumprida.

| Fase | Entrega | Status |
|---|---|---|
| 0 | Fundação: solução, convenções, auditoria de dependência | ✅ |
| 1 | Bandeja, ciclo de vida, log em arquivo, instância única | ✅ |
| 2 | Configurações em `settings.json` (núcleo) | ✅ |
| 3 | Atalho global + captura + salvamento | ✅ |
| 4 | Área de transferência multiformato | ✅ |
| 5 | Design system (base) | ⬜ |
| 6 | Overlay de seleção multimonitor | ⬜ |
| 7 | Barra pós-captura, aviso, histórico | ⬜ |
| 8 | Painel de configurações | ⬜ |
| 9 | Anotação com borrar/pixelar | ⬜ |
| 10 | Fixar na tela, conta-gotas, repetir região | ⬜ |
| 11 | OCR e limpeza automática | ⬜ |
| 12 | Ícone, publicação, roteiro de testes | ⬜ |

## Requisitos

| Item | Versão |
|---|---|
| Windows | 10 build 19041 (2004) ou superior · desenvolvido no Windows 11 25H2 |
| .NET SDK | 8.0 (para compilar) |
| .NET Runtime | `Microsoft.WindowsDesktop.App` 8.0 (para executar a versão framework-dependent) |

## Como rodar

```bash
git clone <url> print-dev
cd print-dev
dotnet build
dotnet test
dotnet run --project src/PrintDev.App
```

## Estrutura

| Caminho | Papel |
|---|---|
| [`src/PrintDev.Core`](src/PrintDev.Core) | Domínio, serviços e interoperação Win32/WinRT. Sem janela, sem XAML. |
| [`src/PrintDev.App`](src/PrintDev.App) | WPF: bandeja, overlay, painel de configurações, composition root. |
| [`tests/PrintDev.Tests`](tests/PrintDev.Tests) | xUnit sobre a lógica pura do Core. |
| [`docs/decisoes-de-arquitetura.md`](docs/decisoes-de-arquitetura.md) | Por que cada escolha técnica foi feita. |

## Argumentos de linha de comando

| Argumento | Efeito |
|---|---|
| `--silencioso` (`-s`, `/silencioso`, `--tray`) | Sobe direto para a bandeja, sem janela. É o que o autostart usa. |
| `--configuracoes` (`-c`, `--settings`) | Abre o painel de configurações ao iniciar. |
| `--install-elevated-task` | Cria a tarefa do Agendador que inicia o app elevado no logon. Exige elevação. |
| `--uninstall-elevated-task` | Remove essa tarefa. |

As três convenções do Windows são aceitas (`--nome`, `-n`, `/nome`), sem diferenciar maiúsculas.
Argumento desconhecido nunca derruba o app — vai para o log e a execução segue.

## Como o Ctrl+V acerta sozinho

Nenhum programa consegue saber para onde você vai colar. O que resolve isso é publicar
**vários formatos numa transação só**, na ordem certa — o programa de destino pega o
primeiro que sabe consumir.

Ordem publicada em cada captura:

| # | Formato | Quem consome |
|---|---|---|
| 1 | `PNG` (registrado por nome) | **Chromium procura este primeiro** — WhatsApp Web, Discord, Slack, Figma, todo app Electron |
| 2 | `CF_DIBV5` | Bitmap com canal alfa: Word, OneNote, editores de imagem |
| 3 | `CF_DIB` | Bitmap clássico, para quem não lê os anteriores |
| 4 | `CF_HDROP` + `Preferred DropEffect` + `FileNameW` | Explorador do Windows *(desligado por padrão)* |
| 5 | `CF_UNICODETEXT` | **O caminho do arquivo** — terminal, `textarea`, editor de código |

O texto vai por último de propósito: é o que sobra para quem não entende imagem. Campo de
imagem nunca chega nele, porque encontra um formato de imagem antes.

`CF_BITMAP`, `CF_TEXT`, `CF_OEMTEXT` e `CF_LOCALE` aparecem depois na lista — são
**sintetizados pelo próprio Windows** a partir dos que publicamos, e por isso não entram
na conta.

### Três armadilhas que decidiram o projeto

- **O canal alfa zerado.** O `BitBlt` grava zero no byte de alfa de cada pixel, e zero
  significa "totalmente transparente". Publicado como `CF_DIBV5`, isso faria a imagem
  colada aparecer como um retângulo preto em todo programa que respeita o canal. A
  captura força alfa 255 na origem.
- **A ordem das linhas.** Os bitmaps vão de baixo para cima (altura positiva). De cima
  para baixo é mal suportado e aparece invertido em vários programas.
- **O formato HTML fica de fora.** Em campo de texto rico o Chromium prefere HTML à
  imagem, e uma marcação apontando para o disco local cola **imagem quebrada**. É opção
  desligada, não esquecimento.

### Forçar um formato

`Ctrl+Shift+V` cola o caminho; `Ctrl+Alt+V` cola a imagem. Os dois republicam a última
captura no formato pedido e mandam o colar — soltando antes os modificadores que você
ainda está segurando, senão o destino receberia `Ctrl+Shift+V` em vez de `Ctrl+V`.

## Atalhos

Todos configuráveis em `settings.json`, seção `atalhos`. Mudar lá vale na hora.

| Atalho padrão | Ação |
|---|---|
| `PrtSc` | Abre o seletor de área *(hoje captura o monitor sob o cursor; o seletor chega na fase 6)* |
| `Ctrl+PrtSc` | Captura o monitor sob o cursor |
| `Shift+PrtSc` | Captura a janela em primeiro plano |
| `Ctrl+Shift+PrtSc` | Repete o último recorte, na mesma posição |
| `Ctrl+Shift+V` | Cola a última captura como caminho de texto |
| `Ctrl+Alt+V` | Cola a última captura como imagem |
| `Ctrl+Alt+P` | Conta-gotas de cor |
| `Ctrl+Alt+T` | Recorta e copia o texto reconhecido |

Escreva na forma `Ctrl+Shift+PrtSc`. As grafias usuais são aceitas (`PrintScreen`,
`Print`, `Esc`/`Escape`, `PgUp`, setas em português), sem diferenciar maiúsculas.
Combinação que o Windows reserva — `Win+L`, `Win+Shift+S`, `Ctrl+Alt+Del`, `Win+Tab` — é
recusada com o motivo escrito, em vez de aceita e silenciosamente inútil.

### Quando outro programa está com a tecla

O `PrtSc` é a tecla mais disputada do Windows: Lightshot, ShareX, Greenshot, Snagit,
PicPick e até o OneDrive a registram, e quem chega primeiro fica com ela.

O Print Dev **nomeia o culpado** no log em vez de dizer apenas que falhou, e continua
tentando a cada 30 segundos — então fechar o concorrente na mão já basta, sem reiniciar
nada. O comportamento é escolhido em `avancado.aoDetectarConcorrente`:

| Valor | O que faz |
|---|---|
| `perguntar` (padrão) | Registra o conflito nomeando o programa e segue tentando reaver |
| `assumirAutomaticamente` | Encerra o concorrente e assume a tecla |
| `somenteAvisar` | Só registra; não tenta encerrar nada |

O padrão não é `assumirAutomaticamente` porque encerrar processo alheio é destrutivo
demais para acontecer sem o usuário ter pedido.

## Configurações

Ficam em `%APPDATA%\PrintDev\settings.json`, com as chaves em português — o arquivo é feito para
ser editado à mão. O programa **relê sozinho** quando o arquivo muda: salvar no editor já vale, sem
reiniciar nada. O menu da bandeja tem um atalho para abri-lo.

| Seção | Para quê |
|---|---|
| `geral` | Iniciar com o Windows, iniciar elevado, tema |
| `captura` | Modo padrão do seletor, escurecimento, lupa, cursor, atraso, aviso |
| `salvamento` | Pasta, subpasta por data, formato, qualidade JPEG, modelo de nome |
| `areaDeTransferencia` | O que copiar e como o caminho é escrito ao ser colado como texto |
| `atalhos` | Combinações de teclas globais |
| `anotacao` | Cor, espessura e estilo de ocultação |
| `historico` · `limpeza` | Capturas recentes e remoção automática das antigas |
| `avancado` | Nível de log, política ao detectar outro capturador |

Três padrões que são decisão consciente, não esquecimento:

- **`incluirArquivo` e `incluirHtml` nascem desligados.** São os dois formatos de área de
  transferência que causam efeito colateral em programa de terceiro — o `CF_HDROP` faz o Outlook
  anexar em vez de embutir a imagem, e o HTML faz o Chromium preferir uma marcação que aponta para o
  disco local e colar imagem quebrada.
- **`limpeza.ativa` nasce desligada**, e quando ligada só remove arquivo cujo nome casa com o modelo
  do Print Dev, mandando para a Lixeira. Apagar arquivo do usuário exige pedido explícito dele.
- **`aoDetectarConcorrente` é `perguntar`.** Encerrar processo alheio é destrutivo demais para
  acontecer em silêncio, mesmo sendo o que resolve o conflito de atalho.

Arquivo inválido não impede o programa de subir: ele é renomeado para
`settings.corrompido-<data>.json`, os padrões entram no lugar e o motivo vai para o log. Chave que
este código não conhece é preservada na regravação, então uma versão mais nova não perde
configuração ao ser aberta por uma mais antiga.

## Qualidade

- **Auditoria de dependência no próprio `restore`**: `NuGetAudit` com `AuditMode=all` e
  `AuditLevel=low` no [`Directory.Build.props`](Directory.Build.props). Pacote vulnerável —
  inclusive transitivo — quebra o build. Estado atual: **0 vulnerabilidades**.
- **Arquivos `.cs` e `.xaml` em UTF-8 com BOM** ([`.editorconfig`](.editorconfig)). A interface é
  toda em português acentuado; sem o BOM, um build ou editor pode ler o arquivo como ANSI e
  corromper o texto.
- **`TreatWarningsAsErrors` em Release.**

## Solução de problemas

**O ícone não apareceu na bandeja.** Ele apareceu — o Windows 11 esconde todo ícone novo no menu de
estouro (a setinha `^` ao lado do relógio). Para fixá-lo na barra, arraste-o de dentro do estouro
para a área do relógio, ou vá em *Configurações do Windows → Personalização → Barra de tarefas →
Outros ícones da bandeja do sistema* e ligue o Print Dev.

**Abri o programa de novo e nada aconteceu.** É o comportamento correto: só existe uma instância por
sessão. A segunda avisa a primeira e encerra — o registro fica no log
(`%APPDATA%\PrintDev\logs\`).

## Alvos futuros

Documentados aqui para não virarem surpresa, mas **ainda não implementados**:

- **Captura com rolagem** está fora do escopo. A implementação honesta exige UI Automation, vários
  quadros e costura por casamento de imagem — é a funcionalidade com mais defeitos abertos nos
  concorrentes. Para página web inteira, o DevTools do navegador resolve melhor
  (*Capture full size screenshot*).
- **Detecção automática de segredos** só existirá com confirmação humana. Uma ferramenta que promete
  borrar credencial sozinha e falha uma vez faz o usuário vazar a chave confiando nela.
- **Envio para a nuvem** está fora por privacidade: justamente a captura que costuma ter segredo.
