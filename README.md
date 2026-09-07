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
| 2 | Configurações em `settings.json` (núcleo) | ⬜ |
| 3 | Atalho global + captura + salvamento | ⬜ |
| 4 | Área de transferência multiformato | ⬜ |
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
