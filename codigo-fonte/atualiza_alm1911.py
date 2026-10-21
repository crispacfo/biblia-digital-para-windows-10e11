"""Atualiza a ortografia da Almeida 1911 (dominio publico) para a grafia atual, so a grafia.

Uso:
  python -I atualiza_alm1911.py analisar <biblias.txt> <palavras_ptbr.txt> <pasta_saida>
  python -I atualiza_alm1911.py aplicar  <biblias.txt> <pasta_saida>

analisar: para cada palavra diferente da Almeida 1911 decide a forma atual e grava em <pasta_saida>:
  mapa.tsv        antiga<TAB>nova<TAB>ocorrencias<TAB>como   (como = dicionario | nome | revisar | manter)
  revisar.tsv     palavras sem solucao segura, para decisao humana (preencha a coluna "nova")
aplicar: le mapa.tsv (e revisar.tsv preenchido) e acrescenta ao biblias.txt a versao
  pt-alm1911a "Almeida 1911 (ortografia atualizada)", mantendo a original intacta.

Regras (grafia de 1911 -> atual): ph>f, th>t, y>i, ch(r/l)>c, consoantes dobradas (ll mm nn tt pp ff cc gg)>simples,
ct>t, pt>t, mn>n, gn>n, sc inicial>c, ae>ai, e inicial>i (egreja). Cada combinacao gerada so e aceita se existir
no dicionario pt_BR (VERO/LibreOffice), comparando sem acentos; o acento vem do dicionario.
Nomes proprios: aceita a forma que a regra produz quando ela aparece no mesmo versiculo da Biblia Livre ou da VEB.
Vocabulario e gramatica de 1911 nao mudam (vos, porem, cousa...): palavra que ja existe no dicionario fica como esta.
"""
import io, os, re, sys, unicodedata, collections, itertools

def tira_acento(s):
    return "".join(c for c in unicodedata.normalize("NFD", s) if unicodedata.category(c) != "Mn")

def ler_versoes(caminho, chaves):
    textos = {k: {} for k in chaves}
    atual = None
    for linha in io.open(caminho, encoding="utf-8"):
        if linha.startswith("@V"):
            k = linha.split("\t")[1]
            atual = k if k in chaves else None
            continue
        if atual and linha[:1].isdigit():
            b, c, v, t = linha.rstrip("\n").split("\t", 3)
            textos[atual][(int(b), int(c), int(v))] = prepara(t) if atual == "pt-alm1911" else t
    return textos

PALAVRA = re.compile(r"[^\W\d_]+(?:-[^\W\d_]+)*")

# Elisoes da grafia de 1911: d'elle, n'aquelle, lh'o... Junta pronomes e demonstrativos (deles, naquele, lho);
# antes de nomes e das demais palavras separa (d'Israel -> de Israel, d'um -> de um, n'Israel -> em Israel).
JUNTA_D = {"elle", "elles", "ella", "ellas", "este", "estes", "esta", "estas", "isto", "esse", "esses", "essa", "essas", "isso",
           "aquelle", "aquelles", "aquella", "aquellas", "aquillo", "ali", "alli", "aqui", "ahi", "entre", "onde", "ante", "antes",
           "outro", "outros", "outra", "outras", "outrem"}
JUNTA_N = {"elle", "elles", "ella", "ellas", "este", "estes", "esta", "estas", "isto", "esse", "esses", "essa", "essas", "isso",
           "aquelle", "aquelles", "aquella", "aquellas", "aquillo", "um", "uma", "uns", "umas", "outro", "outros", "outra", "outras"}

def _elisao(m):
    x, y = m.group(1), m.group(2)
    xl, yl = x.lower(), y.lower()
    if xl in ("lh", "m", "t") or (xl == "n" and yl in ("o", "a", "os", "as")):
        return x + y
    if xl == "d":
        return x + y if yl in JUNTA_D else x + "e " + y
    if xl == "n":
        return x + y if yl in JUNTA_N else ("E" if x.isupper() else "e") + "m " + y
    return m.group(0)

