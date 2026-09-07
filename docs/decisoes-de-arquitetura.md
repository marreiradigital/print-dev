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
