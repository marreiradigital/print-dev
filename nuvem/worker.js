/**
 * Print Dev — envio temporário de capturas.
 *
 * Três rotas, todas sob /i/ para não colidir com o site estático que mora no
 * mesmo hostname (o padrão de rota /i/* não casa com /index.html):
 *
 *   POST   /i/novo      envia uma imagem, devolve a URL e o token de exclusão
 *   GET    /i/<id>      serve a imagem crua, para o chat de destino embutir
 *   DELETE /i/<id>      apaga antes da hora, com o token
 *
 * O objeto morre sozinho em 48h: o TTL é do próprio KV, não de uma rotina de
 * limpeza que pode falhar em silêncio e deixar imagem viva para sempre.
 */

const TTL_SEGUNDOS = 48 * 60 * 60;
const TAMANHO_MAXIMO = 10 * 1024 * 1024;
const CACHE_SEGUNDOS = 600;

/** Tamanho do identificador na URL: 12 bytes aleatórios em base64url = 16 caracteres, 96 bits. */
const BYTES_DO_ID = 12;
const TAMANHO_DO_ID = 16;

/**
 * Só estes três tipos entram, e o cabeçalho enviado não é acreditado: os bytes
 * iniciais do arquivo precisam bater. Sem isso o serviço vira hospedagem de
 * qualquer coisa com um Content-Type mentiroso.
 */
const TIPOS_ACEITOS = {
  'image/png': (b) =>
    b[0] === 0x89 && b[1] === 0x50 && b[2] === 0x4e && b[3] === 0x47 &&
    b[4] === 0x0d && b[5] === 0x0a && b[6] === 0x1a && b[7] === 0x0a,
  'image/jpeg': (b) => b[0] === 0xff && b[1] === 0xd8 && b[2] === 0xff,
  'image/webp': (b) =>
    b[0] === 0x52 && b[1] === 0x49 && b[2] === 0x46 && b[3] === 0x46 &&
    b[8] === 0x57 && b[9] === 0x45 && b[10] === 0x42 && b[11] === 0x50,
};

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    const caminho = url.pathname;

    if (caminho === '/i/novo' && request.method === 'POST') {
      return enviar(request, env, url);
    }

    const id = casarId(caminho);
    if (id) {
      if (request.method === 'GET' || request.method === 'HEAD') {
        return servir(id, request, env, ctx);
      }
      if (request.method === 'DELETE') {
        return excluir(id, request, env, url);
      }
      return erro(405, 'Método não permitido para este endereço.');
    }

    return erro(404, 'Endereço não existe neste serviço.');
  },
};

function casarId(caminho) {
  const m = caminho.match(/^\/i\/([A-Za-z0-9_-]+)$/);
  if (!m || m[1].length !== TAMANHO_DO_ID) {
    return null;
  }
  return m[1];
}

// ---------------------------------------------------------------- envio

async function enviar(request, env, url) {
  // O Worker guarda o DIGESTO da chave, nunca a chave. Isso é o que permite a
  // configuração viver num repositório público: SHA-256 de um valor aleatório de
  // 256 bits não tem preimagem alcançável por força bruta.
  if (!env.HASH_DA_CHAVE) {
    return erro(503, 'Serviço sem chave configurada.');
  }

  const chave = request.headers.get('x-chave') || '';
  if (!chave || !igualEmTempoConstante(await digerir(chave), env.HASH_DA_CHAVE)) {
    return erro(401, 'Chave do aplicativo inválida ou ausente.');
  }

  // O limite por endereço NÃO é feito aqui: é uma regra de limite do WAF, na
  // borda, que barra antes de o Worker sequer executar. Ver nuvem/LEIAME.md.
  // A tentativa anterior usava o binding de rate limit dos Workers e foi
  // removida por não ter disparado em teste nenhum — defesa que não se prova
  // funcionando é pior que nenhuma, porque dá a impressão de que existe.

  const tipo = (request.headers.get('content-type') || '').split(';')[0].trim().toLowerCase();
  const validar = TIPOS_ACEITOS[tipo];
  if (!validar) {
    return erro(415, 'Só entram imagens PNG, JPEG ou WebP.');
  }

  // Content-Length é dica, não garantia: o corpo é medido depois de lido.
  const anunciado = Number(request.headers.get('content-length') || 0);
  if (anunciado > TAMANHO_MAXIMO) {
    return erro(413, `A imagem passa do limite de ${TAMANHO_MAXIMO / 1024 / 1024} MB.`);
  }

  const corpo = await request.arrayBuffer();
  if (corpo.byteLength === 0) {
    return erro(400, 'Corpo vazio.');
  }
  if (corpo.byteLength > TAMANHO_MAXIMO) {
    return erro(413, `A imagem passa do limite de ${TAMANHO_MAXIMO / 1024 / 1024} MB.`);
  }

  const bytes = new Uint8Array(corpo);
  if (bytes.length < 12 || !validar(bytes)) {
    return erro(415, 'Os bytes do arquivo não são de uma imagem do tipo declarado.');
  }

  const id = aleatorio(BYTES_DO_ID);
  const token = aleatorio(16);
  const expiraEm = Math.floor(Date.now() / 1000) + TTL_SEGUNDOS;

  try {
    await env.IMAGENS.put(`i:${id}`, corpo, {
      expirationTtl: TTL_SEGUNDOS,
      metadata: {
        ct: tipo,
        tam: corpo.byteLength,
        exp: expiraEm,
        // O token nunca fica guardado em claro: se alguém ler o KV, ainda não
        // consegue apagar as imagens dos outros.
        dt: await digerir(token),
      },
    });
  } catch (falha) {
    // O plano gratuito do KV dá 1.000 gravações por dia para o serviço INTEIRO.
    // Estourar isso é o desfecho plausível de um dia movimentado, e o programa
    // precisa dizer o que houve em vez de mostrar um erro de rede genérico.
    console.log('falha ao gravar no KV:', String(falha));
    return erro(503, 'O serviço atingiu o limite diário de envios. Tente de novo amanhã, ou aponte o Print Dev para um destino seu nas configurações.');
  }

  return json(201, {
    url: `${url.origin}/i/${id}`,
    id,
    token,
    expiraEm,
    ttlSegundos: TTL_SEGUNDOS,
  });
}