ACENTO = {"a": "á", "e": "ê", "i": "í", "o": "ô"}

def _encl(m):
    # matal-o -> matá-lo; possuil-a -> possuí-la; fel-o -> fê-lo; nol-o -> no-lo; eil-o -> ei-lo
    base, pron = m.group(1), m.group(2)
    if base.lower() not in ("no", "vo", "ei") and base[-1:].lower() in ACENTO:
        a = ACENTO[base[-1].lower()]
        base = base[:-1] + (a.upper() if base[-1].isupper() else a)
    return base + "-l" + pron

MESO = {"hei": "ei", "ha": "á", "hão": "ão", "has": "ás", "hemos": "emos", "heis": "eis", "hia": "ia", "hiam": "iam", "hias": "ias", "hiamos": "íamos"}

def prepara(texto):
    texto = re.sub(r"\b([^\W\d_]{1,2})'([^\W\d_]+)", _elisao, texto)
    texto = re.sub(r"\b([^\W\d_]+?)l-(o|a|os|as)\b", _encl, texto)
    texto = re.sub(r"-h(ei|a|ão|as|emos|eis|ia|iam|ias|iamos)\b", lambda m: "-" + MESO["h" + m.group(1)], texto)
    return texto

REGRAS = [
    ("ph", "f"), ("th", "t"), ("y", "i"), ("chr", "cr"), ("chl", "cl"),
    ("ll", "l"), ("mm", "m"), ("nn", "n"), ("tt", "t"), ("pp", "p"), ("ff", "f"), ("cc", "c"), ("gg", "g"), ("dd", "d"), ("bb", "b"),
    ("ct", "t"), ("pt", "t"), ("mn", "n"), ("gn", "n"), ("ae", "ai"), ("mpç", "nç"),
    ("cç", "ç"), ("sch", "sc"), ("ch", "c"), ("ch", "qu"), ("eem", "em"), ("ahi", "ai"), ("hi", "i"),
    ("mt", "nt"), ("mv", "nv"), ("ms", "ns"), ("mq", "nq"), ("md", "nd"), ("eos", "eus"), ("eo$", "eu"),
    ("z$", "s"), ("iu$", "io"), ("^h", ""), ("su", "ssu"),
    ("rh", "r"), ("k", "qu"), ("k", "c"), ("mc", "nc"), ("eh", "e"), ("ge", "je"), ("nço", "nso"), ("nça", "nsa"),
    ("z", "s"), ("s", "z"), ("xt", "st"), ("crea", "cria"), ("creou", "criou"), ("ze", "zes"),
    ("ps", "s"), ("mpt", "nt"), ("ue$", "ui"), ("oe$", "ói"), ("ahe", "ai"), ("xh", "x"), ("sh", "s"), ("gm", "m"),
    ("oh", "o"), ("eia", "ea"), ("s", "ss"), ("bema", "bem-a"), ("crei", "criei"), ("éa", "eia"),
    ("poz", "pus"), ("soceg", "sosseg"), ("cubi", "cobi"), ("peio", "pio"), ("cree", "crie"), ("ea$", "eia"),
    ("eas$", "eias"), ("oes$", "óis"), ("pç", "ç"), ("mph", "nf"),
]
# Regras que trocam uma letra por outra em todas as posicoes de uma vez dao formas erradas (visinhos > vizinhoz);
# estas tambem sao tentadas uma ocorrencia por vez.
UMA_POR_VEZ = {("s", "z"), ("z", "s"), ("s", "ss")}

