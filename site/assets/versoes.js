/* =========================================================================
   Print Dev — sistema de versões
   Lê as releases do GitHub e monta os botões de download.

   Duas fontes, nessa ordem:
     1. a API do GitHub, sempre atual;
     2. assets/releases.json, um retrato gravado a cada publicação.

   A segunda existe porque a API do GitHub sem autenticação permite 60
   chamadas por hora POR IP — e um IP compartilhado (empresa, faculdade,
   operadora móvel) estoura isso sem o visitante ter culpa nenhuma. Sem o
   retrato, o botão de baixar simplesmente sumiria para essa pessoa.
   ========================================================================= */

export const REPO = "marreiradigital/print-dev";
export const URL_RELEASES = `https://github.com/${REPO}/releases`;

/** Busca a lista de versões publicadas, da fonte que responder primeiro. */
export async function buscarVersoes() {
  try {
    const resposta = await fetch(`https://api.github.com/repos/${REPO}/releases?per_page=30`, {
      headers: { Accept: "application/vnd.github+json" },
    });
    if (resposta.ok) {
      const dados = await resposta.json();
      if (Array.isArray(dados) && dados.length) return dados;
    }
  } catch {
    /* rede fora, bloqueio de CORS, cota estourada: cai para o retrato */
  }

  try {
    const resposta = await fetch("assets/releases.json", { cache: "no-cache" });
    if (resposta.ok) {
      const dados = await resposta.json();
      if (Array.isArray(dados) && dados.length) return dados;
    }
  } catch {
    /* sem retrato ainda (primeira publicação) */
  }

  return null;
}

/** Só o que interessa a quem visita: publicado, não rascunho, não prévia. */
export function publicadas(versoes) {
  return (versoes ?? []).filter((v) => !v.draft && !v.prerelease);
}

/** O instalador .exe de uma release, se houver. */
export function instalador(versao) {
  return (versao.assets ?? []).find((a) => a.name?.toLowerCase().endsWith(".exe")) ?? null;
}

/** "6,6 MB" — separador decimal em vírgula, como manda o português. */
export function tamanhoLegivel(bytes) {
  if (!bytes && bytes !== 0) return "";
  const mb = bytes / (1024 * 1024);
  return `${mb.toFixed(1).replace(".", ",")} MB`;
}

/** "7 de setembro de 2026" */
export function dataLegivel(iso) {
  if (!iso) return "";
  try {
    return new Date(iso).toLocaleDateString("pt-BR", {
      day: "numeric",
      month: "long",
      year: "numeric",
    });
  } catch {
    return "";
  }
}

/** O nome da versão, sem o "v" que o Git põe na etiqueta. */
export function numero(versao) {
  return (versao.tag_name ?? versao.name ?? "").replace(/^v/i, "");
}

/* -------------------------------------------------------------------------
   Markdown das notas de versão

   Um subconjunto pequeno de propósito: título, parágrafo, lista, tabela,
   negrito, código e link — que é tudo que nota de release usa. Sem biblioteca:
   trazer um parser inteiro (e o sanitizador que ele exige) para formatar meia
   dúzia de parágrafos seria caro demais para o que entrega.

   Nada aqui usa innerHTML. Cada pedaço vira nó de texto ou elemento de uma
   lista fechada, então marcação hostil no corpo de uma release não tem por
   onde virar HTML.
   ------------------------------------------------------------------------- */

const NEGRITO = /\*\*([^*]+)\*\*/;
const CODIGO = /`([^`]+)`/;
const LINK = /\[([^\]]+)\]\(([^)\s]+)\)/;

/** Aplica negrito, código e link dentro de uma linha, devolvendo nós. */
function inline(texto) {
  const nos = [];
  let resto = texto;

  while (resto) {
    const achados = [
      { m: NEGRITO.exec(resto), tipo: "b" },
      { m: CODIGO.exec(resto), tipo: "code" },
      { m: LINK.exec(resto), tipo: "a" },
    ].filter((a) => a.m);

    if (!achados.length) {
      nos.push(document.createTextNode(resto));
      break;
    }

    const primeiro = achados.reduce((a, b) => (a.m.index <= b.m.index ? a : b));
    const { m, tipo } = primeiro;

    if (m.index > 0) nos.push(document.createTextNode(resto.slice(0, m.index)));

    const el = document.createElement(tipo);
    // Negrito e link podem conter outra marcação (`código` dentro de **negrito**
    // é comum). Código não: o que está entre crases é literal, por definição.
    // A recursão termina porque o miolo é sempre menor que o trecho casado.
    if (tipo === "code") el.textContent = m[1];
    else el.append(...inline(m[1]));

    if (tipo === "a") {
      // Só http(s): um esquema como javascript: nunca vira href aqui.
      el.href = /^https?:\/\//i.test(m[2]) ? m[2] : "#";
      el.rel = "noopener";
    }
    nos.push(el);

    resto = resto.slice(m.index + m[0].length);
  }

  return nos;
}

function celulas(linha) {
  return linha.replace(/^\||\|$/g, "").split("|").map((c) => c.trim());
}

const SEPARADOR_DE_TABELA = /^\|?[\s:-]+\|[\s:|-]*$/;

/** Monta as notas de uma release dentro de um contêiner. */
export function renderizarNotas(texto, destino) {
  const linhas = (texto ?? "").replace(/\r\n/g, "\n").split("\n");
  let i = 0;

  const anexar = (tag, conteudo, classe) => {
    const el = document.createElement(tag);
    if (classe) el.className = classe;
    if (conteudo) el.append(...conteudo);
    destino.append(el);
    return el;
  };

  while (i < linhas.length) {
    const linha = linhas[i];

    if (!linha.trim() || /^-{3,}$/.test(linha.trim())) { i++; continue; }

    const titulo = /^(#{2,4})\s+(.*)$/.exec(linha);
    if (titulo) {
      anexar(titulo[1].length === 2 ? "h3" : "h4", inline(titulo[2]));
      i++;
      continue;
    }

    if (linha.trim().startsWith("|")) {
      const bloco = [];
      while (i < linhas.length && linhas[i].trim().startsWith("|")) bloco.push(linhas[i++]);

      const tabela = document.createElement("table");
      const corpo = document.createElement("tbody");
      let primeira = true;

      for (const l of bloco) {
        if (SEPARADOR_DE_TABELA.test(l.trim())) { primeira = false; continue; }
        const tr = document.createElement("tr");
        for (const c of celulas(l)) {
          const td = document.createElement(primeira ? "th" : "td");
          td.append(...inline(c));
          tr.append(td);
        }
        // Cabeçalho vazio (tabela sem títulos) não vira linha em branco na tela.
        if (tr.textContent.trim()) corpo.append(tr);
      }

      tabela.append(corpo);
      const rolagem = document.createElement("div");
      rolagem.className = "rolagem-lateral";
      rolagem.append(tabela);
      destino.append(rolagem);
      continue;
    }

    if (/^\s*[-*]\s+/.test(linha)) {
      const ul = document.createElement("ul");
      while (i < linhas.length && /^\s*[-*]\s+/.test(linhas[i])) {
        const li = document.createElement("li");
        li.append(...inline(linhas[i].replace(/^\s*[-*]\s+/, "")));
        ul.append(li);
        i++;
      }
      destino.append(ul);
      continue;
    }

    const paragrafo = [];
    while (i < linhas.length && linhas[i].trim() && !/^[|#]|^\s*[-*]\s/.test(linhas[i])) {
      paragrafo.push(linhas[i++]);
    }
    anexar("p", inline(paragrafo.join(" ")));
  }
}