// ---------------------------------------------------------------- entrega

async function servir(id, request, env, ctx) {
  const cache = caches.default;
  const emCache = await cache.match(request);
  if (emCache) {
    return emCache;
  }

  const { value, metadata } = await env.IMAGENS.getWithMetadata(`i:${id}`, { type: 'arrayBuffer' });
  if (!value) {
    return erro(404, 'Esta imagem expirou ou foi excluída.');
  }

  const resposta = new Response(value, {
    headers: {
      'content-type': metadata?.ct || 'application/octet-stream',
      'content-length': String(value.byteLength),
      'cache-control': `public, max-age=${CACHE_SEGUNDOS}`,
      // Link temporário não pode virar resultado de busca permanente.
      'x-robots-tag': 'noindex, nofollow, noarchive',
      'content-security-policy': "default-src 'none'; sandbox",
      'x-content-type-options': 'nosniff',
      'content-disposition': 'inline',
      'expira-em': String(metadata?.exp ?? ''),
    },
  });

  ctx.waitUntil(cache.put(request, resposta.clone()));
  return resposta;
}

// ---------------------------------------------------------------- exclusão

async function excluir(id, request, env, url) {
  const token = request.headers.get('x-token') || '';
  if (!token) {
    return erro(401, 'Falta o token de exclusão.');
  }

  const { value, metadata } = await env.IMAGENS.getWithMetadata(`i:${id}`, { type: 'stream' });
  if (!value) {
    // Já não existe: o resultado desejado é o mesmo. Tratar como falha faria o
    // botão "Excluir agora" reclamar de um trabalho que já estava feito.
    return json(200, { excluida: true, jaNaoExistia: true });
  }

  if (!metadata?.dt || !igualEmTempoConstante(await digerir(token), metadata.dt)) {
    return erro(403, 'Token de exclusão não confere.');
  }

  await env.IMAGENS.delete(`i:${id}`);
  await caches.default.delete(`${url.origin}/i/${id}`);

  return json(200, { excluida: true });
}

// ---------------------------------------------------------------- utilidades

/** Identificador em base64url, sem preenchimento — seguro em URL sem escapar nada. */
function aleatorio(bytes) {
  const b = crypto.getRandomValues(new Uint8Array(bytes));
  return btoa(String.fromCharCode(...b)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

async function digerir(texto) {
  const hash = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(texto));
  return [...new Uint8Array(hash)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

/**
 * Comparação sem saída antecipada. O token é secreto: comparar com === deixaria
 * o tempo de resposta contar quantos caracteres iniciais acertaram.
 */
function igualEmTempoConstante(a, b) {
  if (a.length !== b.length) {
    return false;
  }
  let diferenca = 0;
  for (let i = 0; i < a.length; i++) {
    diferenca |= a.charCodeAt(i) ^ b.charCodeAt(i);
  }
  return diferenca === 0;
}

function json(status, corpo) {
  return new Response(JSON.stringify(corpo), {
    status,
    headers: { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' },
  });
}

function erro(status, mensagem) {
  return json(status, { erro: mensagem });
}
