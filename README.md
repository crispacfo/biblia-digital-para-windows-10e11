# Bíblia Digital para Windows 10 e 11

Leitura, busca e estudo da Bíblia no computador, **sem internet, sem instalação e sem custo**.
Programa do **Estudo Bíblico** — <http://estudobiblico.org>

> **Quer só usar o programa?** Baixe o `BibliaDigital-1.9.2-Windows.zip` na página
> [Releases](https://github.com/crispacfo/biblia-digital-para-windows-10e11/releases) e leia o [LEIA-ME.txt](LEIA-ME.txt).

## O que faz

- **Sete versões em quatro idiomas:** VEB — Versão Estudo Bíblico, Bíblia Livre, Almeida 1911 e Almeida 1911
  com ortografia atualizada (português),
  King James (inglês), Reina-Valera 1909 (espanhol) e Louis Segond 1910 (francês).
- **Importação de outras versões** em texto, CSV ou JSON (botão *Versões...*).
- **Busca instantânea:** exata ou flexível (sinônimos e dicionário analógico), E/OU, por testamento ou livro,
  sem se importar com acentos e maiúsculas.
- **Quatro modos de leitura:** versículo por versículo, texto corrido, parágrafos e **livro aberto** (duas páginas,
  como uma Bíblia impressa). Parágrafos também no Novo Testamento (SBLGNT), números de versículo elevados,
  texto justificado, entrelinha e largura de coluna ajustáveis, seletor de livro/capítulo/versículo no título.
  Fonte, tamanho e temas de cor ajustáveis; várias passagens em abas.
- **Estudos:** marca-texto, anotações, favoritos, histórico, "continuar de onde parei" e cópia com referência.
- Totalmente offline: não usa a internet e não coleta dados.

Requisitos: Windows 10 ou 11 (o .NET Framework 4 já vem no Windows). Não roda nativamente em macOS ou Linux.

## Compilar a partir do código

Não precisa de Visual Studio nem de SDK — usa o compilador C# que já vem no Windows.

```bat
codigo-fonte\compilar.bat
```

O `BibliaDigital.exe` é gerado na raiz do repositório, ao lado da pasta `data`, e já pode ser aberto.
Autoteste: `BibliaDigital.exe /teste` (relatório em `%LOCALAPPDATA%\BibliaDigital\teste.txt`).

Detalhes técnicos (organização do código, formato dos dados, autoteste, antivírus) em
[LEIA-ME-DESENVOLVEDOR.txt](LEIA-ME-DESENVOLVEDOR.txt). Novidades de cada versão em [HISTORICO.txt](HISTORICO.txt).

## Estrutura

| Caminho | Conteúdo |
|---|---|
| `codigo-fonte/` | `BibliaDigital.cs` (programa inteiro, C# / Windows Forms), manifesto, ícone e scripts de compilação; `gera_paragrafos_nt.py` (parágrafos do NT a partir do SBLGNT) e `atualiza_alm1911.py` (Almeida 1911 com ortografia atualizada) |
| `data/` | textos bíblicos (`biblias.txt`), marcas de parágrafo, dicionário analógico e sinônimos |
| `LEIA-ME.txt` | instruções para o usuário (vai junto no pacote Windows) |
| `LEIA-ME-DESENVOLVEDOR.txt` | documentação técnica |
| `HISTORICO.txt` | histórico de versões |
| `LICENCA.txt` | propriedade, distribuição e direitos dos textos |

## Licença e direitos

Bíblia Digital © 2026 Estudo Bíblico. **Distribuição livre e gratuita autorizada.**

- Almeida 1911, King James Version, Reina-Valera 1909 e Louis Segond 1910: domínio público.
- Almeida 1911 (ortografia atualizada): o texto de 1911 só com a grafia atual, feita pelo Estudo Bíblico; domínio público.
- Bíblia Livre: © Diego Santos, Mário Sérgio e Marco Teles, licença
  [Creative Commons Atribuição 3.0 Brasil](http://creativecommons.org/licenses/by/3.0/br/).
  Nesta edição foram corrigidos erros de digitação e de acentuação, sem mudar a tradução.
- VEB — Versão Estudo Bíblico: pertence ao Estudo Bíblico. Antigo Testamento traduzido do Códice de Leningrado
  (Open Scriptures Hebrew Bible, CC BY 4.0); Novo Testamento traduzido do Textus Receptus (Scrivener 1894).
- Parágrafos do Novo Testamento: SBL Greek New Testament (SBLGNT), © 2010 Society of Biblical Literature e
  Logos Bible Software, [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).
- Versões importadas pelo usuário seguem os direitos de seus próprios autores.

Veja [LICENCA.txt](LICENCA.txt).
