# Decisões de arquitetura

Registro do **porquê**. O código mostra o quê; este arquivo guarda o motivo, para a decisão não ser
desfeita por engano seis meses depois.

## 1. Stack: C# .NET 8 + WPF

É o único stack que dá, ao mesmo tempo, acesso direto e barato ao Win32 (`SetClipboardData` byte a
byte, `RegisterHotKey`, `BitBlt`, DWM, PerMonitorV2) **e** um motor de composição vetorial capaz do
visual sob medida que o produto exige.

Descartados:

| Alternativa | Por que não |
|---|---|
| Electron / Tauri | A área de transferência multiformato (`CF_DIBV5`, `CF_HDROP`, ordem dos formatos) é inviável pelas APIs deles — e é o coração do produto. |
| Avalonia | Estilização melhor, mas a interoperação Win32 profunda que este app exige é onde o WPF já está maduro. |
| WinUI 3 | Devolve o visual nativo Fluent, exatamente o que o produto não quer, e exige empacotamento MSIX. |
| Python / Qt | Distribuição sofrida no Windows e área de transferência multiformato limitada. |

## 2. TFM `net8.0-windows10.0.19041.0` desde o primeiro commit

Habilita `Windows.Media.Ocr` (reconhecimento de texto nativo, offline, sem dependência externa) e
`Windows.UI.ViewManagement.UISettings`. Custa um download único do pacote
`Microsoft.Windows.SDK.NET.Ref` e cerca de 20 MB na publicação.

Trocar o TFM depois obrigaria a mexer nos três projetos e restaurar tudo de novo. Adotar agora é de
graça.

## 3. `UseWindowsForms` desligado

O `NotifyIcon` do Windows Forms traria cerca de 16 MB (`System.Windows.Forms.dll` mais Primitives) e
um menu de contexto que **não segue o tema escuro** — inaceitável num produto cujo requisito
explícito é não parecer nativo. A bandeja usa `Hardcodet.NotifyIcon.Wpf`, cujo menu é um
`ContextMenu` do WPF e, portanto, estilizável.

## 4. `UseWPF` ligado também no Core

Não é interface: o Core precisa dos codificadores WIC (`PngBitmapEncoder`, `JpegBitmapEncoder`) e de
`BitmapSource`, que moram em `PresentationCore` e `WindowsBase`. É o que permite manter
`System.Drawing.Common` — e todo o ciclo de vida manual de `Bitmap` e `Graphics` do GDI+ — fora do
projeto inteiro. Nenhum XAML e nenhuma janela vivem no Core.

## 5. `PerMonitorV2` no manifesto

A decisão mais importante do `app.manifest`. Sob PerMonitorV2 não há virtualização de DPI:
`GetSystemMetrics(SM_*VIRTUALSCREEN)`, `EnumDisplayMonitors`, `GetCursorPos` e o contexto de
dispositivo da tela já trabalham em **pixels físicos**.

Consequência que elimina uma classe inteira de defeitos: **toda a matemática de seleção vive em
pixels físicos do desktop virtual**, e a conversão para unidades independentes de dispositivo só
acontece na hora de desenhar, por monitor.

Não adicionar `Switch.System.Windows.DoNotScaleForDpiChanges`. Ele aparece em respostas populares
sobre PerMonitorV2 e faz o oposto do que o nome sugere: **desliga** o reescalonamento do WPF.

## 6. Três projetos, não um nem quatro

Sem uma fronteira física, a lógica pura (modelo de nome de arquivo, formatação de caminho, matemática
de seleção) acaba dentro do code-behind de uma janela e deixa de ser testável. Um quarto projeto
separando domínio puro de interoperação custaria mais um `.csproj` e um grafo de interfaces sem ganho
real — essa separação vive como convenção de pasta dentro do Core.

## 7. Dependências

Cada uma precisa justificar por que o nativo não serve.

| Pacote | Por que não dá para fazer nativo |
|---|---|
| `Hardcodet.NotifyIcon.Wpf` | Ver item 3. Zero dependências, MIT, alvo `net8.0`. Não trocar por `H.NotifyIcon.Wpf`: a versão 2.4.1 abandonou o .NET 8 (só `net462` e `net10.0`). |
| `CommunityToolkit.Mvvm` | `[ObservableProperty]` e `[RelayCommand]` por gerador de código. O caminho nativo seriam dezenas de linhas de `INotifyPropertyChanged` por ViewModel, e são cerca de doze. |
| `Serilog` e `Serilog.Sinks.File` | `Microsoft.Extensions.Logging` **não tem provedor de arquivo na caixa** (só Console, Debug e EventSource). O app roda sem console: arquivo é a única forma de diagnosticar. |
| `Microsoft.Extensions.DependencyInjection` | Ordenar o `Dispose` de cerca de vinte serviços (atalhos, bandeja, observador de arquivo, log) na mão é fonte garantida de vazamento. Fixado na linha 8.0.x para não subir acima do runtime instalado. |

Recusados de propósito: `Microsoft.Extensions.Hosting` (o `Application` do WPF já manda no ciclo de
vida; dois donos do encerramento produzem o clássico app que não fecha), `Newtonsoft.Json`
(`System.Text.Json` está no framework), bibliotecas de tema como `MaterialDesignInXaml`, `WPF-UI` e
`ModernWpf` (trazem dicionários com margens próprias que brigam com a regra de espaçamento do projeto
e devolvem visual nativo), `Magick.NET` e `OpenCvSharp` (pixelar é um laço sobre pixels de um
`WriteableBitmap`, contra dezenas de megabytes de binário nativo), `Tesseract` (o reconhecimento de
texto do Windows é nativo) e `FluentAssertions` (a versão 8 passou a ter licença comercial).