# Formas que as regras nao alcancam (morfologia antiga ou casos isolados), decididas uma a uma.
ESPECIAIS = {
    "quasi": "quase", "tribu": "tribo", "tribus": "tribos", "teem": "têm", "veem": "veem", "deem": "deem",
    "pozeram": "puseram", "pozeste": "puseste", "pozestes": "pusestes", "pozera": "pusera", "pozer": "puser",
    "poderam": "puderam", "cincoenta": "cinquenta", "ephod": "éfode", "eis-que": "eis que",
    "similhante": "semelhante", "similhantes": "semelhantes", "similhança": "semelhança", "similhantemente": "semelhantemente",
    "dezeseis": "dezesseis", "dezesete": "dezessete", "balac": "balaque", "fallaz": "falaz", "amen": "amém",
    "adultéra": "adúltera", "adultéras": "adúlteras", "á": "à", "ás": "às", "ha": "há", "gloria": "glória", "faca": "faca", "forca": "forca", "áquelle": "àquele", "áquella": "àquela", "áquelles": "àqueles", "áquellas": "àquelas", "áquillo": "àquilo", "podesse": "pudesse", "podessem": "pudessem", "poude": "pôde", "peior": "pior", "hieis": "íeis", "podera": "pudera", "poderas": "puderas",
    "escarneo": "escárnio", "arroido": "arruído", "candeia": "candeia",
}

# Nomes proprios sem correspondencia: so as regras de grafia (sem dicionario).
NOME_REGRAS = [("ph", "f"), ("th", "t"), ("rh", "r"), ("y", "i"), ("ll", "l"), ("mm", "m"), ("nn", "n"), ("tt", "t"),
               ("pp", "p"), ("ff", "f"), ("cc", "c"), ("gg", "g"), ("dd", "d"), ("bb", "b"), ("ss", "s")]

ARRISCADAS = {("z", "s"), ("s", "z"), ("^h", ""), ("k", "qu"), ("k", "c"), ("^e", "i"), ("ze", "zes")}

def candidatos(w, seguro=False):
    """Todas as combinacoes de regras aplicadas (cada regra em todas as ocorrencias), ordem: menos mudancas primeiro."""
    aplicaveis = [r for r in REGRAS if (r[0][1:] if r[0].startswith("^") else r[0].rstrip("$")) in w]
    extras = []
    if w.startswith("sc"): extras.append(("^sc", "c"))
    if w.startswith("e"): extras.append(("^e", "i"))
    if seguro:
        aplicaveis = [r for r in aplicaveis if r not in ARRISCADAS]
        extras = [r for r in extras if r not in ARRISCADAS]
    todas = aplicaveis + extras
    vistos, res = set(), []
    for n in range(0, len(todas) + 1):
        for combo in itertools.combinations(todas, n):
            x = w
            for a, b in combo:
                if a == "^sc": x = b + x[2:]
                elif a.startswith("^"): x = b + x[len(a) - 1:] if x.startswith(a[1:]) else x
                elif a.endswith("$"): x = x[: -(len(a) - 1)] + b if x.endswith(a[:-1]) else x
                elif (a, b) in UMA_POR_VEZ: x = x  # tratadas abaixo, uma posicao por vez
                else: x = x.replace(a, b)
            variantes = [x]
            for a, b in combo:
                if (a, b) in UMA_POR_VEZ:
                    novas = []
                    for y in variantes:
                        for i in [i for i in range(len(y)) if y.startswith(a, i)]:
                            novas.append(y[:i] + b + y[i + len(a):])
                    variantes = novas or variantes
            for x in variantes:
                if x not in vistos:
                    vistos.add(x); res.append((n, x))
        if len(res) > 400: break
    return res

def caixa(modelo, nova):
    if modelo.isupper() and len(modelo) > 1: return nova.upper()
    if modelo[:1].isupper(): return nova[:1].upper() + nova[1:]
    return nova

# Mais-que-perfeito: em 1911 o futuro levava acento (fallará, sahirá) e o mais-que-perfeito nao (fallara, sahira,
# apparecêra). Forma antiga em -ra sem acento agudo final nunca vira futuro em -rá (fallara > falara, nao falará).
def mais_que_perfeito(w, e, como):
    if como.startswith("nome") or w.endswith("rá") or not w.endswith("ra") or not e.endswith("rá"):
        return e
    e = e[:-2] + "ra"
    return re.sub(r"([aeou])ira$", r"\1íra", e)  # saíra, caíra, destruíra, traíra

