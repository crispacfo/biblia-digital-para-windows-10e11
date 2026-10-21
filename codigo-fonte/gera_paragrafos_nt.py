"""Acrescenta ao paragrafos.txt os inicios de paragrafo do NT, tirados do SBL Greek New Testament (SBLGNT).

Uso: python -I gera_paragrafos_nt.py <pasta_xml_sblgnt> <paragrafos.txt> <biblias.txt>

- Fonte: XML oficial em github.com/LogosBible/SBLGNT (data/sblgnt/xml), elemento <p> de cada livro.
- Licenca: CC BY 4.0, (c) 2010 Society of Biblical Literature e Logos Bible Software. Usamos so a
  divisao em paragrafos (por referencia), aplicada ao nosso texto; o texto grego do SBLGNT nao entra.
- Um paragrafo que no SBLGNT comeca no meio de um versiculo passa a comecar no proximo versiculo
  (nao partimos versiculos). Inicios em versiculos que nao existem na VEB sao descartados e listados.
- Linhas do NT gravadas com a marca G (grego SBLGNT); as do AT (P/S, do WLC) ficam como estao.
"""
import io, os, re, sys

LIVROS = {
    "Matt": 40, "Mark": 41, "Luke": 42, "John": 43, "Acts": 44, "Rom": 45, "1Cor": 46, "2Cor": 47,
    "Gal": 48, "Eph": 49, "Phil": 50, "Col": 51, "1Thess": 52, "2Thess": 53, "1Tim": 54, "2Tim": 55,
    "Titus": 56, "Phlm": 57, "Heb": 58, "Jas": 59, "1Pet": 60, "2Pet": 61, "1John": 62, "2John": 63,
    "3John": 64, "Jude": 65, "Rev": 66,
}

pasta, saida, biblias = sys.argv[1], sys.argv[2], sys.argv[3]

# Versiculos existentes na VEB (para nao apontar para versiculo inexistente).
existe = set()
na_veb = False
for line in io.open(biblias, encoding="utf-8"):
    if line.startswith("@V"):
        na_veb = line.split("\t")[1] == "pt-veb"
        continue
    if na_veb and line[:1].isdigit():
        c = line.split("\t", 3)
        existe.add((int(c[0]), int(c[1]), int(c[2])))

novos, meio, descartados = [], [], []
for nome, num in LIVROS.items():
    xml = io.open(os.path.join(pasta, nome + ".xml"), encoding="utf-8-sig").read()
    for par in xml.split("<p>")[1:]:
        par = par.split("</p>")[0]
        m = re.search(r'<verse-number id="[^"]* (\d+):(\d+)"', par)
        if not m:
            continue
        cap, ver = int(m.group(1)), int(m.group(2))
        if not par.lstrip().startswith("<verse-number"):
            meio.append("%d %d:%d" % (num, cap, ver))  # comecava antes, no versiculo anterior
        if (num, cap, ver) in existe:
            novos.append((num, cap, ver))
        else:
            descartados.append("%d %d:%d" % (num, cap, ver))

linhas = [l.rstrip("\r\n") for l in io.open(saida, encoding="utf-8")]
cabeca = [l for l in linhas if l.startswith("#") and "SBLGNT" not in l]
at = [l for l in linhas if l and not l.startswith("#") and int(l.split("\t")[0]) < 40]
cabeca.append("# NT (marca G): inicios de paragrafo do SBL Greek New Testament (SBLGNT), (c) 2010 Society of Biblical "
              "Literature e Logos Bible Software, licenca CC BY 4.0 - so a divisao em paragrafos, aplicada por "
              "referencia. Gerado por ferramentas/gera_paragrafos_nt.py.")
nt = ["%d\t%d\t%d\tG" % t for t in sorted(set(novos))]
io.open(saida, "w", encoding="utf-8", newline="\n").write("\n".join(cabeca + at + nt) + "\n")
print("NT: %d inicios de paragrafo em 27 livros" % len(set(novos)))
print("comecavam no meio do versiculo (movidos para o inicio do versiculo seguinte): %d -> %s" % (len(meio), ", ".join(meio)))
print("descartados (versiculo inexistente na VEB): %d -> %s" % (len(descartados), ", ".join(descartados)))