## 8. UTF-8 com BOM em `.cs` e `.xaml`

A interface é toda em português acentuado. Sem o BOM, uma ferramenta da cadeia pode interpretar o
arquivo como ANSI e corromper os acentos silenciosamente — o defeito só aparece depois, na tela do
usuário. Fixado no `.editorconfig`.

Consequência prática para quem edita por linha de comando: **conteúdo acentuado nunca deve ser
escrito com `echo >` ou redirecionamento do PowerShell**, que aplicam a página de código do console.

## 9. Memória: um programa que passa o dia na bandeja

Um capturador fica horas parado entre um uso e outro, e cada uso mexe com imagens do tamanho da
área de trabalho inteira (14 MB em dois monitores, 1920×1080 + 1366×768). Até a 0.1.3 isso
acumulava: o processo instalado chegou a **870 MB privados depois de 26 capturas**, com 25 telas
inteiras vivas no heap. As regras abaixo saíram dessa investigação (12/11/2026) e cada uma fecha
uma porta por onde a memória escapava.

| Regra | Por quê |
|---|---|
| Objeto de vida curta que se inscreve em evento de objeto eterno **precisa de um ponto de saída** | O `SelectionState` vive o programa inteiro. A camada do seletor se inscrevia nele e nunca saía, e com isso cada abertura deixava a tela congelada presa para sempre — e ainda redesenhada a cada movimento do mouse. Hoje a janela chama `SelectionLayer.Detach()` ao fechar. |
| A tela congelada existe **uma vez só**, como bitmap nativo (`FrozenScreen`) | Eram duas cópias: o array gerenciado e o bitmap feito dele. O recorte e a lupa saem do bitmap direto. |
| Nada de `TransformedBitmap` ou `CroppedBitmap` guardado por muito tempo | Os dois são preguiçosos e **seguram a imagem de origem inteira**. A miniatura do histórico usava um, e vinte miniaturas seguravam vinte telas cheias. Hoje a redução é feita nos bytes (`CapturedImage.Downscale`). |
| A lupa reaproveita um `WriteableBitmap` pequeno | Um recorte novo por quadro é uma textura nova por quadro para o WPF subir e o coletor recolher. |
| O PNG é codificado uma vez e serve ao disco e à área de transferência | Eram duas codificações idênticas, a etapa mais cara do caminho entre soltar o mouse e poder colar. Recopiar do histórico publica o próprio arquivo. |
| `CF_DIB` e `CF_DIBV5` são escritos direto na memória da área de transferência | Montar num array e copiar deixava dois blocos do tamanho da captura para o coletor a cada publicação. |
| A última captura não guarda pixels se o arquivo existe | Os atalhos de colagem forçada releem do disco na hora. |
| Ao fim de cada trabalho, `WorkingSet.ReleaseSoon()` | Ver abaixo. |

### Por que coletar à mão

O coletor só recolhe blocos grandes — e toda captura é um bloco grande — numa coleta completa, e
só coleta quando o programa aloca. Parado na bandeja, ele quase não aloca: o lixo de uma captura
ficava comprometido por horas. `ReleaseSoon` junta os pedidos próximos e, numa thread de fundo, faz
coleta completa compactando, espera os finalizadores (a memória nativa dos bitmaps do WPF só volta
por eles), coleta de novo e **só então** corta o conjunto de trabalho. Cortar antes de coletar,
como era feito, só mandava o lixo para o arquivo de paginação, e ele continuava contando como memória
do programa.

O mesmo pedido força um quadro do WPF: ele só entrega ao motor de renderização a liberação das
superfícies de uma janela fechada no **próximo quadro desenhado**, e na bandeja nada é desenhado.
Medido: 25 MB das janelas do seletor só voltavam quando a captura seguinte mostrava o aviso.

### O que sobra, e por que não é vazamento

| O que | Quanto | Por que fica |
|---|---|---|
| Dispositivo e caches do Direct3D do WPF | ~60 MB, depois do primeiro seletor | Custo fixo da renderização por hardware, que é o que deixa o arrasto fluido. Não cresce: 6 ou 15 aberturas deixam o mesmo valor. |
| Os bitmaps da última captura | 2× os pixels dela (16 MB numa tela 1920×1080) | O Windows guarda os dados publicados na memória do processo dono enquanto o conteúdo for nosso. Some quando outro programa copia algo. |

### Configuração do runtime

`ConcurrentGarbageCollection` e `TieredPGO` desligados no `PrintDev.App.csproj`: o primeiro mantém
uma thread e estruturas próprias para não pausar a interface em coletas grandes, e o heap aqui fica
em poucos megabytes; o segundo instrumenta métodos e guarda perfil em memória para um caminho quente
que roda algumas vezes por dia.

### Como medir

```powershell
dotnet tool install -g dotnet-counters
dotnet tool install -g dotnet-gcdump
dotnet-counters collect -p <pid> --counters System.Runtime --duration 00:00:05 --format csv -o c.csv
dotnet-gcdump collect -p <pid> -o heap.gcdump; dotnet-gcdump report heap.gcdump
```

O `dotnet-counters monitor` não funciona sem console interativo; use `collect`. "LOH Size" crescendo
a cada captura é o sinal de que algo voltou a segurar imagens.

Mesmo roteiro antes e depois (21 aberturas do seletor com a lupa em movimento, 3 recortes, 3 capturas
de monitor), 12/11/2026:

| | 0.1.3 | Depois |
|---|---|---|
| Memória privada depois do uso | 688 MB, sem voltar | 137 MB |
| Pico de memória física | 774 MB | 170 MB |
| Objetos grandes vivos no heap | 287 MB | 0,1 MB |
