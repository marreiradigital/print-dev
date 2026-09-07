#!/usr/bin/env bash
# Garante UTF-8 com BOM em todo .cs e .xaml, como manda o .editorconfig.
#
# Por que existe: a interface do Print Dev e toda em portugues acentuado. Sem o BOM,
# uma ferramenta da cadeia pode ler o arquivo como ANSI e corromper o acento em
# silencio - o defeito so aparece depois, na tela do usuario.
#
# Uso:  bash scripts/garantir-bom.sh          (corrige)
#       bash scripts/garantir-bom.sh --check  (so lista, sai 1 se faltar)
set -euo pipefail

raiz="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
somente_verificar=false
[[ "${1:-}" == "--check" ]] && somente_verificar=true

faltando=0
while IFS= read -r -d '' arquivo; do
  if [[ "$(head -c 3 "$arquivo" | od -An -tx1 | tr -d ' \n')" == "efbbbf" ]]; then
    continue
  fi

  faltando=$((faltando + 1))
  if $somente_verificar; then
    echo "sem BOM: ${arquivo#"$raiz/"}"
  else
    printf '\xEF\xBB\xBF' | cat - "$arquivo" > "$arquivo.bom.tmp"
    mv "$arquivo.bom.tmp" "$arquivo"
    echo "BOM adicionado: ${arquivo#"$raiz/"}"
  fi
done < <(find "$raiz/src" "$raiz/tests" \
           \( -name obj -o -name bin \) -prune -o \
           \( -name '*.cs' -o -name '*.xaml' \) -print0)

if [[ $faltando -eq 0 ]]; then
  echo "Todos os .cs e .xaml estao em UTF-8 com BOM."
elif $somente_verificar; then
  exit 1
fi
