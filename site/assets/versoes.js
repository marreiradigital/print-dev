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