def analisar(biblias, lista, saida):
    os.makedirs(saida, exist_ok=True)
    print("lendo dicionario..."); sys.stdout.flush()
    dic = set()
    sem_acento = collections.defaultdict(set)
    for linha in io.open(lista, encoding="utf-8"):
        w = linha.strip()
        if w:
            dic.add(w); sem_acento[tira_acento(w)].add(w)
    txt = ler_versoes(biblias, ["pt-alm1911", "pt-blivre", "pt-veb"])
    alm = txt["pt-alm1911"]
    freq = collections.Counter(); maiusc = collections.Counter(); ondes = collections.defaultdict(list)
    for ref, t in alm.items():
        for m in PALAVRA.finditer(t):
            w = m.group(0)
            freq[w.lower()] += 1
            if w[:1].isupper() and m.start() > 0 and t[m.start() - 2:m.start()].strip() not in (".", "?", "!", ":"): maiusc[w.lower()] += 1
            if len(ondes[w.lower()]) < 40: ondes[w.lower()].append(ref)
    # nomes das outras versoes portuguesas por versiculo (sem acento -> forma)
    nomes_ref = {}
    for k in ("pt-blivre", "pt-veb"):
        for ref, t in txt[k].items():
            d = nomes_ref.setdefault(ref, {})
            for m in PALAVRA.finditer(t):
                w = m.group(0)
                if w[:1].isupper(): d.setdefault(tira_acento(w.lower()), w)
    moderno = collections.Counter()
    for k in ("pt-blivre", "pt-veb"):
        for t2 in txt[k].values():
            for m in PALAVRA.finditer(t2): moderno[m.group(0).lower()] += 1
    mapa, revisar = [], []
    for w, n in sorted(freq.items(), key=lambda x: -x[1]):
        nome = maiusc[w] >= max(1, n // 2)
        if w in ESPECIAIS:
            mapa.append((w, ESPECIAIS[w], n, "especial")); continue
        mq = re.match(r"^(.*)([áéí])(ra|ras|ram|ramos|reis)$", w)
        if mq and not nome and "-" not in w:
            # mais-que-perfeito de 1911 (podéra, deixára): so perde o acento; nao e o futuro (poderá)
            base = mq.group(1) + tira_acento(mq.group(2)) + mq.group(3)
            for a2, b2 in [("ll", "l"), ("mm", "m"), ("nn", "n"), ("tt", "t"), ("pp", "p"), ("ff", "f"), ("cc", "c"), ("ph", "f"), ("th", "t"), ("y", "i"), ("ct", "t"), ("pt", "t")]:
                base = base.replace(a2, b2)
            base = base.replace("crea", "cria")
            mapa.append((w, base, n, "mais-que-perfeito")); continue
        if "-" in w and w not in dic:
            # palavra composta: atualiza cada parte (poz-se -> pôs-se, bemaventurado tratado como palavra simples)
            partes = w.split("-")
            novas = []
            for pt in partes:
                if pt in ESPECIAIS: novas.append(ESPECIAIS[pt]); continue
                if pt in dic or pt in ("o", "a", "os", "as", "lhe", "lhes", "se", "me", "te", "nos", "vos"): novas.append(pt); continue
                fs = sorted({f for _, c in candidatos(pt) for f in sem_acento.get(tira_acento(c), ())},
                            key=lambda f: (-moderno[f], f != tira_acento(f), f))  # empate: sem acento, depois ordem fixa
                novas.append(fs[0] if fs else pt)
            if novas != partes:
                mapa.append((w, "-".join(novas), n, "dicionario")); continue
            if all(pt in dic or pt in ("o", "a", "os", "as", "lhe", "lhes", "se", "me", "te", "nos", "vos", "lho", "lha", "lhos", "lhas", "mo", "ma", "to", "ta")
                   for pt in partes):
                mapa.append((w, w, n, "manter")); continue
        if not nome and (w in dic or w.replace("-", "") in dic):
            # Grafia antiga que coincide com palavra atual (logar, paes): se nunca aparece nos textos atuais
            # e uma forma atualizada aparece, vai para revisao com sugestao.
            variantes = [f for f in sem_acento.get(tira_acento(w), ()) if f != w]
            # Forma antiga quase ausente nos textos atuais (0, ou ate 2 erros de digitacao) e sem acento proprio;
            # nao vale para finais em -ra (vira/virá, morrera/morrerá: depende do contexto).
            rara = moderno[w] == 0 or (tira_acento(w) == w and moderno[w] <= 2 and
                   any(moderno[f] >= max(10, 20 * moderno[w]) and not (w.endswith("ra") and f.endswith("rá")) for f in variantes))
            if rara and n >= 2:
                # inclui a propria palavra com outro acento (principio > princípio, espirito > espírito)
                seguros = [c for _, c in candidatos(w, seguro=True)]
                sug = sorted({f for c in seguros for f in sem_acento.get(tira_acento(c), ()) if moderno[f] > moderno[w] and f != w},
                             key=lambda f: (-moderno[f], f))
                if sug:
                    mapa.append((w, sug[0], n, "coincide:" + "|".join(sug[:3])))
                    continue
            mapa.append((w, w, n, "manter")); continue
        escolha, como = None, None
        if nome:
            votos = collections.Counter()
            for ref in ondes[w]:
                d = nomes_ref.get(ref, {})
                for _, c in candidatos(w):
                    f = d.get(tira_acento(c))
                    if f: votos[f.lower()] += 1; break
            if votos:
                escolha, como = votos.most_common(1)[0][0], "nome"
            else:
                # Sem forma exata: o nome parecido (mesma inicial, semelhanca >= 0,6) que aparece no mesmo versiculo
                # da Biblia Livre ou da VEB, aceito se for o mais votado em pelo menos 60% das ocorrencias examinadas.
                import difflib
                alvo = tira_acento(w)
                for ref in ondes[w]:
                    d = nomes_ref.get(ref, {})
                    melhor, nota = None, 0.0
                    for k2, forma in d.items():
                        if not k2 or k2[0] != alvo[0]: continue
                        r = difflib.SequenceMatcher(None, alvo, k2).ratio()
                        if r > nota: melhor, nota = forma, r
                    if melhor and nota >= 0.6: votos[melhor.lower()] += 1
                if votos:
                    f, c = votos.most_common(1)[0]
                    if c >= 2 and c >= 0.6 * len(ondes[w]): escolha, como = f, "nome-parecido"
        if escolha is None and nome:
            x = w
            for a, b in NOME_REGRAS: x = x.replace(a, b)
            escolha, como = x, ("nome-regra" if x != w else "manter")
        if escolha is None and not nome:
            # Todas as formas do dicionario que as regras alcancam; escolhe a de menos mudancas e, entre elas,
            # a mais usada na Biblia Livre/VEB. Sem uso nos textos atuais e com mais de uma forma: revisao.
            achados = {}
            for k, c in candidatos(w):
                for f in sem_acento.get(tira_acento(c), ()):
                    if f not in achados or k < achados[f]: achados[f] = k
            if achados:
                ordem = sorted(achados, key=lambda f: (moderno[f] == 0, achados[f], -moderno[f], f))
                if moderno[ordem[0]] > 0 or len(ordem) == 1:
                    escolha, como = ordem[0], "dicionario"
                else:
                    revisar.append((w, "|".join(ordem[:4]), n, "varias formas", " ".join("%d:%d:%d" % r for r in ondes[w][:3])))
            elif nome:
                x = w
                for a, b in NOME_REGRAS: x = x.replace(a, b)
                escolha, como = x, ("nome-regra" if x != w else "manter")
        if escolha is not None:
            mapa.append((w, escolha, n, como))
        elif not any(r[0] == w for r in revisar):
            revisar.append((w, "", n, "nao encontrada", " ".join("%d:%d:%d" % r for r in ondes[w][:3])))
    mapa = [(w, mais_que_perfeito(w, e, como), n, como) for w, e, n, como in mapa]
    with io.open(os.path.join(saida, "mapa.tsv"), "w", encoding="utf-8") as f:
        f.write("antiga\tnova\tocorrencias\tcomo\n")
        for r in mapa: f.write("%s\t%s\t%d\t%s\n" % r)
    with io.open(os.path.join(saida, "revisar.tsv"), "w", encoding="utf-8") as f:
        f.write("antiga\tsugestoes\tocorrencias\tmotivo\texemplos\tnova\n")
        for r in revisar: f.write("%s\t%s\t%d\t%s\t%s\t\n" % r)
    muda = [r for r in mapa if r[0] != r[1]]
    print("palavras diferentes:", len(freq))
    print("mantidas:", sum(1 for r in mapa if r[0] == r[1]), "| atualizadas:", len(muda),
          "(%d ocorrencias)" % sum(r[2] for r in muda), "| por nome:", sum(1 for r in muda if r[3] == "nome"))
    print("para revisar:", len(revisar), "(%d ocorrencias)" % sum(r[2] for r in revisar))

def aplicar(biblias, saida):
    troca = {}
    for linha in io.open(os.path.join(saida, "mapa.tsv"), encoding="utf-8").read().splitlines()[1:]:
        a, n, _, _ = linha.split("\t")
        if a != n: troca[a] = n
    rev = os.path.join(saida, "revisar.tsv")
    if os.path.exists(rev):
        for linha in io.open(rev, encoding="utf-8").read().splitlines()[1:]:
            c = linha.split("\t")
            if len(c) >= 6 and c[5].strip(): troca[c[0]] = c[5].strip()
    linhas = io.open(biblias, encoding="utf-8").read().splitlines()
    # tira uma versao atualizada anterior, se houver
    saida_l, pula = [], False
    for l in linhas:
        if l.startswith("@V"): pula = l.split("\t")[1] == "pt-alm1911a"
        if not pula: saida_l.append(l)
    bloco, dentro = [], False
    for l in saida_l:
        if l.startswith("@V"):
            dentro = l.split("\t")[1] == "pt-alm1911"
            if dentro: bloco.append("@V\tpt-alm1911a\tALM1911A\tpt-BR\tPortuguês — Almeida 1911 (ortografia atualizada)"); continue
        if not dentro: continue
        if l[:1].isdigit():
            b, c, v, t = l.split("\t", 3)
            t = prepara(t)
            t = re.sub(r"(?<!\bos )(?<!\bas )(?<!\bse )(?<!\bte )(?<!\bnos )(?<!\bvos )(?<!\bo )(?<!\ba )(?<!\bme )(?<!\blhe )(?<!\bpara )\b([Pp])orem\b",
                       lambda m: m.group(1) + "orém", t)
            t = PALAVRA.sub(lambda m: caixa(m.group(0), troca.get(m.group(0).lower(), m.group(0).lower())) if m.group(0).lower() in troca else m.group(0), t)
            bloco.append("\t".join((b, c, v, t)))
        else:
            bloco.append(l)
    # a versao atualizada entra logo depois da original
    res, i = [], 0
    while i < len(saida_l):
        res.append(saida_l[i])
        if saida_l[i].startswith("@V") and saida_l[i].split("\t")[1] == "pt-alm1911":
            j = i + 1
            while j < len(saida_l) and not saida_l[j].startswith("@V"): res.append(saida_l[j]); j += 1
            res.extend(bloco); i = j; continue
        i += 1
    io.open(biblias, "w", encoding="utf-8", newline="\n").write("\n".join(res) + "\n")
    print("versao pt-alm1911a gravada:", sum(1 for l in bloco if l[:1].isdigit()), "versiculos;", len(troca), "palavras trocadas")

if __name__ == "__main__":
    if sys.argv[1] == "analisar": analisar(sys.argv[2], sys.argv[3], sys.argv[4])
    else: aplicar(sys.argv[2], sys.argv[3])
