# Roteiro de testes manuais

O que a suíte automatizada **não** consegue provar: registro de atalho global, geometria
entre monitores, e — o mais importante — o que cada programa faz ao receber um `Ctrl+V`.

Marque a cada release. Um item que falha aqui vale mais que dez testes verdes.

---

## 1. A matriz de colagem

**É o teste mais importante do projeto.** Capture uma região e dê `Ctrl+V` em cada alvo.

### Devem colar a IMAGEM

| Alvo | Observação |
|---|---|
| WhatsApp Web (Chrome) | Abre a prévia de envio |
| Discord (aplicativo) | |
| Slack (aplicativo) | |
| Microsoft Word | Imagem embutida no texto |
| Corpo de e-mail no Outlook | ⚠️ Se virar **anexo**, o formato de arquivo está ligado |
| Paint | |
| Photoshop / GIMP / Paint.NET | ⚠️ **Sem fundo preto** — é a prova de que o canal alfa está certo |
| Figma / Excalidraw (Chrome) | |
| Google Docs (Chrome) | |
| PowerPoint | |

### Devem colar o CAMINHO

| Alvo | Observação |
|---|---|
| Windows Terminal (PowerShell) | Com aspas, se houver espaço no caminho |
| `cmd.exe` | |
| Bloco de Notas | |
| VS Code, arquivo `.ts` | |
| `<textarea>` e `<input>` em qualquer página | |
| Barra de endereço do Explorador | |
| Prompt do Claude Code | |

### Casos de fronteira

- [ ] Explorador, dentro de uma pasta, com `incluirArquivo` **ligado**: cola o arquivo.
- [ ] O mesmo com `incluirArquivo` **desligado**: não cola nada. *(Provar os dois.)*
- [ ] `Win+V` e colar do histórico do Windows — anotar o que sai.
- [ ] Colar num Bloco de Notas aberto **como administrador**.
- [ ] `Ctrl+Shift+V` num editor de imagem: cola o **caminho**, não a imagem.
- [ ] `Ctrl+Alt+V` num editor de texto: cola a **imagem**, se ele aceitar.

### Prova objetiva

Um inspetor de área de transferência (NirSoft *InsideClipboard*) mostra **quais formatos**
estão presentes e **em que ordem** — vale mais que os vinte testes de aplicativo acima.

A ordem esperada é: `PNG`, `CF_DIBV5`, `CF_DIB`, `CF_UNICODETEXT`. Os formatos
`CF_BITMAP`, `CF_TEXT`, `CF_OEMTEXT` e `CF_LOCALE` aparecem **depois**: são sintetizados
pelo próprio Windows.

---

## 2. Captura e geometria

- [ ] Arrastar do monitor 1 para o 2, cruzando a fronteira: recorte contínuo e do tamanho
      certo.
- [ ] Arrastar passando por uma **área morta** do desktop virtual (existe quando os
      monitores têm alturas diferentes): a moldura não é desenhada no vazio.
- [ ] Repetir os dois depois de mudar a escala de um monitor para 150% **com o programa
      aberto** — prova que o PerMonitorV2 funciona sem reiniciar.
- [ ] Modo Janela: destacar janela maximizada, janela de aplicativo da Store, e uma com
      sombra. **Sem borda escura** no recorte.
- [ ] Desconectar e reconectar um monitor com o programa aberto.
- [ ] Capturar com um vídeo protegido na tela: sai preto, e o aviso de tela preta aparece
      no log. *(É limitação da plataforma, não defeito.)*
- [ ] 200 capturas seguidas: a contagem de identificadores GDI no Gerenciador de Tarefas
      fica **estável**.

---

## 3. Atalhos

- [ ] `PrtSc` abre o seletor.
- [ ] Com outro capturador rodando (Lightshot, ShareX, Greenshot): o log **nomeia** o
      programa em vez de dizer só que falhou.
- [ ] Fechar o concorrente sem reiniciar o Print Dev: em até 30 segundos o atalho é
      reavido.
- [ ] `PrtSc` com o Gerenciador de Tarefas em primeiro plano: **não funciona** no modo
      normal, **funciona** no modo elevado.
- [ ] Trocar um atalho no `settings.json` com o programa aberto: vale na hora.
- [ ] Configurar `Win+Shift+S`: recusado com o motivo escrito.

---

## 4. Interface

- [ ] Menu da bandeja segue o tema escuro.
- [ ] Trocar o tema do Windows com o painel aberto e `tema: sistema`: o painel acompanha.
- [ ] Tema claro: percorrer as oito seções procurando texto sem contraste.
- [ ] Maximizar o painel: **não cobre a barra de tarefas** e não sangra para fora da tela.
- [ ] Redimensionar pelas bordas e pelos cantos.
- [ ] Navegar o painel inteiro **só pelo teclado**: o anel de foco aparece sempre.
- [ ] Nada colado: `rg 'Margin="[0-9]' src/PrintDev.App/Settings src/PrintDev.App/Overlay`
      volta vazio.

---

## 5. Anotação e ocultação

- [ ] Cada uma das oito ferramentas.
- [ ] `Ctrl+Z` e `Ctrl+Y` em sequência.
- [ ] **Esconder um trecho de texto e conferir no arquivo salvo** que os pixels foram
      substituídos — abrir a imagem e ampliar. É a promessa que a barra faz.
- [ ] Os três estilos: pixelar, tarja e desfocar.

---

## 6. Ciclo de vida

- [ ] Ícone aparece na bandeja *(o Windows 11 esconde ícones novos no estouro)*.
- [ ] **Clique simples no ícone da bandeja: abre o painel** — e ele vem para a frente,
      não atrás da janela que estava em uso.
- [ ] Abrir o programa de novo: abre as configurações em vez de uma segunda instância.
- [ ] Duplo clique no atalho da Área de Trabalho **com o programa já aberto**: o painel
      vem para a frente. *(Se só piscar na barra de tarefas, o Windows recusou o primeiro
      plano — é o caso que a cessão de direito na instância secundária resolve.)*
- [ ] Ligar "Iniciar com o Windows", reiniciar, e conferir que ele sobe.
- [ ] Ligar o modo elevado: pede elevação **uma vez** e depois sobe sem prompt de UAC.
- [ ] Reiniciar o Explorador (`taskkill /f /im explorer.exe` e abrir de novo): o ícone da
      bandeja **volta**.
- [ ] Editar o `settings.json` à mão com o programa aberto: recarrega.
- [ ] Corromper o `settings.json` de propósito: o programa sobe com os padrões e guarda o
      arquivo antigo.

---

## 7. Limpeza automática

- [ ] Com a limpeza **desligada**, apertar "Simular limpeza": lista vazia.
- [ ] Pôr um arquivo que **não** é do Print Dev na pasta de capturas, ligar a limpeza com
      um prazo curto e simular: **o arquivo alheio não pode aparecer na lista.**
- [ ] Rodar a limpeza de verdade e conferir que os arquivos foram para a **Lixeira**.
