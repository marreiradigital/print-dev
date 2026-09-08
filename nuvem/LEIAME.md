# Envio temporário para a nuvem

O serviço que recebe uma captura do Print Dev e devolve uma URL que **morre sozinha em 48 horas**.

Existe por um limite do produto: o caminho do arquivo só vale na sua máquina. Para colar num chat
de suporte, num issue ou num prompt, é preciso uma URL. E uma URL de captura não deve durar para
sempre — por isso a expiração não é opcional nem configurável pelo usuário.

## O endereço

```
POST   https://printdev.marreira.dev/i/novo    envia
GET    https://printdev.marreira.dev/i/<id>    baixa
DELETE https://printdev.marreira.dev/i/<id>    apaga antes da hora
```

O site do projeto mora no mesmo hostname, servido pelo GitHub Pages. A rota do Worker é
`printdev.marreira.dev/i/*` — estreita de propósito: um padrão como `/i*` engoliria `/index.html`.

| Peça | Valor |
|---|---|
| Worker | `printdev-nuvem` |
| KV | `printdev-imagens` · `7ff85d3448c04fc6be85cb2a55ca56d8` |
| Zona | `marreira.dev` · `34ef8bea57472540457e161f5e4d008e` |
| Registro DNS | `printdev.marreira.dev` CNAME → `marreiradigital.github.io`, **proxiado** |

O registro **precisa** ficar proxiado (nuvem laranja): rota de Worker não dispara em registro
DNS-only. O SSL da zona está em `full`, que é o que evita o laço de redirecionamento clássico de
GitHub Pages atrás da Cloudflare.

## Publicar

```bash
pnpm dlx wrangler@latest deploy
```

O `wrangler.jsonc` já traz KV, rota e a variável. Não há segredo para enviar (ver abaixo).

## A chave do aplicativo

O Print Dev manda `x-chave` em todo envio. O Worker guarda apenas o **SHA-256** dela, em
`HASH_DA_CHAVE` — por isso a configuração pode viver neste repositório público sem revelar nada:
não há como voltar de um digesto para um valor aleatório de 256 bits.

A chave em si mora em `~/.secrets/printdev-chave-nuvem.txt`, nunca entra no git, e chega ao
executável como metadado de compilação (`-p:ChaveDaNuvem=…` no `gerar-instalador.ps1`).

**Ela não é uma defesa forte, e não deve ser tratada como tal.** O executável é público: quem
desmontar o binário tira a chave. O que ela faz é barrar o curioso que descobriu o endereço, não o
adversário determinado.

## O que segura abuso, de verdade

| Camada | Onde | Efeito |
|---|---|---|
| Limite por endereço | Regra de limite do WAF na zona | 5 requisições por 10s por IP em `/i/novo`, bloqueio de 10s |
| Tamanho | Worker | 10 MB, medido no corpo lido e não no `Content-Length` |
| Tipo | Worker | Só PNG, JPEG e WebP, **conferindo os bytes iniciais** — cabeçalho mentiroso não passa |
| Teto absoluto | KV | 1.000 gravações por dia no plano gratuito, para o serviço inteiro |

A regra do WAF fica na borda, **antes** do Worker, então protege também a cota de requisições.
O plano gratuito só permite janela de 10 segundos — foi por isso que ficou 5/10s e não 30/min.

> O binding `ratelimit` dos Workers foi tentado primeiro e **não disparou em teste nenhum** (30
> requisições seguidas passaram). Foi removido em vez de mantido "por garantia": defesa que não se
> prova funcionando é pior que nenhuma, porque dá a impressão de que existe.

## Os limites do plano gratuito

| Recurso | Grátis | O que acontece ao estourar |
|---|---|---|
| Gravações no KV | 1.000/dia | O envio falha com uma frase explicando, não com erro de rede |
| Leituras no KV | 100.000/dia | Aliviado pelo cache de borda de 10 min |
| Armazenamento | 1 GB | Com expiração de 48h, é muita folga |
| Requisições do Worker | 100.000/dia | — |

Passar disso exige o plano Workers pago (US$ 5/mês, que já inclui 1 milhão de gravações/mês).
**As 1.000 gravações por dia são do serviço inteiro, somando todos os usuários** — é o teto real
de um serviço público mantido de graça.

## Privacidade

- O envio é **sempre manual**. Nada sai da máquina sem alguém clicar.
- O identificador tem 96 bits de entropia (12 bytes aleatórios em base64url, 16 caracteres). Não é
  adivinhável, mas **quem tem o link vê a imagem** — é um segredo de capacidade, não uma senha.
- Toda resposta leva `X-Robots-Tag: noindex, nofollow, noarchive`.
- O token de exclusão é guardado só como digesto: ler o KV inteiro não permite apagar imagem alheia.
  A comparação é em tempo constante.
- Apagar remove do KV **e** do cache de borda. Sem isso, uma imagem excluída continuaria sendo
  servida por até 10 minutos.
