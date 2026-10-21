// Biblia Digital Windows 11 - versao 1.9.2
// Programa nativo (.exe) em C# / Windows Forms, sem PowerShell, sem navegador e sem Python.
// Dados offline em data\biblias.txt, data\dicionario_analogico.csv e data\sinonimos.csv.
// Compilar: compilar.bat (usa o compilador C# que ja vem com o .NET Framework 4 do Windows).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Metadados completos: programas sem empresa/descricao/copyright pesam contra na
// heuristica do Windows Defender e do SmartScreen.
[assembly: System.Reflection.AssemblyTitle("Biblia Digital")]
[assembly: System.Reflection.AssemblyDescription("Biblia Digital - leitura e busca offline da Biblia")]
[assembly: System.Reflection.AssemblyCompany("Estudo Bíblico")]
[assembly: System.Reflection.AssemblyProduct("Biblia Digital Windows 11")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 Estudo Bíblico - estudobiblico.org")]
[assembly: System.Reflection.AssemblyTrademark("")]
[assembly: System.Reflection.AssemblyCulture("")]
[assembly: System.Reflection.AssemblyVersion("1.9.2.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.9.2.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("1.9.2")]
[assembly: System.Runtime.InteropServices.ComVisible(false)]
[assembly: System.Runtime.InteropServices.Guid("6b3f2c1e-9a4d-4e8b-b7c2-1f5d8a0e3c47")]

namespace BibliaDigital
{
    // Formatacao de paragrafo do RichEdit (justificar, recuo da primeira linha, entrelinha, espaco antes),
    // que o RichTextBox do .NET nao expoe.
    static class ParaFormat
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct PARAFORMAT2
        {
            public int cbSize; public uint dwMask; public short wNumbering; public short wEffects;
            public int dxStartIndent; public int dxRightIndent; public int dxOffset;
            public short wAlignment; public short cTabCount;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 32)] public int[] rgxTabs;
            public int dySpaceBefore; public int dySpaceAfter; public int dyLineSpacing;
            public short sStyle; public byte bLineSpacingRule; public byte bOutlineLevel;
            public short wShadingWeight; public short wShadingStyle; public short wNumberingStart; public short wNumberingStyle;
            public short wNumberingTab; public short wBorderSpace; public short wBorderWidth; public short wBorders;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref PARAFORMAT2 lParam);

        const int EM_SETPARAFORMAT = 0x447;
        const uint PFM_STARTINDENT = 0x1, PFM_OFFSET = 0x4, PFM_ALIGNMENT = 0x8, PFM_SPACEBEFORE = 0x40, PFM_SPACEAFTER = 0x80, PFM_LINESPACING = 0x100;
        public const short Left = 1, Justify = 4;

        // Aplica aos paragrafos que tocam [start, start+length). Medidas em twips (1/1440 pol.).
        // lineRule20: entrelinha em vintavos de linha (20 = simples, 23 = 1,15, 26 = 1,3, 30 = 1,5).
        public static void Apply(RichTextBox rtb, int start, int length, short align, int firstIndent, int offset, int spaceBefore, int lineRule20)
        {
            if (length < 0) return;
            IntPtr handle = rtb.Handle; // cria a janela do controle, se ainda nao existir
            PARAFORMAT2 pf = new PARAFORMAT2();
            pf.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(PARAFORMAT2));
            pf.rgxTabs = new int[32];
            pf.dwMask = PFM_ALIGNMENT | PFM_STARTINDENT | PFM_OFFSET | PFM_SPACEBEFORE | PFM_SPACEAFTER | PFM_LINESPACING;
            pf.wAlignment = align;
            pf.dxStartIndent = firstIndent; pf.dxOffset = offset;
            pf.dySpaceBefore = spaceBefore; pf.dySpaceAfter = 0;
            pf.bLineSpacingRule = 5; pf.dyLineSpacing = Math.Max(20, lineRule20);
            rtb.Select(start, length);
            SendMessage(rtb.Handle, EM_SETPARAFORMAT, IntPtr.Zero, ref pf);
        }
    }

    class Book
    {
        public int Id;
        public string Name;
        public string Code = "";  // abreviacao do arquivo de dados (gn, ex, 1sm...)
        public string Testament;
        public int Chapters;
    }

    class Verse
    {
        public int B, C, V;
        public string T;         // texto original
        public string N;         // texto normalizado (minusculo, sem acentos)
        public string Testament;
    }

    class BibleVersion
    {
        public string Key;
        public string Label;
        public string Abbr;      // sigla curta (BLIVRE, KJV...) usada nas abas
        public string Lang = "";
        public string UserFile;  // versao importada pelo usuario: arquivo em %LOCALAPPDATA%\BibliaDigital\versoes
        public List<Book> Books = new List<Book>();
        public Dictionary<int, Book> BooksById = new Dictionary<int, Book>();
        public Dictionary<string, Book> BooksByName = new Dictionary<string, Book>();
        public List<Verse> Verses = new List<Verse>();
        public Dictionary<long, List<Verse>> Chapters = new Dictionary<long, List<Verse>>();

        public static long ChapterKey(int book, int chapter) { return (long)book * 1000 + chapter; }

        public List<Verse> GetChapter(int book, int chapter)
        {
            List<Verse> list;
            if (Chapters.TryGetValue(ChapterKey(book, chapter), out list)) return list;
            return new List<Verse>();
        }
    }

    static class TextUtil
    {
        // Minusculo e sem acentos. Usa uma tabela por caractere (calculada uma vez) em vez de
        // normalizar cada texto inteiro: o resultado e o mesmo e o carregamento fica muito mais rapido.
        static readonly string[] charMap = new string[65536];

        static string SlowNorm(string text)
        {
            string d = text.Normalize(NormalizationForm.FormD);
            StringBuilder sb = new StringBuilder(d.Length);
            foreach (char ch in d)
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
            return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        }

        static string MapChar(char ch)
        {
            string m = charMap[ch];
            if (m == null)
            {
                m = char.IsWhiteSpace(ch) || char.IsSurrogate(ch) ? ch.ToString() : SlowNorm(ch.ToString());
                charMap[ch] = m;
            }
            return m;
        }

        public static string Norm(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            char[] buf = new char[text.Length];
            int n = 0;
            for (int i = 0; i < text.Length; i++)
            {
                string m = MapChar(text[i]);
                if (m.Length == 1) buf[n++] = m[0];
                else if (m.Length > 1)
                {
                    // Raro: caractere que vira mais de um (ou nenhum) caractere.
                    StringBuilder sb = new StringBuilder(text.Length + 8);
                    sb.Append(buf, 0, n);
                    for (int j = i; j < text.Length; j++) sb.Append(MapChar(text[j]));
                    return sb.ToString();
                }
            }
            return new string(buf, 0, n);
        }
    }

    static class Search
    {
        static bool IsWordChar(char c) { return char.IsLetterOrDigit(c); }

        // Procura a frase como palavra(s) inteira(s): "amor" acha "amor" e "amor," mas nao "amorreus".
        public static bool ContainsWhole(string text, string phrase)
        {
            if (phrase.Length == 0) return false;
            int i = text.IndexOf(phrase, StringComparison.Ordinal);
            while (i >= 0)
            {
                int end = i + phrase.Length;
                bool okStart = i == 0 || !IsWordChar(text[i - 1]);
                bool okEnd = end >= text.Length || !IsWordChar(text[end]);
                if (okStart && okEnd) return true;
                i = text.IndexOf(phrase, i + 1, StringComparison.Ordinal);
            }
            return false;
        }
    }

    // Inicios de paragrafo, so para exibicao: AT pelas marcas pe/samekh do Codice de Leningrado;
    // NT pela divisao do SBL Greek New Testament (SBLGNT, CC BY 4.0), aplicada por referencia ao nosso texto.
    // Livros sem dados (ex.: versoes importadas com outra numeracao) ficam sem quebras.
    static class Paragraphs
    {
        static readonly HashSet<int> starts = new HashSet<int>();
        static readonly HashSet<int> books = new HashSet<int>();
        public static int Count { get { return starts.Count; } }

        public static void Load(string path)
        {
            starts.Clear(); books.Clear();
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] p = line.Split('\t');
                int b, c, v;
                if (p.Length < 3 || !int.TryParse(p[0], out b) || !int.TryParse(p[1], out c) || !int.TryParse(p[2], out v)) continue;
                starts.Add(b * 1000000 + c * 1000 + v);
                books.Add(b);
            }
        }

        public static bool HasBook(int book) { return books.Contains(book); }
        public static bool Starts(int book, int chapter, int verse) { return starts.Contains(book * 1000000 + chapter * 1000 + verse); }
    }

    static class DataLoader
    {
        static int ToInt(object o) { return Convert.ToInt32(o, CultureInfo.InvariantCulture); }
        static string ToStr(object o) { return o == null ? "" : Convert.ToString(o, CultureInfo.InvariantCulture); }

        public static string LastTimings = "";

        public static List<BibleVersion> LoadBible(string path)
        {
            if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return LoadText(path);
            return LoadJson(path);
        }

        // Formato rapido (data\biblias.txt), UTF-8, campos separados por TAB:
        //   @V  chave  sigla  idioma  nome                 -> comeca uma versao
        //   @L  id  codigo  AT|NT  capitulos  nome         -> livro da versao atual
        //   livro  capitulo  versiculo  texto              -> versiculo da versao atual
        // Linhas vazias ou iniciadas por # sao ignoradas. Cada versao e lida em paralelo.
        static List<BibleVersion> LoadText(string path)
        {
            Stopwatch sw = Stopwatch.StartNew();
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            double readSeconds = sw.Elapsed.TotalSeconds;

            List<int> starts = new List<int>();
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].StartsWith("@V\t", StringComparison.Ordinal)) starts.Add(i);
            BibleVersion[] result = new BibleVersion[starts.Count];
            System.Threading.Tasks.Parallel.For(0, starts.Count, k =>
            {
                int end = k + 1 < starts.Count ? starts[k + 1] : lines.Length;
                result[k] = ParseVersion(lines, starts[k], end);
            });
            LastTimings = "leitura do arquivo " + readSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s, processamento " +
                (sw.Elapsed.TotalSeconds - readSeconds).ToString("0.00", CultureInfo.InvariantCulture) + " s";
            return result.ToList();
        }

        // problems == null: arquivo do programa, qualquer erro interrompe (dado corrompido).
        // problems != null: arquivo do usuario, a linha com defeito e anotada e ignorada.
        public static BibleVersion ParseVersion(string[] lines, int start, int end, List<string> problems = null, string fileName = "biblias.txt")
        {
            string[] h = lines[start].Split('\t');
            BibleVersion ver = new BibleVersion();
            ver.Key = h.Length > 1 ? h[1].Trim() : "";
            ver.Abbr = h.Length > 2 && h[2].Trim().Length > 0 ? h[2].Trim() : ver.Key;
            ver.Lang = h.Length > 3 ? h[3].Trim() : "";
            ver.Label = h.Length > 4 && h[4].Trim().Length > 0 ? h[4].Trim() : ver.Key;
            ver.Verses.Capacity = end - start;
            for (int i = start + 1; i < end; i++)
            {
                string line = lines[i];
                if (line.Length == 0 || line[0] == '#') continue;
                try
                {
                    if (line.StartsWith("@L\t", StringComparison.Ordinal))
                    {
                        string[] p = line.Split(new[] { '\t' }, 6);
                        Book book = new Book();
                        book.Id = int.Parse(p[1], CultureInfo.InvariantCulture);
                        book.Code = p[2];
                        book.Testament = p[3];
                        book.Chapters = int.Parse(p[4], CultureInfo.InvariantCulture);
                        book.Name = p[5];
                        ver.Books.Add(book);
                        ver.BooksById[book.Id] = book;
                        ver.BooksByName[book.Name] = book;
                        continue;
                    }
                    string[] f = line.Split(new[] { '\t' }, 4);
                    Verse verse = new Verse();
                    verse.B = int.Parse(f[0], CultureInfo.InvariantCulture);
                    verse.C = int.Parse(f[1], CultureInfo.InvariantCulture);
                    verse.V = int.Parse(f[2], CultureInfo.InvariantCulture);
                    verse.T = f[3];
                    AddVerse(ver, verse);
                }
                catch (Exception ex)
                {
                    if (problems == null) throw new InvalidDataException("Linha " + (i + 1) + " de " + fileName + " esta com formato invalido: " + line, ex);
                    problems.Add("linha " + (i + 1) + ": formato invalido: " + Clip(line));
                }
            }
            foreach (List<Verse> list in ver.Chapters.Values) list.Sort((a, b) => a.V.CompareTo(b.V));
            return ver;
        }

        public static string Clip(string s) { return s.Length > 90 ? s.Substring(0, 90) + "..." : s; }

        // Versoes importadas pelo usuario (um .txt por versao, no mesmo formato de data\biblias.txt).
        // Um arquivo com defeito nao impede o programa de abrir: so aquela versao fica de fora, com aviso.
        public static List<BibleVersion> LoadUserVersions(string dir, List<BibleVersion> existing, List<string> warnings)
        {
            List<BibleVersion> result = new List<BibleVersion>();
            if (!Directory.Exists(dir)) return result;
            foreach (string file in Directory.GetFiles(dir, "*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                    int start = Array.FindIndex(lines, l => l.StartsWith("@V\t", StringComparison.Ordinal));
                    if (start < 0) { warnings.Add(Path.GetFileName(file) + ": nao tem a linha @V da versao."); continue; }
                    List<string> problems = new List<string>();
                    BibleVersion ver = ParseVersion(lines, start, lines.Length, problems, Path.GetFileName(file));
                    if (ver.Key.Length == 0 || ver.Verses.Count == 0) { warnings.Add(Path.GetFileName(file) + ": nenhum versiculo valido."); continue; }
                    if (existing.Concat(result).Any(x => x.Key.Equals(ver.Key, StringComparison.OrdinalIgnoreCase)))
                    { warnings.Add(Path.GetFileName(file) + ": a chave '" + ver.Key + "' ja existe; versao ignorada."); continue; }
                    ver.UserFile = file;
                    result.Add(ver);
                }
                catch (Exception ex) { warnings.Add(Path.GetFileName(file) + ": " + ex.Message); }
            }
            return result;
        }

        public static void AddVerse(BibleVersion ver, Verse verse)
        {
            verse.N = TextUtil.Norm(verse.T);
            Book bk;
            verse.Testament = ver.BooksById.TryGetValue(verse.B, out bk) ? bk.Testament : "";
            ver.Verses.Add(verse);
            long key = BibleVersion.ChapterKey(verse.B, verse.C);
            List<Verse> list;
            if (!ver.Chapters.TryGetValue(key, out list)) { list = new List<Verse>(); ver.Chapters[key] = list; }
            list.Add(verse);
        }

        // Formato antigo (bible_data.json), mantido como alternativa.
        static List<BibleVersion> LoadJson(string path)
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            ser.RecursionLimit = 100;
            Dictionary<string, object> root = (Dictionary<string, object>)ser.DeserializeObject(json);
            Dictionary<string, object> versions = (Dictionary<string, object>)root["versions"];

            List<BibleVersion> result = new List<BibleVersion>();
            foreach (KeyValuePair<string, object> kv in versions)
            {
                Dictionary<string, object> vObj = (Dictionary<string, object>)kv.Value;
                BibleVersion ver = new BibleVersion();
                ver.Key = kv.Key;
                ver.Label = vObj.ContainsKey("label") ? ToStr(vObj["label"]) : kv.Key;
                ver.Abbr = vObj.ContainsKey("abbr") ? ToStr(vObj["abbr"]) : kv.Key;

                foreach (object bo in (object[])vObj["books"])
                {
                    Dictionary<string, object> b = (Dictionary<string, object>)bo;
                    Book book = new Book();
                    book.Id = ToInt(b["id"]);
                    book.Name = ToStr(b["name"]);
                    book.Testament = ToStr(b["testament"]);
                    book.Chapters = ToInt(b["chapters"]);
                    ver.Books.Add(book);
                    ver.BooksById[book.Id] = book;
                    ver.BooksByName[book.Name] = book;
                }

                foreach (object vo in (object[])vObj["verses"])
                {
                    Dictionary<string, object> v = (Dictionary<string, object>)vo;
                    Verse verse = new Verse();
                    verse.B = ToInt(v["b"]);
                    verse.C = ToInt(v["c"]);
                    verse.V = ToInt(v["v"]);
                    verse.T = ToStr(v["t"]);
                    AddVerse(ver, verse);
                }
                foreach (List<Verse> list in ver.Chapters.Values) list.Sort((a, b) => a.V.CompareTo(b.V));
                result.Add(ver);
            }
            return result;
        }

        public static Dictionary<string, List<string>> LoadDictionary(string path)
        {
            Dictionary<string, List<string>> dict = new Dictionary<string, List<string>>();
            if (!File.Exists(path)) return dict;
            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    int comma = line.IndexOf(',');
                    if (comma <= 0) continue;
                    string key = TextUtil.Norm(line.Substring(0, comma).Trim().Trim('"'));
                    if (key.Length == 0) continue;
                    List<string> rel = line.Substring(comma + 1).Trim().Trim('"')
                        .Split('|').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                    dict[key] = rel;
                }
            }
            catch { }
            return dict;
        }

        // Sinonimos valem nos dois sentidos: se "alegria" tem "gozo", buscar "gozo" sugere "alegria"
        // (e os outros sinonimos da mesma linha). As sugestoes proprias de cada termo vem primeiro.
        public static Dictionary<string, List<string>> LoadSynonyms(string path)
        {
            Dictionary<string, List<string>> direct = LoadDictionary(path);
            Dictionary<string, List<string>> dict = new Dictionary<string, List<string>>();
            Action<string, string> addTo = (key, word) =>
            {
                List<string> list;
                if (!dict.TryGetValue(key, out list)) { list = new List<string>(); dict[key] = list; }
                if (TextUtil.Norm(word) != key && !list.Contains(word)) list.Add(word);
            };
            foreach (KeyValuePair<string, List<string>> kv in direct)
                foreach (string w in kv.Value) addTo(kv.Key, w);
            foreach (KeyValuePair<string, List<string>> kv in direct)
            {
                string head = HeadWord(path, kv.Key);
                foreach (string w in kv.Value)
                {
                    string wk = TextUtil.Norm(w);
                    addTo(wk, head);
                    foreach (string other in kv.Value) addTo(wk, other);
                }
            }
            return dict;
        }

        // Recupera a grafia original (com acentos) do termo da primeira coluna.
        static Dictionary<string, Dictionary<string, string>> headCache = new Dictionary<string, Dictionary<string, string>>();
        static string HeadWord(string path, string key)
        {
            Dictionary<string, string> map;
            if (!headCache.TryGetValue(path, out map))
            {
                map = new Dictionary<string, string>();
                try
                {
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8).Skip(1))
                    {
                        int comma = line.IndexOf(',');
                        if (comma <= 0) continue;
                        string head = line.Substring(0, comma).Trim().Trim('"');
                        map[TextUtil.Norm(head)] = head;
                    }
                }
                catch { }
                headCache[path] = map;
            }
            string found;
            return map.TryGetValue(key, out found) ? found : key;
        }
    }

    // ---- Importacao de versoes pelo usuario ----
    // Formatos aceitos:
    //  - o proprio formato do programa (@V / @L / livro TAB capitulo TAB versiculo TAB texto);
    //  - JSON: {"versions":{...}} (Biblia Digital 1.3), uma versao {"verses":[...]}, lista de versiculos
    //    [{"book":..,"chapter":..,"verse":..,"text":..}] ou lista de livros [{"abbrev":"gn","chapters":[["v1","v2"],...]}];
    //  - texto em colunas livro;capitulo;versiculo;texto (separador ; , TAB ou |), com ou sem coluna de id na frente;
    //  - linhas no estilo "Joao 3:16 Porque Deus amou o mundo...".
    // O livro pode vir como numero (1 a 66), nome ou abreviacao (portugues, ingles, espanhol ou frances).
    class ImportResult
    {
        public string Format = "";
        public List<BibleVersion> Versions = new List<BibleVersion>();
        public List<string> Problems = new List<string>();
        public int ProblemCount;
        public void Problem(string s) { ProblemCount++; if (Problems.Count < 300) Problems.Add(s); }
    }

    static class VersionImport
    {
        class RawVerse { public string Book; public int C, V; public string T, Where; }

        // Tabela de nomes/abreviacoes -> numero do livro. Chave ambigua (ex.: "jo" = Jo ou Joao) vale -1.
        class BookMap
        {
            readonly Dictionary<string, int> exact = new Dictionary<string, int>(), loose = new Dictionary<string, int>();
            static void Put(Dictionary<string, int> d, string k, int id)
            {
                if (k.Length == 0) return;
                int old;
                if (d.TryGetValue(k, out old) && old != id) d[k] = -1; else d[k] = id;
            }
            public void Add(string name, int id)
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                Put(exact, name.Trim().ToLowerInvariant(), id);
                Put(loose, Loose(name), id);
            }
            public int Find(string s)
            {
                int id;
                if (exact.TryGetValue(s.Trim().ToLowerInvariant(), out id) && id > 0) return id;
                return loose.TryGetValue(Loose(s), out id) ? id : 0;
            }
        }

        // "1 Samuel", "1Sm", "I Samuel", "1.º Samuel" -> "1samuel"/"1sm"
        static string Loose(string s)
        {
            string n = TextUtil.Norm(s.Trim()).Replace("º", "").Replace("ª", "").Replace("°", "");
            n = System.Text.RegularExpressions.Regex.Replace(n, @"^(iii|ii|i)(?=[\s.])", m => m.Value.Length.ToString());
            StringBuilder sb = new StringBuilder();
            foreach (char c in n) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"^([123])o(?=[a-z])", "$1");
        }

        static BookMap MapOf(BibleVersion v)
        {
            BookMap m = new BookMap();
            foreach (Book b in v.Books) { m.Add(b.Name, b.Id); m.Add(b.Code, b.Id); }
            return m;
        }

        static bool IsInt(string s) { int n; return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n); }

        // Escolhe a versao cuja nomenclatura reconhece mais livros do arquivo (resolve "jo", "jn" etc.
        // conforme o idioma); o que sobrar tenta na tabela de todas as versoes.
        static Dictionary<string, int> ResolveBooks(IEnumerable<string> tokens, List<BibleVersion> known, out BibleVersion naming)
        {
            Dictionary<string, int> result = new Dictionary<string, int>();
            List<string> names = new List<string>();
            foreach (string t in tokens)
            {
                if (IsInt(t)) result[t] = int.Parse(t.Trim(), CultureInfo.InvariantCulture);
                else names.Add(t);
            }
            naming = known.FirstOrDefault();
            if (names.Count == 0) return result;
            BookMap best = null, all = new BookMap();
            int bestHits = -1;
            foreach (BibleVersion k in known)
            {
                BookMap m = MapOf(k);
                int hits = names.Count(n => m.Find(n) > 0);
                if (hits > bestHits) { bestHits = hits; best = m; naming = k; }
                foreach (Book b in k.Books) { all.Add(b.Name, b.Id); all.Add(b.Code, b.Id); }
            }
            // Nomes usuais que nao aparecem nas versoes do programa.
            string[,] extra = { { "Cantares", "22" }, { "Canticos", "22" }, { "Canticos dos Canticos", "22" }, { "Cantico dos Canticos", "22" },
                { "Salmo", "19" }, { "Atos dos Apostolos", "44" }, { "Song of Songs", "22" }, { "Psalm", "19" }, { "Revelation", "66" } };
            for (int i = 0; i < extra.GetLength(0); i++) all.Add(extra[i, 0], int.Parse(extra[i, 1]));
            foreach (string n in names)
            {
                int id = best != null ? best.Find(n) : 0;
                if (id <= 0) id = all.Find(n);
                if (id > 0) result[n] = id;
            }
            return result;
        }

        // Le o arquivo como UTF-8; se nao for UTF-8 valido, como ANSI do Windows (arquivos antigos em portugues).
        public static string ReadText(string path, out string encoding)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) { encoding = "UTF-16"; return Encoding.Unicode.GetString(b, 2, b.Length - 2); }
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) { encoding = "UTF-16"; return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2); }
            try { encoding = "UTF-8"; return new UTF8Encoding(false, true).GetString(b).TrimStart('﻿'); }
            catch (DecoderFallbackException) { encoding = "ANSI (Windows-1252)"; return Encoding.GetEncoding(1252).GetString(b); }
        }

        public static ImportResult Parse(string path, List<BibleVersion> known)
        {
            ImportResult r = new ImportResult();
            string enc, text = ReadText(path, out enc);
            string name = Path.GetFileNameWithoutExtension(path);
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            string head = text.TrimStart();
            if (lines.Any(l => l.StartsWith("@V\t", StringComparison.Ordinal)))
            {
                r.Format = "Biblia Digital (texto)";
                List<int> starts = new List<int>();
                for (int i = 0; i < lines.Length; i++) if (lines[i].StartsWith("@V\t", StringComparison.Ordinal)) starts.Add(i);
                for (int k = 0; k < starts.Count; k++)
                {
                    List<string> problems = new List<string>();
                    BibleVersion v = DataLoader.ParseVersion(lines, starts[k], k + 1 < starts.Count ? starts[k + 1] : lines.Length, problems, Path.GetFileName(path));
                    foreach (string p in problems) r.Problem(p);
                    r.Versions.Add(v);
                }
            }
            else if (head.StartsWith("{") || head.StartsWith("["))
            {
                r.Format = "JSON";
                ParseJson(text, name, known, r);
            }
            else
            {
                r.Format = "texto em colunas";
                ParseColumns(lines, name, known, r);
            }
            r.Format += ", " + enc;
            foreach (BibleVersion v in r.Versions) Complete(v, known, r);
            r.Versions.RemoveAll(v => v.Verses.Count == 0);
            return r;
        }

        static string Unquote(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"') s = s.Substring(1, s.Length - 2).Replace("\"\"", "\"");
            return s.Trim();
        }

        // Divide respeitando aspas ("texto; com separador") e junta o resto na ultima coluna.
        static List<string> SplitColumns(string line, char sep, int max)
        {
            List<string> cols = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') { quoted = !quoted; cur.Append(c); }
                else if (c == sep && !quoted && cols.Count < max - 1) { cols.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
            cols.Add(cur.ToString());
            return cols;
        }

        static bool TryColumns(string line, char sep, out RawVerse raw)
        {
            raw = null;
            List<string> c = SplitColumns(line, sep, 5);
            int ch, v;
            // id;livro;capitulo;versiculo;texto (ex.: 1001001,1,1,1,"No principio...")
            if (c.Count == 5 && IsInt(c[0]) && int.TryParse(Unquote(c[2]), out ch) && int.TryParse(Unquote(c[3]), out v) && Unquote(c[1]).Length > 0 && !IsInt(c[4]))
            { raw = new RawVerse { Book = Unquote(c[1]), C = ch, V = v, T = Unquote(c[4]) }; return true; }
            c = SplitColumns(line, sep, 4);
            if (c.Count == 4 && int.TryParse(Unquote(c[1]), out ch) && int.TryParse(Unquote(c[2]), out v) && Unquote(c[0]).Length > 0)
            { raw = new RawVerse { Book = Unquote(c[0]), C = ch, V = v, T = Unquote(c[3]) }; return true; }
            return false;
        }

        static readonly System.Text.RegularExpressions.Regex RefLine =
            new System.Text.RegularExpressions.Regex(@"^\s*((?:[123]\s*)?[^\d\s:][^\d:]*?)\s*(\d+)\s*[:.,]\s*(\d+)\s+(.+)$");

        static void ParseColumns(string[] lines, string name, List<BibleVersion> known, ImportResult r)
        {
            List<string> data = lines.Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("#")).Take(60).ToList();
            char sep = '\0';
            int best = 0;
            foreach (char s in new[] { '\t', ';', '|', ',' })
            {
                RawVerse tmp;
                int ok = data.Count(l => TryColumns(l, s, out tmp));
                if (ok > best) { best = ok; sep = s; }
            }
            if (sep != '\0') r.Format += " (separador " + (sep == '\t' ? "TAB" : sep.ToString()) + ")";
            else r.Format = "texto com referencias (Livro cap:vers texto)";
            List<RawVerse> raws = new List<RawVerse>();
            bool first = true;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                RawVerse raw = null;
                bool ok = false;
                if (sep != '\0') ok = TryColumns(line, sep, out raw);
                if (!ok)
                {
                    System.Text.RegularExpressions.Match m = RefLine.Match(line);
                    if (m.Success) { raw = new RawVerse { Book = m.Groups[1].Value.Trim(), C = int.Parse(m.Groups[2].Value), V = int.Parse(m.Groups[3].Value), T = m.Groups[4].Value }; ok = true; }
                }
                if (ok) { raw.Where = "linha " + (i + 1); raws.Add(raw); }
                else if (!first) r.Problem("linha " + (i + 1) + ": nao reconhecida: " + DataLoader.Clip(line));
                first = false; // a primeira linha pode ser o cabecalho (livro;capitulo;versiculo;texto)
            }
            r.Versions.Add(Build(raws, known, r, name, null, null, ""));
        }

        // ---- JSON ----
        static object Get(Dictionary<string, object> d, params string[] keys)
        {
            foreach (string k in keys)
                foreach (KeyValuePair<string, object> kv in d)
                    if (string.Equals(kv.Key, k, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }
        static string Str(Dictionary<string, object> d, params string[] keys)
        {
            object o = Get(d, keys);
            return o == null ? "" : Convert.ToString(o, CultureInfo.InvariantCulture).Trim();
        }
        static int Int(object o)
        {
            if (o == null) return 0;
            int n;
            return int.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        static void ParseJson(string text, string name, List<BibleVersion> known, ImportResult r)
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue; ser.RecursionLimit = 200;
            object root;
            try { root = ser.DeserializeObject(text); }
            catch (Exception ex) { r.Problem("JSON invalido: " + ex.Message); return; }
            Dictionary<string, object> d = root as Dictionary<string, object>;
            Dictionary<string, object> many = d != null ? Get(d, "versions", "versoes") as Dictionary<string, object> : null;
            if (many != null)
            {
                foreach (KeyValuePair<string, object> kv in many)
                {
                    Dictionary<string, object> vd = kv.Value as Dictionary<string, object>;
                    if (vd != null) JsonVersion(vd, kv.Key, known, r);
                }
            }
            else if (d != null) JsonVersion(d, name, known, r);
            else if (root is object[]) JsonVersion(new Dictionary<string, object> { { "verses", root } }, name, known, r);
            else r.Problem("estrutura JSON nao reconhecida");
        }

        static void JsonVersion(Dictionary<string, object> d, string key, List<BibleVersion> known, ImportResult r)
        {
            List<RawVerse> raws = new List<RawVerse>();
            List<Book> declared = new List<Book>();
            object[] books = Get(d, "books", "livros") as object[];
            object[] verses = Get(d, "verses", "versiculos") as object[];
            // Lista de livros com os capitulos dentro: [{"abbrev":"gn","chapters":[["v1","v2"],...]}]
            object[] nested = verses != null && verses.Length > 0 && verses[0] is Dictionary<string, object> &&
                Get((Dictionary<string, object>)verses[0], "chapters", "capitulos") is object[] ? verses : null;
            if (nested == null && books != null && books.Length > 0 && books[0] is Dictionary<string, object> &&
                Get((Dictionary<string, object>)books[0], "chapters", "capitulos") is object[]) nested = books;
            if (nested != null)
            {
                for (int bi = 0; bi < nested.Length; bi++)
                {
                    Dictionary<string, object> b = nested[bi] as Dictionary<string, object>;
                    if (b == null) continue;
                    // Com os 66 livros, a posicao e mais segura que a abreviacao ("jo" pode ser Jo ou Joao).
                    string token = nested.Length == 66 ? (bi + 1).ToString() : Str(b, "id", "book", "name", "nome", "abbrev", "abbreviation");
                    if (nested.Length == 66 && Str(b, "name", "nome").Length > 0)
                        declared.Add(new Book { Id = bi + 1, Name = Str(b, "name", "nome"), Code = Str(b, "abbrev", "abbreviation") });
                    object[] chapters = Get(b, "chapters", "capitulos") as object[] ?? new object[0];
                    for (int c = 0; c < chapters.Length; c++)
                    {
                        object[] vs = chapters[c] as object[];
                        if (vs == null) { r.Problem("livro " + token + ", capitulo " + (c + 1) + ": formato nao reconhecido"); continue; }
                        for (int v = 0; v < vs.Length; v++)
                        {
                            Dictionary<string, object> vd = vs[v] as Dictionary<string, object>;
                            string t = vd != null ? Str(vd, "text", "texto", "t") : Convert.ToString(vs[v], CultureInfo.InvariantCulture);
                            int vn = vd != null && Int(Get(vd, "verse", "versiculo", "v")) > 0 ? Int(Get(vd, "verse", "versiculo", "v")) : v + 1;
                            raws.Add(new RawVerse { Book = token, C = c + 1, V = vn, T = t, Where = "livro " + token + " " + (c + 1) + ":" + vn });
                        }
                    }
                }
            }
            else
            {
                if (books != null)
                    foreach (object o in books)
                    {
                        Dictionary<string, object> b = o as Dictionary<string, object>;
                        int id = b != null ? Int(Get(b, "id", "book", "livro")) : 0;
                        if (id > 0) declared.Add(new Book { Id = id, Name = Str(b, "name", "nome"), Code = Str(b, "code", "abbrev", "abbreviation"),
                            Testament = Str(b, "testament", "testamento").ToUpperInvariant(), Chapters = Int(Get(b, "chapters", "capitulos")) });
                    }
                if (verses == null) { r.Problem("versao '" + key + "': nao ha lista de versiculos"); return; }
                for (int i = 0; i < verses.Length; i++)
                {
                    Dictionary<string, object> vd = verses[i] as Dictionary<string, object>;
                    if (vd == null) { r.Problem("versiculo " + (i + 1) + ": formato nao reconhecido"); continue; }
                    raws.Add(new RawVerse
                    {
                        Book = Str(vd, "b", "book", "book_id", "bookid", "livro", "book_name", "livro_nome"),
                        C = Int(Get(vd, "c", "chapter", "capitulo", "cap")),
                        V = Int(Get(vd, "v", "verse", "versiculo", "vers")),
                        T = Str(vd, "t", "text", "texto"),
                        Where = "versiculo " + (i + 1)
                    });
                }
            }
            BibleVersion ver = Build(raws, known, r, key, Str(d, "label", "name", "nome", "title", "titulo"), Str(d, "abbr", "abbreviation", "sigla"), Str(d, "lang", "language", "idioma"));
            foreach (Book b in declared)
            {
                if (ver.BooksById.ContainsKey(b.Id) || string.IsNullOrEmpty(b.Name)) continue;
                if (b.Testament != "AT" && b.Testament != "NT") b.Testament = "";
                ver.Books.Add(b); ver.BooksById[b.Id] = b; ver.BooksByName[b.Name] = b;
            }
            r.Versions.Add(ver);
        }

        static BibleVersion Build(List<RawVerse> raws, List<BibleVersion> known, ImportResult r, string key, string label, string abbr, string lang)
        {
            BibleVersion naming;
            Dictionary<string, int> map = ResolveBooks(raws.Select(x => x.Book ?? "").Distinct(), known, out naming);
            BibleVersion ver = new BibleVersion();
            ver.Key = key ?? ""; ver.Label = string.IsNullOrEmpty(label) ? key : label; ver.Abbr = abbr ?? ""; ver.Lang = lang ?? "";
            HashSet<long> seen = new HashSet<long>();
            foreach (RawVerse raw in raws)
            {
                int id;
                if (!map.TryGetValue(raw.Book ?? "", out id) || id <= 0) { r.Problem(raw.Where + ": livro nao reconhecido '" + raw.Book + "'"); continue; }
                string t = System.Text.RegularExpressions.Regex.Replace(raw.T ?? "", @"\s+", " ").Trim();
                if (raw.C <= 0 || raw.V <= 0) { r.Problem(raw.Where + ": capitulo/versiculo invalido"); continue; }
                if (t.Length == 0) { r.Problem(raw.Where + ": texto vazio"); continue; }
                if (!seen.Add((long)id * 1000000 + raw.C * 1000 + raw.V)) { r.Problem(raw.Where + ": versiculo repetido (" + id + " " + raw.C + ":" + raw.V + "), mantido o primeiro"); continue; }
                DataLoader.AddVerse(ver, new Verse { B = id, C = raw.C, V = raw.V, T = t });
            }
            return ver;
        }

        // Livros que aparecem nos versiculos mas nao foram declarados recebem nome e testamento de uma versao
        // ja carregada; livros sem nenhum versiculo saem da lista; capitulos = maior capitulo encontrado.
        static void Complete(BibleVersion ver, List<BibleVersion> known, ImportResult r)
        {
            Dictionary<int, int> maxChapter = new Dictionary<int, int>();
            foreach (Verse v in ver.Verses) { int m; maxChapter.TryGetValue(v.B, out m); maxChapter[v.B] = Math.Max(m, v.C); }
            List<Book> books = new List<Book>();
            foreach (int id in maxChapter.Keys.OrderBy(x => x))
            {
                Book b;
                if (!ver.BooksById.TryGetValue(id, out b)) b = new Book { Id = id };
                Book reference = known.Select(k => { Book x; return k.BooksById.TryGetValue(id, out x) ? x : null; }).FirstOrDefault(x => x != null);
                if (string.IsNullOrEmpty(b.Name)) b.Name = reference != null ? reference.Name : "Livro " + id;
                if (string.IsNullOrEmpty(b.Code)) b.Code = reference != null ? reference.Code : id.ToString();
                if (b.Testament != "AT" && b.Testament != "NT") b.Testament = reference != null ? reference.Testament : (id <= 39 ? "AT" : "NT");
                b.Chapters = maxChapter[id];
                books.Add(b);
            }
            ver.Books = books;
            ver.BooksById = books.ToDictionary(b => b.Id);
            ver.BooksByName = new Dictionary<string, Book>();
            foreach (Book b in books) ver.BooksByName[b.Name] = b;
            foreach (Verse v in ver.Verses) v.Testament = ver.BooksById[v.B].Testament;
            foreach (List<Verse> list in ver.Chapters.Values) list.Sort((a, b) => a.V.CompareTo(b.V));
            // Sugestao de sigla a partir da chave ("pt-acf" -> ACF) ou do nome do arquivo.
            if (string.IsNullOrEmpty(ver.Abbr))
            {
                string k = ver.Key.Contains("-") ? ver.Key.Substring(ver.Key.LastIndexOf('-') + 1) : ver.Key;
                ver.Abbr = new string(TextUtil.Norm(k).Where(char.IsLetterOrDigit).Take(10).ToArray()).ToUpperInvariant();
            }
        }

        // Chave interna das versoes importadas: "usr-" + sigla (so letras e numeros).
        public static string KeyFor(string abbr)
        {
            return "usr-" + new string(TextUtil.Norm(abbr).Where(char.IsLetterOrDigit).ToArray());
        }

        // Grava no formato do programa (um arquivo por versao). Grava num temporario e troca, para nunca deixar arquivo pela metade.
        public static void Save(BibleVersion ver, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            Func<string, string> clean = s => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            string tmp = path + ".tmp";
            using (StreamWriter w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            {
                w.WriteLine("# Versao importada pelo usuario na Biblia Digital em " + DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) + ".");
                w.WriteLine("# Mesmo formato de data\\biblias.txt: @V chave sigla idioma nome / @L id codigo AT|NT capitulos nome / livro capitulo versiculo texto.");
                w.WriteLine("@V\t" + clean(ver.Key) + "\t" + clean(ver.Abbr) + "\t" + clean(ver.Lang) + "\t" + clean(ver.Label));
                foreach (Book b in ver.Books)
                    w.WriteLine("@L\t" + b.Id + "\t" + clean(b.Code) + "\t" + b.Testament + "\t" + b.Chapters + "\t" + clean(b.Name));
                foreach (Verse v in ver.Verses.OrderBy(x => x.B).ThenBy(x => x.C).ThenBy(x => x.V))
                    w.WriteLine(v.B + "\t" + v.C + "\t" + v.V + "\t" + clean(v.T));
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }

    // Lista de resultados com o termo buscado destacado dentro do texto.
    // Estudos do usuario: anotacoes, marca-texto, favoritos e historico, em %LOCALAPPDATA%\BibliaDigital.
    // A chave e livro/capitulo/versiculo, entao valem para todas as versoes.
    // Cada gravacao troca o arquivo inteiro e guarda a versao anterior em .bak.
    static class UserData
    {
        public static bool Memory = false; // autoteste: nada e lido nem gravado em disco
        public static readonly Dictionary<long, string> Notes = new Dictionary<long, string>();
        public static readonly Dictionary<long, string> Marks = new Dictionary<long, string>();
        public static readonly Dictionary<long, string> Favorites = new Dictionary<long, string>(); // valor: data
        public static readonly List<string[]> History = new List<string[]>(); // livro, capitulo, versiculo, versao, data
        public const int HistoryLimit = 100;
        public static readonly string[] MarkNames = { "amarelo", "verde", "azul", "rosa" };
        public static readonly string[] MarkLabels = { "Amarelo", "Verde", "Azul", "Rosa" };
        static readonly Color[] MarkColors =
        {
            Color.FromArgb(255, 236, 140), Color.FromArgb(178, 230, 170), Color.FromArgb(170, 210, 245), Color.FromArgb(245, 190, 215)
        };

        public static long Key(int b, int c, int v) { return (long)b * 1000000 + c * 1000 + v; }
        public static int[] Split(long k) { return new[] { (int)(k / 1000000), (int)(k / 1000 % 1000), (int)(k % 1000) }; }

        public static Color MarkColor(string name, Color back)
        {
            int i = Array.IndexOf(MarkNames, name);
            if (i < 0) return back;
            Color m = MarkColors[i];
            // Em temas escuros a cor e misturada ao fundo, para o texto claro continuar legivel.
            int w = back.GetBrightness() < 0.4f ? 40 : 100;
            return Color.FromArgb((m.R * w + back.R * (100 - w)) / 100, (m.G * w + back.G * (100 - w)) / 100, (m.B * w + back.B * (100 - w)) / 100);
        }

        public static string Get(Dictionary<long, string> map, int b, int c, int v)
        {
            string s;
            return map.TryGetValue(Key(b, c, v), out s) ? s : null;
        }

        static string Esc(string s) { return s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\n"); }
        static string Unesc(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    sb.Append(n == 'n' ? "\r\n" : n == 't' ? "\t" : n.ToString());
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        public static void Load()
        {
            Notes.Clear(); Marks.Clear(); Favorites.Clear(); History.Clear();
            if (Memory) return;
            LoadMap("notas.txt", Notes, true);
            LoadMap("marcas.txt", Marks, false);
            LoadMap("favoritos.txt", Favorites, false);
            try
            {
                string p = Path.Combine(Program.AppDataDir(), "historico.txt");
                if (File.Exists(p))
                    foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
                    {
                        string[] c = line.Split('\t');
                        int x;
                        if (c.Length >= 5 && int.TryParse(c[0], out x) && History.Count < HistoryLimit) History.Add(c);
                    }
            }
            catch { }
        }

        static void LoadMap(string file, Dictionary<long, string> map, bool escaped)
        {
            try
            {
                string p = Path.Combine(Program.AppDataDir(), file);
                if (!File.Exists(p)) return;
                foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
                {
                    if (line.StartsWith("#")) continue;
                    string[] c = line.Split(new[] { '\t' }, 4);
                    int b, ch, v;
                    if (c.Length < 4 || !int.TryParse(c[0], out b) || !int.TryParse(c[1], out ch) || !int.TryParse(c[2], out v)) continue;
                    map[Key(b, ch, v)] = escaped ? Unesc(c[3]) : c[3];
                }
            }
            catch { }
        }

        static void Write(string file, string content)
        {
            if (Memory) return;
            try
            {
                string p = Path.Combine(Program.AppDataDir(), file), tmp = p + ".tmp";
                File.WriteAllText(tmp, content, Encoding.UTF8);
                if (File.Exists(p)) File.Replace(tmp, p, p + ".bak"); else File.Move(tmp, p);
            }
            catch { }
        }

        static void SaveMap(string file, Dictionary<long, string> map, bool escaped, string header)
        {
            StringBuilder sb = new StringBuilder("# " + header + "\r\n");
            foreach (long k in map.Keys.OrderBy(x => x))
            {
                int[] r = Split(k);
                sb.Append(r[0] + "\t" + r[1] + "\t" + r[2] + "\t" + (escaped ? Esc(map[k]) : map[k]) + "\r\n");
            }
            Write(file, sb.ToString());
        }

        static string Now() { return DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }

        public static void SetNote(int b, int c, int v, string text)
        {
            long k = Key(b, c, v);
            if (string.IsNullOrWhiteSpace(text)) Notes.Remove(k); else Notes[k] = text.Trim();
            SaveMap("notas.txt", Notes, true, "Anotacoes: livro<TAB>capitulo<TAB>versiculo<TAB>texto (\\n = quebra de linha)");
        }

        public static void SetMark(int b, int c, int v, string color)
        {
            long k = Key(b, c, v);
            if (string.IsNullOrEmpty(color)) Marks.Remove(k); else Marks[k] = color;
            SaveMap("marcas.txt", Marks, false, "Marca-texto: livro<TAB>capitulo<TAB>versiculo<TAB>cor");
        }

        public static void SetFavorite(int b, int c, int v, bool on)
        {
            long k = Key(b, c, v);
            if (on) Favorites[k] = Now(); else Favorites.Remove(k);
            SaveMap("favoritos.txt", Favorites, false, "Favoritos: livro<TAB>capitulo<TAB>versiculo<TAB>data");
        }

        // Um item por capitulo lido: reabrir o mesmo capitulo so atualiza o item mais recente.
        public static void AddHistory(int b, int c, int v, string version)
        {
            History.RemoveAll(h => h[0] == b.ToString() && h[1] == c.ToString() && h[3] == version);
            History.Insert(0, new[] { b.ToString(), c.ToString(), v.ToString(), version, Now() });
            while (History.Count > HistoryLimit) History.RemoveAt(History.Count - 1);
            SaveHistory();
        }

        public static void ClearHistory() { History.Clear(); SaveHistory(); }

        static void SaveHistory()
        {
            Write("historico.txt", string.Join("", History.Select(h => string.Join("\t", h) + "\r\n").ToArray()));
        }
    }

    class ResultsListView : ListView
    {
        public ResultsListView() { DoubleBuffered = true; OwnerDraw = true; }
    }

    // Temas de cor da leitura: fundo, texto, numeros/titulo e destaque do versiculo.
    class ReadingTheme
    {
        public string Name;
        public Color Back, Fore, Accent, Highlight;
        public bool Dark;

        static ReadingTheme T(string name, Color back, Color fore, Color accent, Color highlight, bool dark)
        {
            ReadingTheme t = new ReadingTheme();
            t.Name = name; t.Back = back; t.Fore = fore; t.Accent = accent; t.Highlight = highlight; t.Dark = dark;
            return t;
        }

        public static readonly ReadingTheme[] All =
        {
            T("Branco", Color.White, Color.Black, Color.DarkBlue, Color.LemonChiffon, false),
            T("Creme", Color.FromArgb(255, 252, 235), Color.Black, Color.DarkBlue, Color.FromArgb(255, 236, 170), false),
            T("Sepia", Color.FromArgb(244, 236, 216), Color.FromArgb(91, 70, 54), Color.FromArgb(139, 69, 19), Color.FromArgb(233, 212, 160), false),
            T("Cinza claro", Color.FromArgb(245, 245, 245), Color.FromArgb(30, 30, 30), Color.DarkBlue, Color.LemonChiffon, false),
            T("Amarelo suave", Color.LemonChiffon, Color.Black, Color.DarkBlue, Color.FromArgb(255, 220, 120), false),
            T("Verde suave", Color.FromArgb(232, 245, 233), Color.FromArgb(20, 45, 20), Color.DarkGreen, Color.FromArgb(200, 230, 170), false),
            T("Azul noite", Color.FromArgb(22, 30, 48), Color.FromArgb(220, 228, 240), Color.FromArgb(130, 180, 255), Color.FromArgb(50, 70, 110), true),
            T("Preto", Color.FromArgb(24, 24, 24), Color.White, Color.LightSkyBlue, Color.DarkSlateBlue, true),
            T("Alto contraste", Color.Black, Color.Yellow, Color.Cyan, Color.FromArgb(0, 90, 0), true),
        };

        public static int IndexOf(string name)
        {
            for (int i = 0; i < All.Length; i++) if (All[i].Name == name) return i;
            return 0;
        }
    }

    class MainForm : Form
    {
        List<BibleVersion> versions;
        BibleVersion current;
        Dictionary<string, List<string>> analogico, sinonimos;
        string savedVersionKey = "";
        int suggestSource = 0; // 0 = dicionario analogico, 1 = sinonimos, 2 = ambos
        static readonly string[] SuggestSourceNames = { "Dicionario analogico", "Sinonimos", "Ambos" };
        ComboBox cmbSuggest;

        int currentBookId = 1, currentChapter = 1, currentVerse = 1;
        int readerFontSize = 12, resultFontSize = 9;
        bool continuousText { get { return readingMode == 1; } set { readingMode = value ? 1 : 0; } }
        int readingMode = 0; // 0 versiculos, 1 texto corrido, 2 paragrafos, 3 livro aberto
        string readerFontName = "Georgia";
        int themeIndex = 0;
        // Diagramacao dos modos de rolagem (versiculos, corrido, paragrafos).
        bool justifyText = true;
        int lineSpacingIndex = 1;  // 0 simples, 1 = 1,15, 2 = 1,3, 3 = 1,5
        int columnIndex = 1;       // largura da coluna: 0 estreita, 1 media, 2 larga, 3 toda a janela
        static readonly string[] LineSpacingNames = { "Simples", "1,15", "1,3", "1,5" };
        static readonly int[] LineSpacingRule = { 20, 23, 26, 30 };
        static readonly string[] ColumnNames = { "Estreita (65 letras)", "Media (80 letras)", "Larga (100 letras)", "Toda a janela" };
        static readonly int[] ColumnChars = { 65, 80, 100, 0 };
        bool syncingUi = false;
        public bool NoSave = false; // autoteste nao grava preferencias

        const int MinReaderSize = 8, MaxReaderSize = 32;
        static readonly string[] CandidateFonts =
        {
            "Georgia", "Cambria", "Times New Roman", "Palatino Linotype", "Book Antiqua", "Constantia",
            "Segoe UI", "Calibri", "Arial", "Verdana", "Tahoma", "Trebuchet MS", "Consolas"
        };
        List<string> readerFonts;

        TabControl tabs;
        TabPage tabIndex;
        ComboBox cmbVersion, cmbAT, cmbATCap, cmbATVer, cmbNT, cmbNTCap, cmbNTVer, cmbBg;
        TextBox txtSearch;
        CheckBox chkExact, chkFlex, chkImmediate;
        RadioButton rbAnd, rbOr, rbAll, rbOT, rbNT, rbBook;
        ListBox lstSuggest;
        Label lblCount;
        ListView results;
        ToolTip tip;
        CheckBox chkNewTab;

        // Destaque das palavras buscadas ao abrir um resultado.
        class SearchMarks
        {
            public List<string> Terms = new List<string>(); // normalizados (minusculo, sem acento)
            public bool WholeWords;                          // Exata: so palavras inteiras
        }

        // Uma aba de passagem: cada uma guarda sua versao, livro, capitulo e destaque.
        class PassageTab
        {
            public BibleVersion Version;
            public int BookId, Chapter, Verse;
            public SearchMarks Marks;
            public TabPage Page;
            public RichTextBox Reader;
            public Label Title, Size;
            public ComboBox Mode, FontBox, ThemeBox;
            public Panel ReadingHost;
            public TableLayoutPanel BookLayout;
            public RichTextBox LeftPage, RightPage;
            public Panel LeftPaper, RightPaper;           // folha de papel com margens em volta do texto
            public Label LeftHead, RightHead, LeftFolio, RightFolio; // cabecalho corrido e numero da pagina
            public Label PageCounter;
            public Button PrevPage, NextPage;
            public List<string> BookPages = new List<string>();
            public int SpreadIndex;
            public Timer ResizeTimer; // espera o fim do redimensionamento antes de repaginar
            public List<int[]> VerseRanges = new List<int[]>(); // inicio, fim, versiculo no Reader (modos 0-2)
            public List<int> PageFirstVerse = new List<int>();  // livro aberto: versiculo em curso no topo de cada pagina
            public int MenuVerse;                                // versiculo sob o botao direito
            public RichTextBox MenuBox;
            public int TipVerse = -1;
            public int LastWheel;                                // evita virar varias folhas num giro rapido da roda
            public Size PagedSize;                               // tamanho da folha usado na ultima paginacao
        }

        string lastRead = ""; // versao|livro|capitulo|versiculo da ultima leitura
        Button btnResume, btnResume2;
        TabPage tabStudies;
        ListView lvFav, lvNotes, lvMarks, lvHist;

        List<PassageTab> passageTabs = new List<PassageTab>();
        PassageTab lastPassage;      // ultima aba usada (reaproveitada quando nao e "nova aba")
        SearchMarks lastSearchMarks; // termos da ultima busca
        bool openInNewTab = false;
        RichTextBox txtChapter { get { return lastPassage != null ? lastPassage.Reader : null; } }

        public MainForm(List<BibleVersion> versions, Dictionary<string, List<string>> analogico, Dictionary<string, List<string>> sinonimos)
        {
            this.versions = versions;
            this.analogico = analogico;
            this.sinonimos = sinonimos;
            current = versions[0];
            readerFonts = InstalledReaderFonts();
            LoadSettings();
            UserData.Load();
            current = versions.FirstOrDefault(v => v.Key == savedVersionKey) ?? versions[0];
            BuildUi();
            FillBookCombos();
        }

        static T Add<T>(Control parent, T c, int left, int top, int width) where T : Control
        {
            c.Left = left; c.Top = top; c.Width = width;
            parent.Controls.Add(c);
            return c;
        }

        static Label MakeLabel(string text) { Label l = new Label(); l.Text = text; return l; }
        static ComboBox MakeCombo() { ComboBox c = new ComboBox(); c.DropDownStyle = ComboBoxStyle.DropDownList; return c; }
        static Button MakeButton(string text) { Button b = new Button(); b.Text = text; return b; }

        void BuildUi()
        {
            Text = "Biblia Digital - Windows 11 v1.9.2";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1120, 780); // espaco para o rodape sem cortar o quadro de Busca
            MinimumSize = new Size(950, 720);
            Font = new Font("Segoe UI", 9);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            Controls.Add(tabs);
            Controls.Add(BuildFooter()); // adicionado depois: ancora embaixo e a TabControl ocupa o resto

            tabIndex = new TabPage("Indice Digital");
            tabs.TabPages.Add(tabIndex);
            tabStudies = new TabPage("Meus estudos");
            tabs.TabPages.Add(tabStudies);

            TableLayoutPanel main = new TableLayoutPanel();
            main.Dock = DockStyle.Fill;
            main.ColumnCount = 2; main.RowCount = 1;
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 390));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tabIndex.Controls.Add(main);

            Panel left = new Panel(); left.Dock = DockStyle.Fill; left.Padding = new Padding(8);
            left.AutoScroll = true; // telas baixas (768 px): rola em vez de cortar a Busca
            main.Controls.Add(left, 0, 0);
            Panel right = new Panel(); right.Dock = DockStyle.Fill; right.Padding = new Padding(8);
            main.Controls.Add(right, 1, 0);

            Label title = MakeLabel("Indice Digital");
            title.Font = new Font("Segoe UI", 11, FontStyle.Bold);
            title.TextAlign = ContentAlignment.MiddleCenter;
            title.Dock = DockStyle.Top; title.Height = 28;
            left.Controls.Add(title);

            Add(left, MakeLabel("Versao:"), 8, 38, 55);
            cmbVersion = Add(left, MakeCombo(), 65, 35, 222);
            cmbVersion.DropDownWidth = 380;
            foreach (BibleVersion v in versions) cmbVersion.Items.Add(VersionItemText(v));
            cmbVersion.SelectedIndex = versions.IndexOf(current);
            Button btnVersions = Add(left, MakeButton("Versoes..."), 292, 34, 73); btnVersions.Height = 25;

            Add(left, MakeLabel("Antigo Testamento:"), 8, 75, 150);
            cmbAT = Add(left, MakeCombo(), 8, 95, 150);
            cmbATCap = Add(left, MakeCombo(), 165, 95, 50);
            cmbATVer = Add(left, MakeCombo(), 225, 95, 50);
            Button btnAT = Add(left, MakeButton("Abrir"), 285, 93, 75); btnAT.Height = 25;

            Add(left, MakeLabel("Novo Testamento:"), 8, 130, 150);
            cmbNT = Add(left, MakeCombo(), 8, 150, 150);
            cmbNTCap = Add(left, MakeCombo(), 165, 150, 50);
            cmbNTVer = Add(left, MakeCombo(), 225, 150, 50);
            Button btnNT = Add(left, MakeButton("Abrir"), 285, 148, 75); btnNT.Height = 25;

            chkNewTab = Add(left, new CheckBox(), 8, 178, 300);
            chkNewTab.Text = "Abrir cada passagem em uma nova aba";
            chkNewTab.Checked = openInNewTab;

            GroupBox grpDisplay = Add(left, new GroupBox(), 8, 205, 365);
            grpDisplay.Text = "Aparencia da leitura"; grpDisplay.Height = 75;
            Button btnFontMinus = Add(grpDisplay, MakeButton("A-"), 10, 25, 45);
            Button btnFontPlus = Add(grpDisplay, MakeButton("A+"), 60, 25, 45);
            Add(grpDisplay, MakeLabel("Tema de cor:"), 125, 29, 85);
            cmbBg = Add(grpDisplay, MakeCombo(), 210, 25, 135);
            foreach (ReadingTheme t in ReadingTheme.All) cmbBg.Items.Add(t.Name);
            cmbBg.SelectedIndex = themeIndex;

            GroupBox grpSearch = Add(left, new GroupBox(), 8, 290, 365);
            grpSearch.Text = "Busca"; grpSearch.Height = 330;
            Add(grpSearch, MakeLabel("Procurar:"), 10, 28, 65);
            txtSearch = Add(grpSearch, new TextBox(), 75, 25, 205);
            Button btnSearch = Add(grpSearch, MakeButton("Buscar"), 285, 23, 65);

            chkExact = Add(grpSearch, new CheckBox(), 10, 58, 70); chkExact.Text = "Exata";
            chkFlex = Add(grpSearch, new CheckBox(), 85, 58, 80); chkFlex.Text = "Flexivel"; chkFlex.Checked = true;
            chkImmediate = Add(grpSearch, new CheckBox(), 170, 58, 125); chkImmediate.Text = "Busca imediata";

            GroupBox grpLogic = Add(grpSearch, new GroupBox(), 10, 88, 105);
            grpLogic.Text = "Operador"; grpLogic.Height = 70;
            rbAnd = Add(grpLogic, new RadioButton(), 10, 18, 45); rbAnd.Text = "E"; rbAnd.Checked = true;
            rbOr = Add(grpLogic, new RadioButton(), 10, 42, 50); rbOr.Text = "OU";

            rbAll = Add(grpSearch, new RadioButton(), 130, 92, 150); rbAll.Text = "em toda a Biblia"; rbAll.Checked = true;
            rbOT = Add(grpSearch, new RadioButton(), 130, 116, 170); rbOT.Text = "no Antigo Testamento";
            rbNT = Add(grpSearch, new RadioButton(), 130, 140, 170); rbNT.Text = "no Novo Testamento";
            rbBook = Add(grpSearch, new RadioButton(), 130, 164, 150); rbBook.Text = "no livro aberto";

            Add(grpSearch, MakeLabel("Sugestoes por:"), 10, 191, 90);
            cmbSuggest = Add(grpSearch, MakeCombo(), 100, 187, 170);
            cmbSuggest.Items.AddRange(SuggestSourceNames);
            cmbSuggest.SelectedIndex = suggestSource;
            lstSuggest = Add(grpSearch, new ListBox(), 10, 212, 340); lstSuggest.Height = 75;
            Label lblHelp = Add(grpSearch, MakeLabel("Exata: palavras inteiras. Flexivel: + 3 termos das sugestoes."), 10, 292, 340);
            lblHelp.Height = 28;

            Label lblResultsTitle = MakeLabel("Referencias e textos encontrados");
            lblResultsTitle.Font = new Font("Segoe UI", 11, FontStyle.Bold);
            lblResultsTitle.Dock = DockStyle.Top; lblResultsTitle.Height = 30;
            lblCount = MakeLabel("Digite um termo e clique em Buscar.");
            lblCount.Dock = DockStyle.Top; lblCount.Height = 24;

            results = new ResultsListView();
            results.View = View.Details;
            results.FullRowSelect = true;
            results.GridLines = true;
            results.Dock = DockStyle.Fill;
            results.ShowItemToolTips = true;
            results.Font = new Font("Segoe UI", resultFontSize);
            results.Columns.Add("Referencia", 120);
            results.Columns.Add("Texto encontrado", 600);

            results.DrawColumnHeader += (sender, e) => { e.DrawDefault = true; };
            results.DrawItem += (sender, e) => { };
            results.DrawSubItem += (sender, e) => DrawResultCell(e);

            // Ordem de Dock: o controle adicionado por ultimo fica mais externo.
            right.Controls.Add(results);
            right.Controls.Add(lblCount);
            right.Controls.Add(lblResultsTitle);
            btnResume = MakeButton(""); btnResume.Dock = DockStyle.Top; btnResume.Height = 34;
            btnResume.TextAlign = ContentAlignment.MiddleLeft;
            btnResume.Click += delegate { ResumeReading(); };
            right.Controls.Add(btnResume);

            tip = new ToolTip();
            tip.AutoPopDelay = 12000; tip.InitialDelay = 250; tip.ReshowDelay = 100;
            BuildStudiesTab();
            UpdateResumeButtons();
            tip.SetToolTip(btnVersions, "Importar uma nova versao da Biblia ou remover uma versao importada");
            btnVersions.Click += delegate { ShowVersionsDialog(); };

            // Eventos
            cmbVersion.SelectedIndexChanged += delegate
            {
                if (cmbVersion.SelectedIndex >= 0) current = versions[cmbVersion.SelectedIndex];
                savedVersionKey = current.Key;
                SaveSettings();
                FillBookCombos();
            };
            cmbAT.SelectedIndexChanged += delegate { FillChapters(cmbAT, cmbATCap, cmbATVer); };
            cmbATCap.SelectedIndexChanged += delegate { FillVerses(cmbAT, cmbATCap, cmbATVer); };
            cmbNT.SelectedIndexChanged += delegate { FillChapters(cmbNT, cmbNTCap, cmbNTVer); };
            cmbNTCap.SelectedIndexChanged += delegate { FillVerses(cmbNT, cmbNTCap, cmbNTVer); };
            btnAT.Click += delegate { GoFromCombos(cmbAT, cmbATCap, cmbATVer); };
            btnNT.Click += delegate { GoFromCombos(cmbNT, cmbNTCap, cmbNTVer); };
            btnSearch.Click += delegate { DoSearch(); };
            txtSearch.TextChanged += delegate
            {
                UpdateSuggestions();
                if (chkImmediate.Checked && txtSearch.Text.Trim().Length >= 3) DoSearch();
            };
            txtSearch.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { DoSearch(); e.SuppressKeyPress = true; }
            };
            lstSuggest.DoubleClick += delegate
            {
                if (lstSuggest.SelectedItem != null) { txtSearch.Text = lstSuggest.SelectedItem.ToString(); DoSearch(); }
            };
            results.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                ListViewHitTestInfo hit = results.HitTest(e.Location);
                if (hit.Item != null) OpenFromItem(hit.Item, (ModifierKeys & Keys.Control) != 0 ? true : (bool?)null);
            };
            ContextMenuStrip resultsMenu = new ContextMenuStrip();
            resultsMenu.Items.Add("Abrir", null, delegate { if (results.SelectedItems.Count > 0) OpenFromItem(results.SelectedItems[0], false); });
            resultsMenu.Items.Add("Abrir em nova aba", null, delegate { if (results.SelectedItems.Count > 0) OpenFromItem(results.SelectedItems[0], true); });
            results.ContextMenuStrip = resultsMenu;
            tip.SetToolTip(chkNewTab, "Mesmo desmarcado: Ctrl+clique no resultado ou botao direito > Abrir em nova aba");
            chkNewTab.CheckedChanged += delegate { openInNewTab = chkNewTab.Checked; SaveSettings(); };
            cmbSuggest.SelectedIndexChanged += delegate
            {
                suggestSource = Math.Max(0, cmbSuggest.SelectedIndex);
                SaveSettings();
                UpdateSuggestions();
            };

            // Abas: clique do meio fecha; botao direito mostra menu de fechar.
            tabs.SelectedIndexChanged += delegate
            {
                PassageTab p = ActivePassage();
                if (p != null) { lastPassage = p; currentBookId = p.BookId; }
                if (tabs.SelectedTab == tabStudies) RefreshStudies();
            };
            tabs.MouseUp += (s, e) =>
            {
                for (int i = 0; i < tabs.TabPages.Count; i++)
                {
                    if (!tabs.GetTabRect(i).Contains(e.Location)) continue;
                    PassageTab p = passageTabs.FirstOrDefault(x => x.Page == tabs.TabPages[i]);
                    if (p == null) return;
                    if (e.Button == MouseButtons.Middle) ClosePassage(p);
                    else if (e.Button == MouseButtons.Right)
                    {
                        ContextMenuStrip m = new ContextMenuStrip();
                        m.Items.Add("Fechar esta aba", null, delegate { ClosePassage(p); });
                        m.Items.Add("Fechar as outras abas", null, delegate { CloseOtherPassages(p); });
                        m.Items.Add("Fechar todas as passagens", null, delegate { CloseOtherPassages(null); });
                        m.Show(tabs, e.Location);
                    }
                    return;
                }
            };
            results.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && results.SelectedItems.Count > 0) OpenFromItem(results.SelectedItems[0]);
            };
            ListViewItem lastTipItem = null;
            results.MouseMove += (s, e) =>
            {
                ListViewHitTestInfo hit = results.HitTest(e.Location);
                if (hit.Item != null && hit.Item != lastTipItem)
                {
                    lastTipItem = hit.Item;
                    tip.SetToolTip(results, hit.Item.ToolTipText);
                }
            };
            btnFontMinus.Click += delegate
            {
                ChangeFontSize(-1);
            };
            btnFontPlus.Click += delegate
            {
                ChangeFontSize(+1);
            };
            cmbBg.SelectedIndexChanged += delegate
            {
                if (syncingUi || cmbBg.SelectedIndex < 0) return;
                themeIndex = cmbBg.SelectedIndex;
                SaveSettings();
                RefreshOpenChapter();
            };

            // Exata e Flexivel sao modos opostos: marcar um desmarca o outro.
            // Busca imediata e independente (so define quando a busca dispara).
            chkExact.CheckedChanged += delegate
            {
                if (chkExact.Checked) chkFlex.Checked = false;
                grpLogic.Enabled = !chkExact.Checked; // E/OU nao se aplica a frase exata
            };
            chkFlex.CheckedChanged += delegate
            {
                if (chkFlex.Checked) chkExact.Checked = false;
            };
        }

        // ---- Rodape: uma linha com a propriedade, o portal e o link para os creditos completos ----
        // (os creditos e licencas de cada texto ficam na janela "Creditos e direitos dos textos").

        public const string PortalUrl = "http://estudobiblico.org";
        public const string CreditsLink = "Créditos e direitos dos textos";
        public const string FooterText = "Bíblia Digital © 2026 Estudo Bíblico — distribuição livre e gratuita.   Portal: " + PortalUrl + "   ·   " + CreditsLink;
        public static readonly string[] FooterLines =
        {
            "Bíblia Digital © 2026 Estudo Bíblico — distribuição livre e gratuita autorizada. Portal: " + PortalUrl,
            "Versões incluídas de uso livre: Almeida 1911 (original e com ortografia atualizada), King James, Reina-Valera 1909 e Louis Segond 1910 (domínio público) e Bíblia Livre (licença CC BY 3.0 BR).",
            "Almeida 1911 (ortografia atualizada): só a grafia foi atualizada pelo Estudo Bíblico (2026), mantendo vocabulário e gramática de 1911; o texto continua em domínio público.",
            "A VEB — Versão Estudo Bíblico pertence ao Estudo Bíblico. Versões importadas pelo usuário seguem os direitos de seus próprios autores.",
            "Parágrafos do Novo Testamento: SBL Greek New Testament (SBLGNT), © 2010 Society of Biblical Literature e Logos Bible Software, licença CC BY 4.0 (https://sblgnt.com). Só a divisão em parágrafos, aplicada por referência e adaptada; o SBL e a Logos não endossam esta adaptação.",
            "Parágrafos do Antigo Testamento e texto-base da VEB no AT: Westminster Leningrad Codex, via Open Scriptures Hebrew Bible, licença CC BY 4.0.",
            "",
            "Bíblia Livre: © Diego Santos, Mário Sérgio e Marco Teles, Creative Commons Atribuição 3.0 Brasil (http://creativecommons.org/licenses/by/3.0/br/). Nesta edição foram corrigidos erros de digitação e de acentuação, sem mudar a tradução.",
            "Veja também LICENCA.txt e LEIA-ME.txt, que acompanham o programa."
        };

        Control BuildFooter()
        {
            Panel footer = new Panel();
            footer.Dock = DockStyle.Bottom; footer.Height = 54; footer.Padding = new Padding(8, 3, 8, 3);
            footer.BackColor = SystemColors.ControlLight;
            LinkLabel text = new LinkLabel();
            text.Dock = DockStyle.Fill; text.AutoSize = false;
            text.TextAlign = ContentAlignment.MiddleCenter;
            text.Font = new Font("Segoe UI", 8.25f);
            text.ForeColor = SystemColors.ControlDarkDark;
            text.Text = FooterText;
            text.Links.Clear();
            text.Links.Add(text.Text.IndexOf(PortalUrl, StringComparison.Ordinal), PortalUrl.Length, "portal");
            text.Links.Add(text.Text.IndexOf(CreditsLink, StringComparison.Ordinal), CreditsLink.Length, "creditos");
            text.LinkClicked += (s, e) =>
            {
                if ((string)e.Link.LinkData == "creditos") { ShowCredits(); return; }
                try { Process.Start(new ProcessStartInfo(PortalUrl) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show(this, "Nao foi possivel abrir o navegador: " + ex.Message + "\r\nEndereco: " + PortalUrl, "Biblia Digital"); }
            };
            footer.Controls.Add(text);
            // Altura acompanha o texto: em janela estreita as linhas quebram e o rodape cresce, sem cortar.
            footer.SizeChanged += delegate
            {
                int w = Math.Max(100, footer.ClientSize.Width - footer.Padding.Horizontal - 4);
                int h = TextRenderer.MeasureText(text.Text, text.Font, new Size(w, int.MaxValue), TextFormatFlags.WordBreak).Height + footer.Padding.Vertical + 6;
                if (footer.Height != h) footer.Height = h;
            };
            // Linha fina separando o rodape do conteudo.
            footer.Paint += (s, e) => e.Graphics.DrawLine(SystemPens.ControlDark, 0, 0, footer.Width, 0);
            return footer;
        }

        void ShowCredits()
        {
            using (Form f = new Form())
            {
                f.Text = "Créditos e direitos dos textos - Bíblia Digital";
                f.StartPosition = FormStartPosition.CenterParent; f.Size = new Size(720, 460); f.MinimumSize = new Size(480, 300);
                f.ShowInTaskbar = false; f.MinimizeBox = false; f.MaximizeBox = false; f.Font = Font;
                TextBox t = new TextBox(); t.Multiline = true; t.ReadOnly = true; t.ScrollBars = ScrollBars.Vertical; t.Dock = DockStyle.Fill;
                t.Font = new Font("Segoe UI", 9.5f); t.BackColor = SystemColors.Window;
                t.Text = string.Join("\r\n", FooterLines);
                Button ok = MakeButton("Fechar"); ok.Dock = DockStyle.Bottom; ok.Height = 34; ok.DialogResult = DialogResult.OK;
                f.Controls.Add(t); f.Controls.Add(ok); f.AcceptButton = ok; f.CancelButton = ok;
                f.Shown += delegate { t.SelectionLength = 0; };
                f.ShowDialog(this);
            }
        }

        // ---- Versoes: importar e remover ----

        static string VersionItemText(BibleVersion v)
        {
            return v.Key + " - " + v.Label + (v.UserFile != null ? "  (importada)" : "");
        }

        void RebuildVersionCombo(BibleVersion select)
        {
            cmbVersion.Items.Clear();
            foreach (BibleVersion v in versions) cmbVersion.Items.Add(VersionItemText(v));
            int i = versions.IndexOf(select);
            cmbVersion.SelectedIndex = i >= 0 ? i : 0; // dispara a troca de versao (current, livros, preferencias)
        }

        // Confere sigla e nome antes de gravar. Devolve a mensagem de erro, ou null se puder importar.
        // replace: versao importada anteriormente com a mesma sigla (sera substituida, se o usuario confirmar).
        public string ValidateImport(BibleVersion ver, string abbr, string label, out BibleVersion replace)
        {
            replace = null;
            abbr = (abbr ?? "").Trim(); label = (label ?? "").Trim();
            if (ver == null || ver.Verses.Count == 0) return "Nenhum versiculo valido foi encontrado no arquivo.";
            if (abbr.Length == 0) return "Informe a sigla da versao (ex.: ACF, NVI, ARA).";
            if (abbr.Length > 12 || !abbr.Any(char.IsLetterOrDigit)) return "A sigla deve ter ate 12 caracteres, com letras ou numeros.";
            if (label.Length == 0) return "Informe o nome da versao.";
            string key = VersionImport.KeyFor(abbr);
            foreach (BibleVersion v in versions)
            {
                bool same = v.Key.Equals(key, StringComparison.OrdinalIgnoreCase) || v.Abbr.Equals(abbr, StringComparison.OrdinalIgnoreCase);
                if (!same) continue;
                if (v.UserFile == null) return "Ja existe uma versao do programa com a sigla " + v.Abbr + ". Escolha outra sigla.";
                replace = v;
            }
            return null;
        }

        // Grava a versao em %LOCALAPPDATA%\BibliaDigital\versoes e a coloca na lista, sem reiniciar o programa.
        public BibleVersion InstallVersion(BibleVersion ver, string abbr, string label, BibleVersion replace)
        {
            ver.Abbr = abbr.Trim(); ver.Label = label.Trim();
            ver.Key = VersionImport.KeyFor(ver.Abbr);
            string path = Path.Combine(Program.VersionsDir(), ver.Key + ".txt");
            VersionImport.Save(ver, path);
            ver.UserFile = path;
            if (replace != null)
            {
                foreach (PassageTab p in passageTabs.Where(x => x.Version == replace).ToList()) ClosePassage(p);
                if (replace.UserFile != null && !replace.UserFile.Equals(path, StringComparison.OrdinalIgnoreCase) && File.Exists(replace.UserFile)) File.Delete(replace.UserFile);
                versions[versions.IndexOf(replace)] = ver;
            }
            else versions.Add(ver);
            RebuildVersionCombo(ver);
            return ver;
        }

        // Remove uma versao importada. O arquivo nao e apagado: vira .removida, para poder ser recuperado.
        public bool RemoveUserVersion(BibleVersion ver)
        {
            if (ver == null || ver.UserFile == null) return false;
            foreach (PassageTab p in passageTabs.Where(x => x.Version == ver).ToList()) ClosePassage(p);
            if (File.Exists(ver.UserFile))
            {
                string old = ver.UserFile + ".removida";
                if (File.Exists(old)) File.Delete(old);
                File.Move(ver.UserFile, old);
            }
            BibleVersion keep = current == ver ? versions[0] : current;
            versions.Remove(ver);
            RebuildVersionCombo(keep);
            return true;
        }

        void ShowVersionsDialog()
        {
            using (Form f = new Form())
            {
                f.Text = "Versoes da Biblia";
                f.StartPosition = FormStartPosition.CenterParent;
                f.Size = new Size(820, 470); f.MinimumSize = new Size(640, 360);
                f.Font = Font; f.ShowInTaskbar = false; f.MinimizeBox = false;
                ListView lv = new ListView(); lv.Dock = DockStyle.Fill; lv.View = View.Details;
                lv.FullRowSelect = true; lv.GridLines = true; lv.MultiSelect = false; lv.HideSelection = false;
                lv.Columns.Add("Sigla", 80); lv.Columns.Add("Nome", 330); lv.Columns.Add("Livros", 60);
                lv.Columns.Add("Versiculos", 80); lv.Columns.Add("Origem", 210);
                CultureInfo br = new CultureInfo("pt-BR");
                Action fill = () =>
                {
                    lv.BeginUpdate(); lv.Items.Clear();
                    foreach (BibleVersion v in versions)
                    {
                        ListViewItem it = new ListViewItem(new[] { v.Abbr, v.Label, v.Books.Count.ToString(), v.Verses.Count.ToString("N0", br),
                            v.UserFile != null ? "importada (" + Path.GetFileName(v.UserFile) + ")" : "incluida no programa" });
                        it.Tag = v;
                        if (v == current) it.Font = new Font(lv.Font, FontStyle.Bold);
                        lv.Items.Add(it);
                    }
                    lv.EndUpdate();
                };
                fill();
                Label info = MakeLabel("Importe arquivos .txt, .csv, .tsv ou .json com livro, capitulo, versiculo e texto (em negrito: versao em uso). " +
                    "As versoes importadas ficam em " + Program.VersionsDir() + " e continuam depois de atualizar o programa.");
                info.Dock = DockStyle.Top; info.Height = 48; info.Padding = new Padding(8, 8, 8, 0);
                FlowLayoutPanel bar = new FlowLayoutPanel(); bar.Dock = DockStyle.Bottom; bar.Height = 44; bar.Padding = new Padding(6);
                Button bImport = MakeButton("Importar versao..."); bImport.Width = 140; bImport.Height = 28;
                Button bRemove = MakeButton("Remover versao"); bRemove.Width = 120; bRemove.Height = 28; bRemove.Enabled = false;
                Button bUse = MakeButton("Usar esta versao"); bUse.Width = 120; bUse.Height = 28; bUse.Enabled = false;
                Button bClose = MakeButton("Fechar"); bClose.Width = 90; bClose.Height = 28; bClose.DialogResult = DialogResult.Cancel;
                bar.Controls.Add(bImport); bar.Controls.Add(bRemove); bar.Controls.Add(bUse); bar.Controls.Add(bClose);
                Panel host = new Panel(); host.Dock = DockStyle.Fill; host.Padding = new Padding(8, 4, 8, 4); host.Controls.Add(lv);
                f.Controls.Add(host); f.Controls.Add(bar); f.Controls.Add(info);
                f.CancelButton = bClose;
                Func<BibleVersion> selected = () => lv.SelectedItems.Count > 0 ? (BibleVersion)lv.SelectedItems[0].Tag : null;
                lv.SelectedIndexChanged += delegate
                {
                    BibleVersion v = selected();
                    bRemove.Enabled = v != null && v.UserFile != null;
                    bUse.Enabled = v != null;
                };
                bUse.Click += delegate { BibleVersion v = selected(); if (v != null) { cmbVersion.SelectedIndex = versions.IndexOf(v); fill(); } };
                lv.DoubleClick += delegate { bUse.PerformClick(); };
                bImport.Click += delegate { if (ImportVersionFlow(f)) fill(); };
                bRemove.Click += delegate
                {
                    BibleVersion v = selected();
                    if (v == null || v.UserFile == null) return;
                    if (MessageBox.Show(f, "Remover a versao " + v.Abbr + " - " + v.Label + "?\r\n\r\nAnotacoes, marcas e favoritos nao sao afetados " +
                        "(valem para todas as versoes). O arquivo fica guardado como " + Path.GetFileName(v.UserFile) + ".removida.",
                        "Biblia Digital", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    try { RemoveUserVersion(v); }
                    catch (Exception ex) { MessageBox.Show(f, "Nao foi possivel remover: " + ex.Message, "Biblia Digital"); }
                    fill();
                };
                f.ShowDialog(this);
            }
        }

        // Escolher o arquivo, conferir o que foi lido e gravar. Devolve true se importou.
        bool ImportVersionFlow(IWin32Window owner)
        {
            string path;
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Importar versao da Biblia";
                d.Filter = "Textos biblicos (*.txt;*.csv;*.tsv;*.json)|*.txt;*.csv;*.tsv;*.json|Todos os arquivos (*.*)|*.*";
                if (d.ShowDialog(owner) != DialogResult.OK) return false;
                path = d.FileName;
            }
            ImportResult r;
            Cursor old = Cursor.Current;
            try
            {
                Cursor.Current = Cursors.WaitCursor;
                r = VersionImport.Parse(path, versions.Where(v => v.UserFile == null).ToList());
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "Nao foi possivel ler o arquivo:\r\n" + ex.Message, "Biblia Digital", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            finally { Cursor.Current = old; }
            if (r.Versions.Count == 0)
            {
                MessageBox.Show(owner, "Nenhum versiculo foi reconhecido em " + Path.GetFileName(path) + ".\r\n\r\n" +
                    string.Join("\r\n", r.Problems.Take(8).ToArray()) + "\r\n\r\nFormatos aceitos: veja o LEIA-ME.txt (secao \"Importar uma versao\").",
                    "Biblia Digital", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return ShowImportPreview(owner, path, r);
        }

        public string ImportSummary(BibleVersion v)
        {
            CultureInfo br = new CultureInfo("pt-BR");
            int at = v.Books.Count(b => b.Testament == "AT"), nt = v.Books.Count(b => b.Testament == "NT");
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(v.Books.Count + " livros (AT " + at + ", NT " + nt + "), " + v.Books.Sum(b => b.Chapters).ToString("N0", br) +
                " capitulos, " + v.Verses.Count.ToString("N0", br) + " versiculos.");
            BibleVersion reference = versions.FirstOrDefault(x => x.UserFile == null && x.Books.Count >= 66);
            if (reference != null)
            {
                List<string> missing = reference.Books.Where(b => !v.BooksById.ContainsKey(b.Id)).Select(b => b.Name).ToList();
                if (missing.Count > 0) sb.AppendLine("Faltam " + missing.Count + " livro(s): " + string.Join(", ", missing.Take(12).ToArray()) + (missing.Count > 12 ? "..." : ""));
            }
            foreach (int[] rf in new[] { new[] { 1, 1, 1 }, new[] { 19, 23, 1 }, new[] { 43, 3, 16 } })
            {
                Verse x = v.GetChapter(rf[0], rf[1]).FirstOrDefault(y => y.V == rf[2]);
                Book b;
                if (x != null && v.BooksById.TryGetValue(rf[0], out b)) sb.AppendLine(b.Name + " " + rf[1] + ":" + rf[2] + " — " + DataLoader.Clip(x.T));
            }
            return sb.ToString();
        }

        bool ShowImportPreview(IWin32Window owner, string path, ImportResult r)
        {
            using (Form f = new Form())
            {
                f.Text = "Importar versao - " + Path.GetFileName(path);
                f.StartPosition = FormStartPosition.CenterParent;
                f.Size = new Size(760, 560); f.MinimumSize = new Size(600, 460);
                f.Font = Font; f.ShowInTaskbar = false; f.MinimizeBox = false; f.MaximizeBox = false;
                TableLayoutPanel t = new TableLayoutPanel(); t.Dock = DockStyle.Fill; t.ColumnCount = 2; t.RowCount = 6; t.Padding = new Padding(10);
                t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                Func<string, Label> cap = s => { Label l = MakeLabel(s); l.AutoSize = true; l.Margin = new Padding(0, 7, 6, 0); return l; };
                Label fmt = MakeLabel(r.Format); fmt.AutoSize = true; fmt.Margin = new Padding(0, 7, 0, 0);
                t.Controls.Add(cap("Formato:"), 0, 0); t.Controls.Add(fmt, 1, 0);
                ComboBox which = MakeCombo(); which.Width = 420;
                foreach (BibleVersion v in r.Versions) which.Items.Add(v.Label + " (" + v.Verses.Count + " versiculos)");
                t.Controls.Add(cap(r.Versions.Count > 1 ? "Versao no arquivo:" : ""), 0, 1); t.Controls.Add(which, 1, 1);
                which.Visible = r.Versions.Count > 1;
                TextBox abbr = new TextBox(); abbr.Width = 120; abbr.MaxLength = 12;
                TextBox label = new TextBox(); label.Dock = DockStyle.Fill;
                t.Controls.Add(cap("Sigla:"), 0, 2); t.Controls.Add(abbr, 1, 2);
                t.Controls.Add(cap("Nome:"), 0, 3); t.Controls.Add(label, 1, 3);
                Label summary = MakeLabel(""); summary.AutoSize = true; summary.MaximumSize = new Size(590, 0); summary.Margin = new Padding(0, 8, 0, 6);
                t.Controls.Add(cap("Conteudo:"), 0, 4); t.Controls.Add(summary, 1, 4);
                TextBox problems = new TextBox(); problems.Multiline = true; problems.ReadOnly = true; problems.ScrollBars = ScrollBars.Both; problems.WordWrap = false;
                problems.Dock = DockStyle.Fill; problems.Font = new Font("Consolas", 9);
                problems.Text = r.ProblemCount == 0 ? "Nenhum problema encontrado." :
                    r.ProblemCount + " linha(s) ignorada(s)" + (r.ProblemCount > r.Problems.Count ? " (mostrando as " + r.Problems.Count + " primeiras)" : "") + ":\r\n" +
                    string.Join("\r\n", r.Problems.ToArray());
                t.Controls.Add(cap("Avisos:"), 0, 5); t.Controls.Add(problems, 1, 5);
                for (int i = 0; i < 5; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                FlowLayoutPanel bar = new FlowLayoutPanel(); bar.Dock = DockStyle.Bottom; bar.Height = 44; bar.FlowDirection = FlowDirection.RightToLeft; bar.Padding = new Padding(6);
                Button ok = MakeButton("Importar"); ok.Width = 100; ok.Height = 28;
                Button cancel = MakeButton("Cancelar"); cancel.Width = 100; cancel.Height = 28; cancel.DialogResult = DialogResult.Cancel;
                bar.Controls.Add(cancel); bar.Controls.Add(ok);
                f.Controls.Add(t); f.Controls.Add(bar);
                f.AcceptButton = ok; f.CancelButton = cancel;
                Func<BibleVersion> chosen = () => r.Versions[Math.Max(0, which.SelectedIndex)];
                which.SelectedIndexChanged += delegate
                {
                    BibleVersion v = chosen();
                    abbr.Text = v.Abbr; label.Text = v.Label; summary.Text = ImportSummary(v);
                };
                which.SelectedIndex = 0;
                bool done = false;
                ok.Click += delegate
                {
                    BibleVersion replace;
                    string err = ValidateImport(chosen(), abbr.Text, label.Text, out replace);
                    if (err != null) { MessageBox.Show(f, err, "Biblia Digital", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    if (replace != null && MessageBox.Show(f, "Ja existe a versao importada " + replace.Abbr + " - " + replace.Label + ". Substituir pela nova?",
                        "Biblia Digital", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    try
                    {
                        BibleVersion v = InstallVersion(chosen(), abbr.Text, label.Text, replace);
                        done = true;
                        MessageBox.Show(f, "Versao " + v.Abbr + " importada: " + v.Verses.Count.ToString("N0", new CultureInfo("pt-BR")) +
                            " versiculos.\r\nJa esta selecionada no Indice Digital.", "Biblia Digital", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        f.Close();
                    }
                    catch (Exception ex) { MessageBox.Show(f, "Nao foi possivel gravar a versao: " + ex.Message, "Biblia Digital"); }
                };
                f.ShowDialog(owner);
                return done;
            }
        }

        void ApplyFonts()
        {
            results.Font = new Font("Segoe UI", resultFontSize);
            SaveSettings();
            RefreshOpenChapter();
        }

        void ChangeFontSize(int delta)
        {
            int size = Math.Max(MinReaderSize, Math.Min(MaxReaderSize, readerFontSize + delta));
            if (size == readerFontSize) return;
            readerFontSize = size;
            resultFontSize = Math.Max(8, Math.Min(16, resultFontSize + delta));
            ApplyFonts();
        }

        void SetTheme(int index)
        {
            themeIndex = Math.Max(0, Math.Min(ReadingTheme.All.Length - 1, index));
            syncingUi = true;
            try { cmbBg.SelectedIndex = themeIndex; } finally { syncingUi = false; }
            SaveSettings();
        }

        void ResetAppearance()
        {
            readingMode = 0;
            readerFontName = "Georgia";
            readerFontSize = 12; resultFontSize = 9;
            justifyText = true; lineSpacingIndex = 1; columnIndex = 1;
            SetTheme(0);
            ApplyFonts();
        }

        static List<string> InstalledReaderFonts()
        {
            HashSet<string> installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (System.Drawing.Text.InstalledFontCollection fc = new System.Drawing.Text.InstalledFontCollection())
                    foreach (FontFamily f in fc.Families) installed.Add(f.Name);
            }
            catch { }
            List<string> list = CandidateFonts.Where(f => installed.Contains(f)).ToList();
            if (list.Count == 0) list.Add("Segoe UI");
            return list;
        }

        // Preferencias de leitura salvas em %LOCALAPPDATA%\BibliaDigital\preferencias.ini
        static string SettingsPath() { return Path.Combine(Program.AppDataDir(), "preferencias.ini"); }

        void LoadSettings()
        {
            try
            {
                string path = SettingsPath();
                if (!File.Exists(path)) return;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                    int n;
                    switch (key)
                    {
                        case "modo": readingMode = val == "livro" ? 3 : val == "paragrafos" ? 2 : val == "corrido" ? 1 : 0; break;
                        case "fonte": if (readerFonts.Contains(val)) readerFontName = val; break;
                        case "tamanho": if (int.TryParse(val, out n)) readerFontSize = Math.Max(MinReaderSize, Math.Min(MaxReaderSize, n)); break;
                        case "tamanho_lista": if (int.TryParse(val, out n)) resultFontSize = Math.Max(8, Math.Min(16, n)); break;
                        case "tema": themeIndex = ReadingTheme.IndexOf(val); break;
                        case "novas_abas": openInNewTab = val == "1"; break;
                        case "sugestoes": suggestSource = val == "sinonimos" ? 1 : val == "ambos" ? 2 : 0; break;
                        case "versao": savedVersionKey = val; break;
                        case "ultima": lastRead = val; break;
                        case "justificar": justifyText = val != "0"; break;
                        case "entrelinha": if (int.TryParse(val, out n)) lineSpacingIndex = Math.Max(0, Math.Min(LineSpacingRule.Length - 1, n)); break;
                        case "coluna": if (int.TryParse(val, out n)) columnIndex = Math.Max(0, Math.Min(ColumnChars.Length - 1, n)); break;
                    }
                }
            }
            catch { }
            if (!readerFonts.Contains(readerFontName)) readerFontName = readerFonts[0];
        }

        void SaveSettings()
        {
            if (NoSave) return;
            try
            {
                File.WriteAllText(SettingsPath(),
                    "modo=" + (readingMode == 3 ? "livro" : readingMode == 2 ? "paragrafos" : continuousText ? "corrido" : "versiculo") + "\r\n" +
                    "fonte=" + readerFontName + "\r\n" +
                    "tamanho=" + readerFontSize + "\r\n" +
                    "tamanho_lista=" + resultFontSize + "\r\n" +
                    "tema=" + ReadingTheme.All[themeIndex].Name + "\r\n" +
                    "novas_abas=" + (openInNewTab ? "1" : "0") + "\r\n" +
                    "sugestoes=" + (suggestSource == 1 ? "sinonimos" : suggestSource == 2 ? "ambos" : "analogico") + "\r\n" +
                    "versao=" + (current != null ? current.Key : "") + "\r\n" +
                    "ultima=" + lastRead + "\r\n" +
                    "justificar=" + (justifyText ? "1" : "0") + "\r\n" +
                    "entrelinha=" + lineSpacingIndex + "\r\n" +
                    "coluna=" + columnIndex + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        void FillBookCombos()
        {
            cmbAT.Items.Clear(); cmbNT.Items.Clear();
            foreach (Book b in current.Books)
            {
                if (b.Testament == "AT") cmbAT.Items.Add(b.Name); else cmbNT.Items.Add(b.Name);
            }
            if (cmbAT.Items.Count > 0) cmbAT.SelectedIndex = 0;
            if (cmbNT.Items.Count > 0) cmbNT.SelectedIndex = 0;
        }

        Book BookByName(object name)
        {
            Book b;
            if (name != null && current.BooksByName.TryGetValue(name.ToString(), out b)) return b;
            return null;
        }

        Book BookById(int id)
        {
            Book b;
            return current.BooksById.TryGetValue(id, out b) ? b : null;
        }

        void FillChapters(ComboBox bookCombo, ComboBox chapterCombo, ComboBox verseCombo)
        {
            chapterCombo.Items.Clear(); verseCombo.Items.Clear();
            Book book = BookByName(bookCombo.SelectedItem);
            if (book == null) return;
            for (int i = 1; i <= book.Chapters; i++) chapterCombo.Items.Add(i);
            if (chapterCombo.Items.Count > 0) chapterCombo.SelectedIndex = 0;
            FillVerses(bookCombo, chapterCombo, verseCombo);
        }

        void FillVerses(ComboBox bookCombo, ComboBox chapterCombo, ComboBox verseCombo)
        {
            verseCombo.Items.Clear();
            Book book = BookByName(bookCombo.SelectedItem);
            if (book == null || chapterCombo.SelectedItem == null) return;
            foreach (Verse v in current.GetChapter(book.Id, (int)chapterCombo.SelectedItem)) verseCombo.Items.Add(v.V);
            if (verseCombo.Items.Count > 0) verseCombo.SelectedIndex = 0;
        }

        void GoFromCombos(ComboBox bookCombo, ComboBox chapterCombo, ComboBox verseCombo)
        {
            Book book = BookByName(bookCombo.SelectedItem);
            if (book != null && chapterCombo.SelectedItem != null && verseCombo.SelectedItem != null)
                OpenReference(book.Id, (int)chapterCombo.SelectedItem, (int)verseCombo.SelectedItem);
        }

        void OpenFromItem(ListViewItem item, bool? newTab = null)
        {
            Verse v = item.Tag as Verse;
            if (v != null) OpenReference(v.B, v.C, v.V, lastSearchMarks, newTab);
        }

        ReadingTheme Theme { get { return ReadingTheme.All[themeIndex]; } }
        Color BackgroundColor() { return Theme.Back; }
        Color ForegroundColor() { return Theme.Fore; }
        Color AccentColor() { return Theme.Accent; }
        Color HighlightColor() { return Theme.Highlight; }
        Color DeskColor()
        {
            Color b = Theme.Back, f = Theme.Fore;
            return Color.FromArgb((b.R * 85 + f.R * 15) / 100, (b.G * 85 + f.G * 15) / 100, (b.B * 85 + f.B * 15) / 100);
        }

        // Setas e PageUp/PageDown viram folhas no modo livro, sem precisar clicar na pagina antes.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            PassageTab tab = tabs != null ? ActivePassage() : null;
            if (tab != null && readingMode == 3 && tab.BookLayout.Visible && !(ActiveControl is ComboBox) && !(ActiveControl is TextBox))
            {
                if (keyData == Keys.Right || keyData == Keys.PageDown) { ChangeBookSpread(tab, 1); return true; }
                if (keyData == Keys.Left || keyData == Keys.PageUp) { ChangeBookSpread(tab, -1); return true; }
                if (keyData == Keys.Home || keyData == Keys.End) { GoToSpread(tab, keyData == Keys.Home ? 0 : int.MaxValue); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        PassageTab ActivePassage()
        {
            foreach (PassageTab p in passageTabs) if (p.Page == tabs.SelectedTab) return p;
            return null;
        }

        // Redesenha todas as abas de passagem (apos mudar fonte, tamanho, tema ou modo).
        void RefreshOpenChapter()
        {
            foreach (PassageTab p in passageTabs) RenderPassage(p);
        }

        void ClosePassage(PassageTab tab)
        {
            int idx = tabs.TabPages.IndexOf(tab.Page);
            passageTabs.Remove(tab);
            if (tab.ResizeTimer != null) { tab.ResizeTimer.Stop(); tab.ResizeTimer.Dispose(); }
            if (lastPassage == tab) lastPassage = passageTabs.LastOrDefault();
            tabs.TabPages.Remove(tab.Page);
            tab.Page.Dispose();
            if (tabs.TabPages.Count > 0) tabs.SelectedIndex = Math.Max(0, Math.Min(idx, tabs.TabPages.Count - 1));
        }

        void CloseOtherPassages(PassageTab keep)
        {
            foreach (PassageTab p in passageTabs.ToList()) if (p != keep) ClosePassage(p);
            if (keep != null) tabs.SelectedTab = keep.Page;
            else tabs.SelectedTab = tabIndex;
        }

        // Abre uma passagem. marks: palavras da busca a destacar (null = destaca o versiculo inteiro).
        // newTab: null = segue a opcao "Abrir cada passagem em nova aba".
        void OpenReference(int bookId, int chapter, int verse, SearchMarks marks = null, bool? newTab = null)
        {
            if (!current.BooksById.ContainsKey(bookId)) return;
            PassageTab tab = null;
            if (!(newTab ?? openInNewTab))
                tab = ActivePassage() ?? (passageTabs.Contains(lastPassage) ? lastPassage : null);
            if (tab == null) tab = CreatePassageTab();
            tab.Version = current;
            tab.BookId = bookId; tab.Chapter = chapter; tab.Verse = verse; tab.Marks = marks;
            RenderPassage(tab);
            tabs.SelectedTab = tab.Page;
            lastPassage = tab;
            currentBookId = bookId; currentChapter = chapter; currentVerse = verse;
            NoteReading(tab);
        }

        // Guarda o ponto de leitura (para "Continuar de onde parei") e o historico.
        void NoteReading(PassageTab tab)
        {
            int v = Math.Max(1, tab.Verse);
            lastRead = tab.Version.Key + "|" + tab.BookId + "|" + tab.Chapter + "|" + v;
            UserData.AddHistory(tab.BookId, tab.Chapter, v, tab.Version.Key);
            SaveSettings();
            UpdateResumeButtons();
        }

        // Anda para o capitulo anterior/seguinte dentro da mesma aba.
        void StepChapter(PassageTab tab, int delta)
        {
            Book cur;
            if (!tab.Version.BooksById.TryGetValue(tab.BookId, out cur)) return;
            int newBook = tab.BookId, newChapter = tab.Chapter + delta;
            // Livro anterior/seguinte na lista da propria versao (versoes parciais podem pular livros).
            int pos = tab.Version.Books.IndexOf(cur);
            if (newChapter < 1)
            {
                if (pos <= 0) return;
                Book prev = tab.Version.Books[pos - 1];
                newBook = prev.Id; newChapter = prev.Chapters;
            }
            else if (newChapter > cur.Chapters)
            {
                if (pos < 0 || pos + 1 >= tab.Version.Books.Count) return;
                Book next = tab.Version.Books[pos + 1];
                newBook = next.Id; newChapter = 1;
            }
            tab.BookId = newBook; tab.Chapter = newChapter; tab.Verse = 0; tab.Marks = null;
            RenderPassage(tab);
            lastPassage = tab; currentBookId = newBook;
            NoteReading(tab);
        }

        PassageTab CreatePassageTab()
        {
            PassageTab tab = new PassageTab();
            tab.Page = new TabPage("...");
            tabs.TabPages.Add(tab.Page);
            passageTabs.Add(tab);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill; layout.RowCount = 3; layout.ColumnCount = 1;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tab.Page.Controls.Add(layout);

            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top; bar.FlowDirection = FlowDirection.LeftToRight;
            bar.AutoSize = true; bar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            bar.WrapContents = true; bar.Padding = new Padding(6);
            layout.Controls.Add(bar, 0, 0);

            Button btnBack = MakeButton("Voltar ao Indice Digital"); btnBack.Width = 165; btnBack.Height = 30;
            Button btnPrev = MakeButton("← Capitulo anterior"); btnPrev.Width = 145; btnPrev.Height = 30;
            Button btnNext = MakeButton("Proximo capitulo →"); btnNext.Width = 145; btnNext.Height = 30;
            Button btnClose = MakeButton("Fechar aba ✕"); btnClose.Width = 100; btnClose.Height = 30;
            tip.SetToolTip(btnClose, "Fecha esta passagem (tambem: clique do meio ou botao direito na aba)");
            tab.Title = MakeLabel("");
            tab.Title.Font = new Font("Segoe UI", 13, FontStyle.Bold);
            tab.Title.AutoSize = true;
            tab.Title.Margin = new Padding(18, 8, 0, 0);
            // O titulo e o seletor: clique abre a grade de livros, capitulos e versiculos.
            tab.Title.Cursor = Cursors.Hand;
            tip.SetToolTip(tab.Title, "Clique para escolher outro livro, capitulo ou versiculo");
            tab.Title.Click += delegate { ShowPicker(tab); };
            bar.Controls.Add(btnBack); bar.Controls.Add(btnPrev); bar.Controls.Add(btnNext); bar.Controls.Add(btnClose); bar.Controls.Add(tab.Title);

            // Barra de aparencia: modo de leitura, fonte, tamanho e tema de cor (valem para todas as abas).
            FlowLayoutPanel look = new FlowLayoutPanel();
            look.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top; look.FlowDirection = FlowDirection.LeftToRight;
            look.AutoSize = true; look.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            look.WrapContents = true; look.Padding = new Padding(6, 2, 6, 2);
            layout.Controls.Add(look, 0, 1);
            Func<string, Label> barLabel = text =>
            {
                Label l = MakeLabel(text); l.AutoSize = true; l.Margin = new Padding(10, 7, 2, 0); return l;
            };

            tab.Mode = MakeCombo(); tab.Mode.Width = 185;
            tab.Mode.Items.AddRange(new object[] { "Versiculo por versiculo", "Texto corrido (carta)", "Leitura em paragrafos", "Livro aberto (duas paginas)" });

            tab.FontBox = MakeCombo(); tab.FontBox.Width = 150;
            foreach (string f in readerFonts) tab.FontBox.Items.Add(f);

            Button btnSmaller = MakeButton("A-"); btnSmaller.Width = 34; btnSmaller.Height = 26;
            tab.Size = MakeLabel(""); tab.Size.AutoSize = false; tab.Size.Width = 44;
            tab.Size.TextAlign = ContentAlignment.MiddleCenter; tab.Size.Height = 26;
            Button btnBigger = MakeButton("A+"); btnBigger.Width = 34; btnBigger.Height = 26;

            tab.ThemeBox = MakeCombo(); tab.ThemeBox.Width = 125;
            foreach (ReadingTheme t in ReadingTheme.All) tab.ThemeBox.Items.Add(t.Name);

            Button btnReset = MakeButton("Padrao"); btnReset.Width = 65; btnReset.Height = 26;
            btnReset.Margin = new Padding(14, 3, 0, 0);
            tip.SetToolTip(btnReset, "Volta para Georgia 12, tema Branco, versiculo por versiculo");

            look.Controls.Add(barLabel("Leitura:")); look.Controls.Add(tab.Mode);
            look.Controls.Add(barLabel("Fonte:")); look.Controls.Add(tab.FontBox);
            look.Controls.Add(barLabel("Tamanho:")); look.Controls.Add(btnSmaller); look.Controls.Add(tab.Size); look.Controls.Add(btnBigger);
            look.Controls.Add(barLabel("Tema:")); look.Controls.Add(tab.ThemeBox);
            Button btnFormat = MakeButton("Formato ▾"); btnFormat.Width = 82; btnFormat.Height = 26;
            btnFormat.Margin = new Padding(10, 3, 0, 0);
            tip.SetToolTip(btnFormat, "Justificar, entrelinha e largura da coluna de texto");
            btnFormat.Click += delegate { FormatMenu().Show(btnFormat, new Point(0, btnFormat.Height)); };
            look.Controls.Add(btnFormat);
            look.Controls.Add(btnReset);

            tab.Reader = new RichTextBox();
            tab.Reader.Dock = DockStyle.Fill;
            tab.Reader.ReadOnly = true;
            tab.Reader.BorderStyle = BorderStyle.None;
            tab.Reader.Margin = new Padding(0);
            tab.Reader.WordWrap = true;
            tab.Reader.HideSelection = true;
            tab.ReadingHost = new Panel(); tab.ReadingHost.Dock = DockStyle.Fill;
            tab.ReadingHost.Padding = new Padding(20, 12, 20, 12);
            layout.Controls.Add(tab.ReadingHost, 0, 2);
            tab.ReadingHost.Controls.Add(tab.Reader);
            tab.ReadingHost.Resize += delegate { ApplyColumn(tab); };
            tab.BookLayout = new TableLayoutPanel();
            tab.BookLayout.Dock = DockStyle.Fill; tab.BookLayout.ColumnCount = 2; tab.BookLayout.RowCount = 2;
            tab.BookLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            tab.BookLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            tab.BookLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tab.BookLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            tab.LeftPage = MakeBookPage(); tab.RightPage = MakeBookPage();
            // Cada pagina e uma folha: margem externa larga, margem interna junto a lombada,
            // cabecalho corrido em cima e numero da pagina embaixo, como numa Biblia impressa.
            tab.LeftPaper = MakePaper(tab.LeftPage, true, out tab.LeftHead, out tab.LeftFolio);
            tab.RightPaper = MakePaper(tab.RightPage, false, out tab.RightHead, out tab.RightFolio);
            tab.LeftPaper.Margin = new Padding(18, 10, 0, 6);
            tab.RightPaper.Margin = new Padding(0, 10, 18, 6);
            tab.BookLayout.Controls.Add(tab.LeftPaper, 0, 0);
            tab.BookLayout.Controls.Add(tab.RightPaper, 1, 0);
            tab.BookLayout.Paint += (s, e) => PaintDesk(tab, e.Graphics);
            FlowLayoutPanel pageNav = new FlowLayoutPanel();
            pageNav.Anchor = AnchorStyles.None; pageNav.AutoSize = true; pageNav.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            pageNav.FlowDirection = FlowDirection.LeftToRight; pageNav.WrapContents = false; pageNav.Padding = new Padding(4);
            tab.PrevPage = MakeButton("◀ Folhas anteriores"); tab.PrevPage.Width = 160;
            tab.NextPage = MakeButton("Proximas folhas ▶"); tab.NextPage.Width = 160;
            tab.PageCounter = MakeLabel(""); tab.PageCounter.AutoSize = true;
            tab.PageCounter.Margin = new Padding(14, 7, 14, 0);
            pageNav.Controls.Add(tab.PrevPage); pageNav.Controls.Add(tab.PageCounter); pageNav.Controls.Add(tab.NextPage);
            tab.BookLayout.Controls.Add(pageNav, 0, 1); tab.BookLayout.SetColumnSpan(pageNav, 2);
            tip.SetToolTip(tab.PrevPage, "Setas, PageUp/PageDown, Home/End ou a roda do mouse tambem viram as folhas");
            tip.SetToolTip(tab.NextPage, "No fim do capitulo, segue para o proximo, como num livro");
            tab.ReadingHost.Controls.Add(tab.BookLayout);
            tab.BookLayout.Visible = false;
            tab.PrevPage.Click += delegate { ChangeBookSpread(tab, -1); };
            tab.NextPage.Click += delegate { ChangeBookSpread(tab, +1); };
            tab.ResizeTimer = new Timer(); tab.ResizeTimer.Interval = 200;
            tab.ResizeTimer.Tick += delegate
            {
                tab.ResizeTimer.Stop();
                if (!(readingMode == 3 && tab.BookLayout.Visible && tab.BookLayout.Width > 180 && tab.BookLayout.Height > 160)) return;
                // Continua mostrando o mesmo trecho: o versiculo do alto da folha esquerda segue nas folhas abertas.
                int p0 = tab.SpreadIndex * 2, anchor = p0 < tab.PageFirstVerse.Count ? tab.PageFirstVerse[p0] : 0;
                RenderBook(tab, true);
                int page = 0;
                for (int i = 0; i < tab.PageFirstVerse.Count; i++) if (tab.PageFirstVerse[i] <= anchor) page = i;
                if (anchor > 0 && page / 2 != tab.SpreadIndex) GoToSpread(tab, page / 2);
            };
            // Repagina quando a folha muda de tamanho (janela redimensionada ou aba que acabou de ganhar o tamanho final).
            tab.LeftPage.SizeChanged += delegate
            {
                if (readingMode == 3 && tab.LeftPage.ClientSize != tab.PagedSize) { tab.ResizeTimer.Stop(); tab.ResizeTimer.Start(); }
            };
            tab.LeftPage.KeyDown += (o, e) => BookKeys(tab, e);
            tab.RightPage.KeyDown += (o, e) => BookKeys(tab, e);
            // Roda do mouse vira a folha (a pagina nao tem barra de rolagem).
            foreach (Control c in new Control[] { tab.LeftPage, tab.RightPage, tab.LeftPaper, tab.RightPaper })
                c.MouseWheel += (o, e) =>
                {
                    if (e.Delta == 0 || unchecked(Environment.TickCount - tab.LastWheel) < 250) return;
                    tab.LastWheel = Environment.TickCount;
                    ChangeBookSpread(tab, e.Delta < 0 ? 1 : -1);
                };
            foreach (RichTextBox box in new[] { tab.Reader, tab.LeftPage, tab.RightPage }) WireStudyEvents(tab, box);

            tab.Mode.SelectedIndexChanged += delegate
            {
                if (syncingUi) return;
                readingMode = tab.Mode.SelectedIndex;
                SaveSettings();
                RefreshOpenChapter();
            };
            tab.FontBox.SelectedIndexChanged += delegate
            {
                if (syncingUi || tab.FontBox.SelectedItem == null) return;
                readerFontName = tab.FontBox.SelectedItem.ToString();
                SaveSettings();
                RefreshOpenChapter();
            };
            tab.ThemeBox.SelectedIndexChanged += delegate
            {
                if (syncingUi) return;
                SetTheme(tab.ThemeBox.SelectedIndex);
                RefreshOpenChapter();
            };
            btnSmaller.Click += delegate { ChangeFontSize(-1); };
            btnBigger.Click += delegate { ChangeFontSize(+1); };
            btnReset.Click += delegate { ResetAppearance(); };
            btnBack.Click += delegate { tabs.SelectedTab = tabIndex; };
            btnPrev.Click += delegate { StepChapter(tab, -1); };
            btnNext.Click += delegate { StepChapter(tab, +1); };
            // A aba e descartada depois que o clique termina (o botao fica dentro dela).
            btnClose.Click += delegate { BeginInvoke((Action)(() => ClosePassage(tab))); };
            return tab;
        }

        // Leitura tipo livro: texto original inalterado; quebras editoriais apenas na exibicao.
        RichTextBox MakeBookPage()
        {
            RichTextBox page = new RichTextBox();
            page.Dock = DockStyle.Fill; page.ReadOnly = true; page.HideSelection = true;
            page.BorderStyle = BorderStyle.None; page.BackColor = BackgroundColor();
            page.ForeColor = ForegroundColor();
            // Pagina de livro nao rola: a paginacao mede a altura real da linha na propria caixa.
            page.ScrollBars = RichTextBoxScrollBars.None;
            return page;
        }

        const int PaperOuter = 44, PaperInner = 30, GutterShade = 22;

        Panel MakePaper(RichTextBox page, bool left, out Label head, out Label folio)
        {
            Panel paper = new Panel();
            paper.Dock = DockStyle.Fill;
            paper.Padding = left ? new Padding(PaperOuter, 6, PaperInner, 4) : new Padding(PaperInner, 6, PaperOuter, 4);
            head = MakeLabel(""); head.Dock = DockStyle.Top; head.Height = 30; head.AutoSize = false;
            head.TextAlign = left ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleRight;
            folio = MakeLabel(""); folio.Dock = DockStyle.Bottom; folio.Height = 26; folio.AutoSize = false;
            folio.TextAlign = left ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleRight;
            paper.Controls.Add(page); paper.Controls.Add(folio); paper.Controls.Add(head);
            // Sombra da lombada: escurece a margem interna, como a dobra de um livro aberto.
            paper.Paint += (s, e) =>
            {
                Rectangle r = left ? new Rectangle(paper.Width - GutterShade, 0, GutterShade, paper.Height) : new Rectangle(0, 0, GutterShade, paper.Height);
                if (r.Width <= 0 || r.Height <= 0) return;
                int a = Theme.Dark ? 70 : 38;
                using (System.Drawing.Drawing2D.LinearGradientBrush br = new System.Drawing.Drawing2D.LinearGradientBrush(r,
                    left ? Color.FromArgb(0, 0, 0, 0) : Color.FromArgb(a, 0, 0, 0), left ? Color.FromArgb(a, 0, 0, 0) : Color.FromArgb(0, 0, 0, 0),
                    System.Drawing.Drawing2D.LinearGradientMode.Horizontal))
                    e.Graphics.FillRectangle(br, r);
            };
            return paper;
        }

        // Mesa em volta do livro: sombra sob as folhas.
        void PaintDesk(PassageTab tab, Graphics g)
        {
            if (tab.LeftPaper == null) return;
            Rectangle book = Rectangle.Union(tab.LeftPaper.Bounds, tab.RightPaper.Bounds);
            Color d = DeskColor();
            for (int i = 4; i >= 1; i--) // de fora para dentro: mais escura junto da folha
                using (SolidBrush br = new SolidBrush(Color.FromArgb(Math.Max(0, d.R - 9 * (5 - i)), Math.Max(0, d.G - 9 * (5 - i)), Math.Max(0, d.B - 9 * (5 - i)))))
                    g.FillRectangle(br, book.X + i, book.Y + i, book.Width, book.Height);
        }

        // Cor discreta para cabecalho e numero da pagina (meio-termo entre texto e papel).
        Color FaintColor()
        {
            Color b = Theme.Back, f = Theme.Fore;
            return Color.FromArgb((b.R + f.R * 2) / 3, (b.G + f.G * 2) / 3, (b.B + f.B * 2) / 3);
        }

        // ---- Estudos no texto aberto: menu do botao direito, dica da anotacao e Ctrl+C com referencia ----

        void WireStudyEvents(PassageTab tab, RichTextBox box)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            box.ContextMenuStrip = menu;
            box.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                int idx = box.GetCharIndexFromPosition(e.Location);
                // Clique fora de uma selecao: o menu vale para o versiculo clicado.
                if (box.SelectionLength == 0 || idx < box.SelectionStart || idx > box.SelectionStart + box.SelectionLength) box.Select(idx, 0);
                tab.MenuVerse = VerseAt(tab, box, idx);
                tab.MenuBox = box;
            };
            menu.Opening += (s, e) =>
            {
                menu.Items.Clear();
                if (tab.MenuBox != box || tab.MenuVerse <= 0) { e.Cancel = true; return; }
                BuildVerseMenu(tab, box, menu);
            };
            box.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C && box.SelectionLength > 0)
                {
                    SetClipboard(CopyText(tab, box, box.SelectionStart, box.SelectionStart + box.SelectionLength));
                    e.Handled = true; e.SuppressKeyPress = true;
                }
            };
            box.MouseMove += (s, e) =>
            {
                int v = VerseAt(tab, box, box.GetCharIndexFromPosition(e.Location));
                if (v == tab.TipVerse) return;
                tab.TipVerse = v;
                string note = v > 0 ? UserData.Get(UserData.Notes, tab.BookId, tab.Chapter, v) : null;
                tip.SetToolTip(box, note != null ? "Anotacao (" + Reference(tab, v, v) + "):\r\n" + note : "");
            };
        }

        void BuildVerseMenu(PassageTab tab, RichTextBox box, ContextMenuStrip menu)
        {
            int v = tab.MenuVerse, b = tab.BookId, c = tab.Chapter;
            ToolStripMenuItem head = new ToolStripMenuItem(Reference(tab, v, v)); head.Enabled = false;
            menu.Items.Add(head);
            menu.Items.Add(new ToolStripSeparator());
            bool sel = box.SelectionLength > 0;
            menu.Items.Add(sel ? "Copiar selecao com referencia\tCtrl+C" : "Copiar versiculo com referencia", null, delegate
            {
                SetClipboard(sel ? CopyText(tab, box, box.SelectionStart, box.SelectionStart + box.SelectionLength) : CopyVerses(tab, v, v));
            });
            ToolStripMenuItem marks = new ToolStripMenuItem("Marca-texto");
            string curMark = UserData.Get(UserData.Marks, b, c, v);
            for (int i = 0; i < UserData.MarkNames.Length; i++)
            {
                string name = UserData.MarkNames[i];
                ToolStripMenuItem it = new ToolStripMenuItem(UserData.MarkLabels[i], MarkSwatch(name), delegate { UserData.SetMark(b, c, v, name); StudiesChanged(); });
                it.Checked = curMark == name;
                marks.DropDownItems.Add(it);
            }
            ToolStripMenuItem none = new ToolStripMenuItem("Sem marca", null, delegate { UserData.SetMark(b, c, v, null); StudiesChanged(); });
            none.Enabled = curMark != null;
            marks.DropDownItems.Add(none);
            menu.Items.Add(marks);
            string note = UserData.Get(UserData.Notes, b, c, v);
            menu.Items.Add(note == null ? "Escrever anotacao..." : "Editar anotacao...", null, delegate { EditNote(tab, v); });
            bool fav = UserData.Get(UserData.Favorites, b, c, v) != null;
            menu.Items.Add(fav ? "Remover dos favoritos" : "Adicionar aos favoritos ★", null, delegate { UserData.SetFavorite(b, c, v, !fav); StudiesChanged(); });
        }

        static Image MarkSwatch(string name)
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            using (SolidBrush br = new SolidBrush(UserData.MarkColor(name, Color.White)))
            {
                g.FillRectangle(br, 1, 1, 14, 14);
                g.DrawRectangle(Pens.Gray, 1, 1, 13, 13);
            }
            return bmp;
        }

        void SetClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); } catch { }
        }

        // Versiculo na posicao 'index' de uma caixa de leitura (0 = titulo ou fora do texto).
        int VerseAt(PassageTab tab, RichTextBox box, int index)
        {
            if (box == tab.Reader)
            {
                int found = 0;
                foreach (int[] r in tab.VerseRanges) { if (r[0] <= index) found = r[2]; else break; }
                return found;
            }
            int page = tab.SpreadIndex * 2 + (box == tab.RightPage ? 1 : 0);
            int verse = page < tab.PageFirstVerse.Count ? tab.PageFirstVerse[page] : 0;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(box.Text, @"(?m)^(\d+)  "))
            {
                if (m.Index > index) break;
                verse = int.Parse(m.Groups[1].Value);
            }
            return verse;
        }

        string Reference(PassageTab tab, int v1, int v2)
        {
            Book book;
            string name = tab.Version.BooksById.TryGetValue(tab.BookId, out book) ? book.Name : "?";
            return name + " " + tab.Chapter + ":" + v1 + (v2 > v1 ? "-" + v2 : "") + " (" + tab.Version.Abbr + ")";
        }

        string CopyVerses(PassageTab tab, int v1, int v2)
        {
            List<Verse> list = tab.Version.GetChapter(tab.BookId, tab.Chapter).Where(x => x.V >= v1 && x.V <= v2).ToList();
            if (list.Count == 0) return "";
            string text = list.Count == 1 ? list[0].T : string.Join(" ", list.Select(x => x.V + " " + x.T).ToArray());
            return text + "\r\n" + Reference(tab, v1, v2);
        }

        // Texto selecionado + referencia. Selecao dentro de um versiculo: copia so o trecho;
        // selecao que passa por varios: copia os versiculos inteiros, numerados.
        string CopyText(PassageTab tab, RichTextBox box, int start, int end)
        {
            int v1 = VerseAt(tab, box, start), v2 = VerseAt(tab, box, Math.Max(start, end - 1));
            if (v1 <= 0) v1 = v2;
            if (v2 <= 0) v2 = v1;
            if (v1 <= 0) return "";
            if (v2 < v1) { int t = v1; v1 = v2; v2 = t; }
            if (v1 != v2 || end <= start) return CopyVerses(tab, v1, v2);
            string text = box.Text.Substring(start, Math.Min(end, box.TextLength) - start).Replace("✎", " ").Replace("★", " ");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
            text = System.Text.RegularExpressions.Regex.Replace(text, @"^" + v1 + @"\s+", "");
            if (text.Length == 0) return CopyVerses(tab, v1, v1);
            return text + "\r\n" + Reference(tab, v1, v1);
        }

        void EditNote(PassageTab tab, int v)
        {
            using (Form f = new Form())
            {
                f.Text = "Anotacao - " + Reference(tab, v, v);
                f.StartPosition = FormStartPosition.CenterParent;
                f.Size = new Size(560, 380); f.MinimumSize = new Size(400, 260);
                f.Font = Font; f.ShowInTaskbar = false; f.MinimizeBox = false; f.MaximizeBox = false;
                Verse verse = tab.Version.GetChapter(tab.BookId, tab.Chapter).FirstOrDefault(x => x.V == v);
                Label lbl = new Label(); lbl.Dock = DockStyle.Top; lbl.Height = 54; lbl.Padding = new Padding(8, 6, 8, 0);
                lbl.Text = verse != null ? verse.T : ""; lbl.AutoEllipsis = true;
                TextBox txt = new TextBox(); txt.Multiline = true; txt.ScrollBars = ScrollBars.Vertical; txt.Dock = DockStyle.Fill;
                txt.AcceptsReturn = true; txt.Font = new Font("Segoe UI", 10);
                txt.Text = UserData.Get(UserData.Notes, tab.BookId, tab.Chapter, v) ?? "";
                FlowLayoutPanel btns = new FlowLayoutPanel(); btns.Dock = DockStyle.Bottom; btns.Height = 40;
                btns.FlowDirection = FlowDirection.RightToLeft; btns.Padding = new Padding(6);
                Button ok = MakeButton("Salvar"); ok.DialogResult = DialogResult.OK;
                Button cancel = MakeButton("Cancelar"); cancel.DialogResult = DialogResult.Cancel;
                Button del = MakeButton("Apagar"); del.DialogResult = DialogResult.Abort;
                del.Enabled = txt.Text.Length > 0;
                btns.Controls.Add(ok); btns.Controls.Add(cancel); btns.Controls.Add(del);
                Panel host = new Panel(); host.Dock = DockStyle.Fill; host.Padding = new Padding(8, 4, 8, 4);
                host.Controls.Add(txt);
                f.Controls.Add(host); f.Controls.Add(btns); f.Controls.Add(lbl);
                f.CancelButton = cancel;
                DialogResult r = f.ShowDialog(this);
                if (r == DialogResult.OK) UserData.SetNote(tab.BookId, tab.Chapter, v, txt.Text);
                else if (r == DialogResult.Abort) UserData.SetNote(tab.BookId, tab.Chapter, v, null);
                else return;
                StudiesChanged();
            }
        }

        // Depois de marcar, anotar ou favoritar: redesenha as abas abertas e as listas.
        void StudiesChanged()
        {
            foreach (PassageTab p in passageTabs)
            {
                if (p.BookLayout.Visible) { int keep = p.SpreadIndex; RenderBook(p, true); p.SpreadIndex = keep; DisplaySpread(p); }
                else RenderPassage(p);
            }
            RefreshStudies();
        }

        // ---- Aba "Meus estudos" e "Continuar de onde parei" ----

        void BuildStudiesTab()
        {
            TableLayoutPanel lay = new TableLayoutPanel();
            lay.Dock = DockStyle.Fill; lay.RowCount = 4; lay.ColumnCount = 1;
            lay.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            lay.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            lay.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            lay.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tabStudies.Controls.Add(lay);

            btnResume2 = MakeButton(""); btnResume2.Dock = DockStyle.Fill; btnResume2.Height = 34;
            btnResume2.TextAlign = ContentAlignment.MiddleLeft; btnResume2.Margin = new Padding(8, 8, 8, 4);
            btnResume2.Click += delegate { ResumeReading(); };
            lay.Controls.Add(btnResume2, 0, 0);
            Label how = MakeLabel("No texto aberto, clique com o botao direito sobre um versiculo para marcar com cor, escrever anotacao, " +
                "favoritar ou copiar com a referencia (Ctrl+C tambem copia a selecao com a referencia). Passe o mouse sobre ✎ para ler a anotacao.");
            how.AutoSize = true; how.MaximumSize = new Size(1100, 0); how.Margin = new Padding(10, 2, 10, 4);
            lay.Controls.Add(how, 0, 1);

            TabControl inner = new TabControl(); inner.Dock = DockStyle.Fill; inner.Margin = new Padding(8, 4, 8, 4);
            lay.Controls.Add(inner, 0, 2);
            Func<string, string[], int[], ListView> page = (title, cols, widths) =>
            {
                TabPage tp = new TabPage(title); inner.TabPages.Add(tp);
                ListView lv = new ListView(); lv.Dock = DockStyle.Fill; lv.View = View.Details;
                lv.FullRowSelect = true; lv.GridLines = true; lv.MultiSelect = false; lv.HideSelection = false;
                for (int i = 0; i < cols.Length; i++) lv.Columns.Add(cols[i], widths[i]);
                lv.DoubleClick += delegate { OpenStudyItem(lv); };
                lv.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) OpenStudyItem(lv); else if (e.KeyCode == Keys.Delete) RemoveStudyItem(lv); };
                tp.Controls.Add(lv);
                return lv;
            };
            lvFav = page("Favoritos", new[] { "Referencia", "Texto", "Adicionado em" }, new[] { 150, 640, 130 });
            lvNotes = page("Anotacoes", new[] { "Referencia", "Anotacao", "Texto do versiculo" }, new[] { 150, 380, 420 });
            lvMarks = page("Marca-texto", new[] { "Referencia", "Cor", "Texto" }, new[] { 150, 80, 690 });
            lvHist = page("Historico", new[] { "Capitulo", "Versao", "Lido em" }, new[] { 200, 120, 140 });

            FlowLayoutPanel bar = new FlowLayoutPanel(); bar.Dock = DockStyle.Fill; bar.AutoSize = true;
            bar.Padding = new Padding(6); bar.FlowDirection = FlowDirection.LeftToRight;
            lay.Controls.Add(bar, 0, 3);
            Func<ListView> currentList = () => (ListView)inner.SelectedTab.Controls[0];
            Button bOpen = MakeButton("Abrir"); bOpen.Width = 90; bOpen.Click += delegate { OpenStudyItem(currentList()); };
            Button bDel = MakeButton("Remover"); bDel.Width = 90; bDel.Click += delegate { RemoveStudyItem(currentList()); };
            Button bExport = MakeButton("Exportar lista..."); bExport.Width = 120; bExport.Click += delegate { ExportStudyList(currentList(), inner.SelectedTab.Text); };
            Button bClearHist = MakeButton("Limpar historico"); bClearHist.Width = 120;
            bClearHist.Click += delegate
            {
                if (MessageBox.Show(this, "Apagar todo o historico de leitura?", "Biblia Digital", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                { UserData.ClearHistory(); RefreshStudies(); }
            };
            Label info = MakeLabel("Os estudos ficam em %LOCALAPPDATA%\\BibliaDigital (notas.txt, marcas.txt, favoritos.txt, historico.txt), com copia .bak a cada gravacao.");
            info.AutoSize = true; info.Margin = new Padding(14, 8, 0, 0); info.ForeColor = SystemColors.GrayText;
            bar.Controls.Add(bOpen); bar.Controls.Add(bDel); bar.Controls.Add(bExport); bar.Controls.Add(bClearHist); bar.Controls.Add(info);
        }

        string BookName(int id)
        {
            Book b;
            return current.BooksById.TryGetValue(id, out b) ? b.Name : "Livro " + id;
        }

        string VerseText(int b, int c, int v)
        {
            Verse x = current.GetChapter(b, c).FirstOrDefault(y => y.V == v);
            return x != null ? x.T : "";
        }

        void RefreshStudies()
        {
            if (lvFav == null) return;
            Action<ListView, IEnumerable<ListViewItem>> fill = (lv, items) =>
            {
                lv.BeginUpdate(); lv.Items.Clear(); lv.Items.AddRange(items.ToArray()); lv.EndUpdate();
            };
            Func<long, string[], ListViewItem> item = (k, cols) =>
            {
                ListViewItem it = new ListViewItem(cols); it.Tag = k; return it;
            };
            Func<long, string> refOf = k => { int[] r = UserData.Split(k); return BookName(r[0]) + " " + r[1] + ":" + r[2]; };
            fill(lvFav, UserData.Favorites.OrderBy(x => x.Key).Select(x =>
            { int[] r = UserData.Split(x.Key); return item(x.Key, new[] { refOf(x.Key), VerseText(r[0], r[1], r[2]), x.Value }); }));
            fill(lvNotes, UserData.Notes.OrderBy(x => x.Key).Select(x =>
            { int[] r = UserData.Split(x.Key); return item(x.Key, new[] { refOf(x.Key), x.Value.Replace("\r\n", " / "), VerseText(r[0], r[1], r[2]) }); }));
            fill(lvMarks, UserData.Marks.OrderBy(x => x.Key).Select(x =>
            {
                int[] r = UserData.Split(x.Key);
                int i = Array.IndexOf(UserData.MarkNames, x.Value);
                ListViewItem it = item(x.Key, new[] { refOf(x.Key), i >= 0 ? UserData.MarkLabels[i] : x.Value, VerseText(r[0], r[1], r[2]) });
                it.UseItemStyleForSubItems = false; it.SubItems[1].BackColor = UserData.MarkColor(x.Value, Color.White);
                return it;
            }));
            fill(lvHist, UserData.History.Select(h =>
            {
                int b, c, v; int.TryParse(h[0], out b); int.TryParse(h[1], out c); int.TryParse(h[2], out v);
                BibleVersion hv = versions.FirstOrDefault(x => x.Key == h[3]);
                ListViewItem it = new ListViewItem(new[] { BookName(b) + " " + c, hv != null ? hv.Abbr : h[3], h[4] });
                it.Tag = h; return it;
            }));
        }

        void OpenStudyItem(ListView lv)
        {
            if (lv.SelectedItems.Count == 0) return;
            object tag = lv.SelectedItems[0].Tag;
            if (tag is long) { int[] r = UserData.Split((long)tag); OpenReference(r[0], r[1], r[2]); return; }
            string[] h = tag as string[];
            if (h != null) { int b, c, v; int.TryParse(h[0], out b); int.TryParse(h[1], out c); int.TryParse(h[2], out v); OpenIn(h[3], b, c, v); }
        }

        void RemoveStudyItem(ListView lv)
        {
            if (lv.SelectedItems.Count == 0 || !(lv.SelectedItems[0].Tag is long)) return;
            int[] r = UserData.Split((long)lv.SelectedItems[0].Tag);
            if (lv == lvFav) UserData.SetFavorite(r[0], r[1], r[2], false);
            else if (lv == lvMarks) UserData.SetMark(r[0], r[1], r[2], null);
            else if (lv == lvNotes)
            {
                if (MessageBox.Show(this, "Apagar a anotacao de " + lv.SelectedItems[0].Text + "?", "Biblia Digital", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                UserData.SetNote(r[0], r[1], r[2], null);
            }
            StudiesChanged();
        }

        void ExportStudyList(ListView lv, string title)
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "Texto (*.txt)|*.txt"; d.FileName = "Biblia Digital - " + title + ".txt";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                StringBuilder sb = new StringBuilder("Biblia Digital - " + title + " (" + current.Abbr + ")\r\n\r\n");
                foreach (ListViewItem it in lv.Items)
                    sb.Append(string.Join(" | ", it.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text).ToArray()) + "\r\n");
                try { File.WriteAllText(d.FileName, sb.ToString(), Encoding.UTF8); }
                catch (Exception ex) { MessageBox.Show(this, "Nao foi possivel gravar: " + ex.Message, "Biblia Digital"); }
            }
        }

        // Abre na versao indicada (se existir), senao na versao atual.
        void OpenIn(string versionKey, int b, int c, int v)
        {
            int i = versions.FindIndex(x => x.Key == versionKey);
            if (i >= 0 && versions[i] != current) cmbVersion.SelectedIndex = i;
            OpenReference(b, c, v);
        }

        void ResumeReading()
        {
            string[] p = lastRead.Split('|');
            int b, c, v;
            if (p.Length < 4 || !int.TryParse(p[1], out b) || !int.TryParse(p[2], out c) || !int.TryParse(p[3], out v)) return;
            OpenIn(p[0], b, c, v);
        }

        void UpdateResumeButtons()
        {
            if (btnResume == null) return;
            string[] p = lastRead.Split('|');
            int b, c, v;
            bool ok = p.Length >= 4 && int.TryParse(p[1], out b) && int.TryParse(p[2], out c) && int.TryParse(p[3], out v);
            string text = "";
            if (ok)
            {
                BibleVersion lv = versions.FirstOrDefault(x => x.Key == p[0]) ?? current;
                Book bk;
                text = "▶  Continuar de onde parei: " + (lv.BooksById.TryGetValue(int.Parse(p[1]), out bk) ? bk.Name : "?") + " " + p[2] + ":" + p[3] + " (" + lv.Abbr + ")";
            }
            foreach (Button btn in new[] { btnResume, btnResume2 })
            {
                if (btn == null) continue;
                btn.Text = ok ? text : "Nenhuma leitura recente";
                btn.Enabled = ok;
            }
            btnResume.Visible = ok;
        }

        void BookKeys(PassageTab tab, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Right || e.KeyCode == Keys.PageDown) { ChangeBookSpread(tab, 1); e.Handled = true; }
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.PageUp) { ChangeBookSpread(tab, -1); e.Handled = true; }
            if (e.KeyCode == Keys.Home || e.KeyCode == Keys.End) { GoToSpread(tab, e.KeyCode == Keys.Home ? 0 : int.MaxValue); e.Handled = true; }
        }

        int MaxSpread(PassageTab tab) { return Math.Max(0, (tab.BookPages.Count - 1) / 2); }

        void GoToSpread(PassageTab tab, int spread)
        {
            tab.SpreadIndex = Math.Max(0, Math.Min(MaxSpread(tab), spread));
            DisplaySpread(tab);
        }

        // Existe capitulo anterior/seguinte nesta versao? (para os botoes no inicio/fim do capitulo)
        bool CanStepChapter(PassageTab tab, int delta)
        {
            Book cur;
            if (tab.Version == null || !tab.Version.BooksById.TryGetValue(tab.BookId, out cur)) return false;
            int pos = tab.Version.Books.IndexOf(cur), c = tab.Chapter + delta;
            if (c >= 1 && c <= cur.Chapters) return true;
            return delta < 0 ? pos > 0 : pos >= 0 && pos + 1 < tab.Version.Books.Count;
        }

        // Como num livro: passar da ultima folha abre o proximo capitulo; voltar da primeira abre o fim do anterior.
        void ChangeBookSpread(PassageTab tab, int delta)
        {
            int want = tab.SpreadIndex + delta;
            if (want >= 0 && want <= MaxSpread(tab)) { tab.SpreadIndex = want; DisplaySpread(tab); return; }
            int b = tab.BookId, c = tab.Chapter;
            StepChapter(tab, want < 0 ? -1 : 1);
            if (tab.BookId == b && tab.Chapter == c) return;
            if (want < 0) GoToSpread(tab, int.MaxValue);
        }

        void DisplaySpread(PassageTab tab)
        {
            int left = tab.SpreadIndex * 2, count = tab.BookPages.Count;
            bool first = tab.SpreadIndex == 0, last = left + 2 >= count;
            SetPageText(tab.LeftPage, left < count ? tab.BookPages[left].Replace("\r\n", "\n") : "");
            SetPageText(tab.RightPage, left + 1 < count ? tab.BookPages[left + 1].Replace("\r\n", "\n") : "");
            tab.PageCounter.Text = "Paginas " + (left + 1) + (left + 1 < count ? "–" + (left + 2) : "") + " de " + count;
            tab.PrevPage.Text = first ? "◀ Capitulo anterior" : "◀ Folhas anteriores";
            tab.NextPage.Text = last ? "Proximo capitulo ▶" : "Proximas folhas ▶";
            tab.PrevPage.Enabled = !first || CanStepChapter(tab, -1);
            tab.NextPage.Enabled = !last || CanStepChapter(tab, 1);
            foreach (RichTextBox page in new[] { tab.LeftPage, tab.RightPage })
            {
                int pageIndex = page == tab.LeftPage ? left : left + 1;
                Label head = page == tab.LeftPage ? tab.LeftHead : tab.RightHead, folio = page == tab.LeftPage ? tab.LeftFolio : tab.RightFolio;
                head.Text = pageIndex < count ? RunningHead(tab, page.Text, pageIndex) : "";
                folio.Text = pageIndex < count ? (pageIndex + 1).ToString() : "";
                if (pageIndex >= count) { ChapterEndMark(tab, page); continue; }
                string text = page.Text;
                FormatBookPage(tab, page, pageIndex);
                PaintStudies(tab, page, pageIndex);
                if (tab.Marks != null && tab.Marks.Terms.Count > 0)
                {
                    foreach (int[] r in FindMarks(text, tab.Marks)) { page.Select(r[0], r[1] - r[0]); page.SelectionBackColor = HighlightColor(); }
                }
                else if (tab.Verse > 1)
                {
                    System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(text, @"(?m)^" + tab.Verse + @"(?=\s)");
                    if (m.Success) { page.Select(m.Index, m.Length); page.SelectionBackColor = HighlightColor(); }
                }
                page.Select(0, 0); page.ScrollToCaret();
            }
        }

        // Livro aberto: pinta o marca-texto de cada versiculo da pagina e troca a fonte dos sinais ✎ ★.
        void PaintStudies(PassageTab tab, RichTextBox page, int pageIndex)
        {
            string text = page.Text;
            if (text.Length == 0) return;
            List<int[]> segs = new List<int[]>(); // inicio, versiculo
            segs.Add(new[] { 0, pageIndex < tab.PageFirstVerse.Count ? tab.PageFirstVerse[pageIndex] : 0 });
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"(?m)^(\d+)  "))
                segs.Add(new[] { m.Index, int.Parse(m.Groups[1].Value) });
            for (int i = 0; i < segs.Count; i++)
            {
                int start = segs[i][0], end = i + 1 < segs.Count ? segs[i + 1][0] : text.Length;
                string mark = segs[i][1] > 0 ? UserData.Get(UserData.Marks, tab.BookId, tab.Chapter, segs[i][1]) : null;
                if (mark == null || end <= start) continue;
                // Sem a quebra de linha final, para a cor nao vazar ate a margem.
                while (end > start && (text[end - 1] == '\n' || text[end - 1] == '\r')) end--;
                page.Select(start, end - start);
                page.SelectionBackColor = UserData.MarkColor(mark, BackgroundColor());
            }
            Font symbol = null;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '✎' && text[i] != '★') continue;
                if (symbol == null) symbol = SymbolFont(page.Font);
                page.Select(i, 1); page.SelectionFont = symbol; page.SelectionColor = AccentColor();
            }
        }

        static readonly System.Text.RegularExpressions.Regex VerseStartRx = new System.Text.RegularExpressions.Regex(@"^(\d+)  ");
        const TextFormatFlags MeasureFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

        // Recuo das linhas de continuacao: o texto fica alinhado depois do numero do versiculo.
        static int HangIndent(Font font, int verse)
        {
            return TextRenderer.MeasureText(verse + "  ", font, new Size(int.MaxValue, int.MaxValue), MeasureFlags).Width;
        }

        // Fonte dos sinais ✎ ★ sem passar da altura da linha do texto (senao a pagina estoura).
        static Font SymbolFont(Font body)
        {
            Font f = new Font("Segoe UI Symbol", body.Size);
            if (f.Height <= body.Height) return f;
            float size = Math.Max(6f, body.Size * body.Height / f.Height - 0.5f);
            f.Dispose();
            return new Font("Segoe UI Symbol", size);
        }

        // Cabecalho corrido como nas Biblias impressas: "SALMOS 119:22–40".
        string RunningHead(PassageTab tab, string text, int pageIndex)
        {
            Book book;
            if (!tab.Version.BooksById.TryGetValue(tab.BookId, out book)) return "";
            int first = pageIndex < tab.PageFirstVerse.Count ? tab.PageFirstVerse[pageIndex] : 0, last = first;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"(?m)^(\d+)  "))
            {
                last = int.Parse(m.Groups[1].Value);
                if (first <= 0) first = last;
            }
            string head = book.Name.ToUpperInvariant() + " " + tab.Chapter;
            return first > 0 ? head + ":" + first + (last > first ? "–" + last : "") : head;
        }

        // Titulo centralizado, numeros na cor de destaque e recuo nas linhas que continuam o versiculo.
        void FormatBookPage(PassageTab tab, RichTextBox page, int pageIndex)
        {
            string text = page.Text;
            Font font = page.Font;
            int verse = pageIndex < tab.PageFirstVerse.Count ? tab.PageFirstVerse[pageIndex] : 0;
            int start = 0, lineNo = 0;
            Dictionary<int, int> indents = new Dictionary<int, int>();
            while (start <= text.Length)
            {
                int end = text.IndexOf('\n', start);
                if (end < 0) end = text.Length;
                string line = text.Substring(start, end - start);
                System.Text.RegularExpressions.Match m = VerseStartRx.Match(line);
                if (m.Success)
                {
                    verse = int.Parse(m.Groups[1].Value);
                    page.Select(start, m.Groups[1].Length); page.SelectionColor = AccentColor();
                }
                else if (line.Length > 0 && verse > 0)
                {
                    int ind;
                    if (!indents.TryGetValue(verse, out ind)) indents[verse] = ind = HangIndent(font, verse);
                    page.Select(start, 0); page.SelectionIndent = ind;
                }
                else if (line.Length > 0 && pageIndex == 0 && lineNo == 0)
                {
                    page.Select(start, line.Length);
                    page.SelectionAlignment = HorizontalAlignment.Center;
                    page.SelectionFont = new Font(font, FontStyle.Bold); page.SelectionColor = AccentColor();
                }
                start = end + 1; lineNo++;
            }
        }

        // Folha da direita sem texto no fim do capitulo: vinheta e aviso do que vem a seguir.
        void ChapterEndMark(PassageTab tab, RichTextBox page)
        {
            string next = "";
            if (CanStepChapter(tab, 1))
            {
                Book cur = tab.Version.BooksById[tab.BookId];
                Book nb = tab.Chapter < cur.Chapters ? cur : tab.Version.Books[tab.Version.Books.IndexOf(cur) + 1];
                next = "\n\nA seguir: " + nb.Name + " " + (nb == cur ? tab.Chapter + 1 : 1) + " »";
            }
            SetPageText(page, "\n\n\n*   *   *\n\nFim do capitulo " + tab.Chapter + next);
            page.SelectAll();
            page.SelectionAlignment = HorizontalAlignment.Center;
            page.SelectionColor = FaintColor();
            page.Select(0, 0);
        }

        // Troca o texto da folha zerando a formatacao: o RichEdit herdaria recuo, negrito ou centralizado da folha anterior.
        static void SetPageText(RichTextBox page, string text)
        {
            page.Text = text;
            page.SelectAll();
            page.SelectionFont = page.Font;
            page.SelectionColor = page.ForeColor;
            page.SelectionBackColor = page.BackColor;
            page.SelectionIndent = 0; page.SelectionHangingIndent = 0; page.SelectionRightIndent = 0;
            page.SelectionAlignment = HorizontalAlignment.Left;
            page.Select(0, 0);
        }

        // Altura real de uma linha na caixa da pagina (o RichEdit nao usa exatamente Font.Height).
        static int MeasureLine(RichTextBox page, Font font, out int top)
        {
            top = 1;
            if (!page.IsHandleCreated) return font.Height;
            SetPageText(page, "Ág\nÁg\nÁg");
            int y0 = page.GetPositionFromCharIndex(0).Y, y2 = page.GetPositionFromCharIndex(6).Y;
            top = Math.Max(0, y0);
            int lh = (y2 - y0) / 2;
            return lh >= 6 ? lh : font.Height;
        }

        // O RichEdit desenha o texto um pouco mais largo que o TextRenderer mede (varia com a fonte):
        // mede um trecho real nas duas formas e devolve a proporcao, para a caixa nunca refazer a quebra.
        static float MeasureWidthRatio(RichTextBox page, Font font, string sample, int width)
        {
            if (!page.IsHandleCreated || sample.Length == 0) return 1.04f;
            Size huge = new Size(int.MaxValue, int.MaxValue);
            string s = "";
            foreach (string w in sample.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string c = s.Length == 0 ? w : s + " " + w;
                if (TextRenderer.MeasureText(c, font, huge, MeasureFlags).Width > width * 85 / 100) break;
                s = c;
            }
            if (s.Length < 12) return 1.04f;
            SetPageText(page, s + "|");
            int x0 = page.GetPositionFromCharIndex(0).X, x1 = page.GetPositionFromCharIndex(s.Length).X;
            int tr = TextRenderer.MeasureText(s, font, huge, MeasureFlags).Width;
            if (tr <= 0 || x1 <= x0) return 1.04f;
            return Math.Max(1f, (float)(x1 - x0) / tr) * 1.01f;
        }

        void RenderBook(PassageTab tab, bool keepSpread)
        {
            if (tab.LeftPage == null || tab.Version == null) return;
            Book book;
            if (!tab.Version.BooksById.TryGetValue(tab.BookId, out book)) return;
            Font font = new Font(readerFontName, readerFontSize);
            Font small = new Font(readerFontName, Math.Max(7, readerFontSize - 3));
            foreach (RichTextBox page in new[] { tab.LeftPage, tab.RightPage })
            {
                page.Font = font;
                page.BackColor = BackgroundColor();
                page.ForeColor = ForegroundColor();
            }
            foreach (Panel paper in new[] { tab.LeftPaper, tab.RightPaper }) { paper.BackColor = BackgroundColor(); paper.Invalidate(); }
            foreach (Label l in new[] { tab.LeftHead, tab.RightHead, tab.LeftFolio, tab.RightFolio })
            {
                l.Font = small; l.ForeColor = FaintColor(); l.BackColor = BackgroundColor();
            }
            tab.BookLayout.Invalidate();
            // Medicao pela area real da pagina e pela altura de linha do proprio RichEdit.
            RichTextBox probe = tab.LeftPage;
            // Aba recem-criada ainda nao tem a janela da caixa: sem ela nao da para medir a linha de verdade.
            if (!probe.IsHandleCreated) { IntPtr h = probe.Handle; }
            int pageW = probe.ClientSize.Width > 120 ? probe.ClientSize.Width : (tab.BookLayout.ClientSize.Width - 36) / 2 - PaperOuter - PaperInner;
            int pageH = probe.ClientSize.Height > 100 ? probe.ClientSize.Height : tab.BookLayout.ClientSize.Height - 42 - 16 - 66;
            int top, lineHeight = MeasureLine(probe, font, out top);
            int rows = Math.Max(4, (pageH - top - 2) / lineHeight);
            string sample = string.Join(" ", tab.Version.GetChapter(tab.BookId, tab.Chapter).Take(6).Select(x => x.T).ToArray());
            // Larguras em unidades do TextRenderer: a area util da caixa dividida pela proporcao medida no RichEdit.
            int avail = pageW - 6;
            float ratio = MeasureWidthRatio(probe, font, sample, avail);
            int width = Math.Max(140, (int)(avail / ratio));
            List<string> lines = new List<string>();
            List<int> lineVerse = new List<int>(); // versiculo a que cada linha pertence (0 = titulo)
            lines.Add(book.Name.ToUpperInvariant() + "  " + tab.Chapter); lineVerse.Add(0);
            lines.Add(""); lineVerse.Add(0);
            bool paragraphs = Paragraphs.HasBook(tab.BookId), firstVerse = true;
            Size huge = new Size(int.MaxValue, int.MaxValue);
            // Numero + texto do versiculo permanecem associados; quebrar em palavras, nao truncar.
            foreach (Verse v in tab.Version.GetChapter(tab.BookId, tab.Chapter))
            {
                // Linha em branco so onde o original abre paragrafo (dados reais, nao a cada 4 versiculos).
                if (!firstVerse && paragraphs && Paragraphs.Starts(tab.BookId, tab.Chapter, v.V) && lines[lines.Count - 1] != "") { lines.Add(""); lineVerse.Add(lineVerse[lineVerse.Count - 1]); }
                firstVerse = false;
                string paragraph = v.V + "  " + v.T + MarkerText(tab, v.V);
                // As linhas de continuacao ficam recuadas ate o texto do versiculo, entao cabem menos palavras.
                int contWidth = (int)((avail - HangIndent(font, v.V)) / ratio);
                string line = "";
                bool firstLine = true;
                foreach (string word in paragraph.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    // O numero vai sempre com dois espacos: e assim que se reconhece o inicio do versiculo na pagina.
                    string candidate = line.Length == 0 ? word : (firstLine && line == v.V.ToString() ? line + "  " + word : line + " " + word);
                    if (line.Length > 0 && TextRenderer.MeasureText(candidate, font, huge, MeasureFlags).Width > (firstLine ? width : contWidth))
                    { lines.Add(line); lineVerse.Add(v.V); line = word; firstLine = false; }
                    else line = candidate;
                }
                if (line.Length > 0) { lines.Add(line); lineVerse.Add(v.V); }
            }
            List<string> pages = new List<string>();
            tab.PageFirstVerse.Clear();
            Func<int, bool> startsVerse = k => VerseStartRx.IsMatch(lines[k]);
            for (int i = 0; i < lines.Count; )
            {
                while (i < lines.Count && lines[i] == "" && i > 0) i++;
                if (i >= lines.Count) break;
                int end = Math.Min(lines.Count, i + rows), min = i + Math.Max(2, rows / 2);
                for (bool moved = true; moved && end < lines.Count && end > min; )
                {
                    moved = false;
                    bool sameVerse = lines[end] != "" && lines[end - 1] != "" && lineVerse[end] == lineVerse[end - 1];
                    if (!sameVerse) break;
                    // Orfa: o numero do versiculo nao fica sozinho no pe da pagina.
                    if (startsVerse(end - 1)) { end--; moved = true; continue; }
                    // Viuva: a ultima linha de um versiculo nao fica sozinha no alto da folha seguinte.
                    bool lastLineOfVerse = end + 1 >= lines.Count || lines[end + 1] == "" || lineVerse[end + 1] != lineVerse[end];
                    if (lastLineOfVerse) { end--; moved = true; }
                }
                int stop = end;
                while (stop > i + 1 && lines[stop - 1] == "") stop--; // pagina nao termina em linha em branco
                pages.Add(string.Join("\r\n", lines.Skip(i).Take(stop - i).ToArray()));
                tab.PageFirstVerse.Add(lineVerse[i]);
                i = end;
            }
            if (pages.Count == 0) { pages.Add(""); tab.PageFirstVerse.Add(0); }
            tab.BookPages = pages;
            tab.PagedSize = probe.ClientSize;
            if (!keepSpread) tab.SpreadIndex = 0;
            // Ao abrir referencia especifica, localizar o verso nas paginas, sem mudar o texto fonte.
            if (!keepSpread && tab.Verse > 1)
            {
                for (int i = 0; i < pages.Count; i++)
                    if (System.Text.RegularExpressions.Regex.IsMatch(pages[i], @"(?m)^" + tab.Verse + @"\s+"))
                    { tab.SpreadIndex = i / 2; break; }
            }
            tab.SpreadIndex = Math.Min(tab.SpreadIndex, (pages.Count - 1) / 2);
            DisplaySpread(tab);
        }

        void RenderPassage(PassageTab tab)
        {
            BibleVersion ver = tab.Version;
            Book book;
            if (ver == null || !ver.BooksById.TryGetValue(tab.BookId, out book)) return;

            string suffix = versions.Count > 1 ? " (" + ver.Abbr + ")" : "";
            tab.Page.Text = book.Name + " " + tab.Chapter + suffix;
            tab.Title.Text = book.Name + " " + tab.Chapter + suffix + "  ▾";

            syncingUi = true;
            try
            {
                tab.Mode.SelectedIndex = readingMode;
                tab.FontBox.SelectedItem = readerFontName;
                tab.ThemeBox.SelectedIndex = themeIndex;
                tab.Size.Text = readerFontSize + " pt";
            }
            finally { syncingUi = false; }

            RichTextBox rtb = tab.Reader;
            bool bookMode = readingMode == 3;
            tab.Reader.Visible = !bookMode;
            tab.BookLayout.Visible = bookMode;
            tab.ReadingHost.BackColor = bookMode ? DeskColor() : BackgroundColor();
            ApplyColumn(tab);
            if (bookMode) { RenderBook(tab, false); return; }
            // Sem a janela do controle, o RichEdit ignora a fonte escolhida antes de cada AppendText.
            IntPtr readerHandle = rtb.Handle;
            rtb.Clear();
            rtb.Font = new Font(readerFontName, readerFontSize);
            rtb.BackColor = BackgroundColor();
            rtb.ForeColor = ForegroundColor();
            rtb.ZoomFactor = 1f;

            Font titleFont = new Font(readerFontName, readerFontSize + 1, FontStyle.Bold);
            Font numFont = new Font(readerFontName, Math.Max(6, readerFontSize - 1), FontStyle.Bold);
            Font bodyFont = new Font(readerFontName, readerFontSize);
            // Numero sobrescrito do modo paragrafos e numero grande do capitulo (capitular).
            Font supFont = new Font(readerFontName, Math.Max(6f, (float)Math.Round(readerFontSize * 0.62f)), FontStyle.Bold);
            int supOffset = Math.Max(2, (int)Math.Round(readerFontSize * 0.45f));
            Font dropFont = new Font(readerFontName, Math.Max(16f, readerFontSize * 2.4f), FontStyle.Bold);

            // Cabecalho: nome do livro em maiusculas, discreto, como o titulo corrido de uma Biblia impressa.
            Put(rtb, book.Name.ToUpperInvariant() + "\r\n", titleFont, AccentColor(), 0);
            if (readingMode == 2 && !Paragraphs.HasBook(tab.BookId))
            {
                Put(rtb, "(O texto-fonte deste livro nao tem marcas de paragrafo; exibido como texto corrido.)\r\n", numFont, AccentColor(), 0);
            }
            int bodyStart = rtb.TextLength;
            // Numero do capitulo grande: no inicio do primeiro paragrafo (corrido e paragrafos) ou numa linha propria (versiculos).
            Put(rtb, tab.Chapter.ToString() + (readingMode == 0 ? "\r\n" : " "), dropFont, AccentColor(), 0);
            int firstParaEnd = -1;

            // Guarda onde cada versiculo comecou, para destacar depois.
            List<KeyValuePair<int, Verse>> placed = new List<KeyValuePair<int, Verse>>();
            int targetStart = -1, targetEnd = -1, numStart = -1, numLen = 0;
            bool first = true;
            bool realParagraphs = Paragraphs.HasBook(tab.BookId);
            tab.VerseRanges.Clear();
            Font markerFont = new Font("Segoe UI Symbol", Math.Max(6, readerFontSize - 1));
            foreach (Verse v in ver.GetChapter(tab.BookId, tab.Chapter))
            {
                int verseStart, rangeStart;
                if (readingMode == 1 || readingMode == 2)
                {
                    // Texto corrido: sem numeros nem quebras, um paragrafo so, como uma carta.
                    // Paragrafos: quebra onde o original abre paragrafo (AT: Codice de Leningrado; NT: SBLGNT),
                    // com o numero do versiculo pequeno e elevado, como numa Biblia impressa.
                    if (!first)
                    {
                        bool newPara = readingMode == 2 && realParagraphs && Paragraphs.Starts(tab.BookId, tab.Chapter, v.V);
                        if (newPara && firstParaEnd < 0) firstParaEnd = rtb.TextLength;
                        Put(rtb, newPara ? "\r\n" : " ", bodyFont, ForegroundColor(), 0);
                    }
                    first = false;
                    rangeStart = rtb.TextLength;
                    if (readingMode == 2)
                    {
                        Put(rtb, v.V.ToString(), supFont, AccentColor(), supOffset);
                        Put(rtb, " ", supFont, AccentColor(), 0);
                        if (v.V == tab.Verse) { numStart = rangeStart; numLen = v.V.ToString().Length; }
                    }
                    verseStart = rtb.TextLength;
                    Put(rtb, v.T, bodyFont, ForegroundColor(), 0);
                    AppendMarkers(rtb, tab, v.V, markerFont);
                    if (v.V == tab.Verse) targetStart = verseStart;
                }
                else
                {
                    int n0 = rangeStart = rtb.TextLength;
                    Put(rtb, v.V + " ", numFont, AccentColor(), 0);
                    verseStart = rtb.TextLength;
                    Put(rtb, v.T, bodyFont, ForegroundColor(), 0);
                    AppendMarkers(rtb, tab, v.V, markerFont);
                    Put(rtb, "\r\n", bodyFont, ForegroundColor(), 0);
                    if (v.V == tab.Verse) { targetStart = n0; numStart = n0; numLen = verseStart - n0 - 1; }
                }
                placed.Add(new KeyValuePair<int, Verse>(verseStart, v));
                tab.VerseRanges.Add(new[] { rangeStart, rtb.TextLength, v.V });
                if (v.V == tab.Verse) targetEnd = verseStart + v.T.Length;
            }

            FormatParagraphs(tab, bodyStart, firstParaEnd, numFont);

            // Marca-texto do usuario (antes dos destaques de busca, que ficam por cima).
            foreach (KeyValuePair<int, Verse> kv in placed)
            {
                string mark = UserData.Get(UserData.Marks, tab.BookId, tab.Chapter, kv.Value.V);
                if (mark == null) continue;
                rtb.Select(kv.Key, kv.Value.T.Length);
                rtb.SelectionBackColor = UserData.MarkColor(mark, BackgroundColor());
            }

            Color hl = HighlightColor();
            if (tab.Marks != null && tab.Marks.Terms.Count > 0)
            {
                // Veio da busca: destaca so as palavras encontradas (no capitulo todo)
                // e o numero do versiculo escolhido, para localizar.
                foreach (KeyValuePair<int, Verse> kv in placed)
                    foreach (int[] r in FindMarks(kv.Value.T, tab.Marks))
                    {
                        rtb.Select(kv.Key + r[0], r[1] - r[0]);
                        rtb.SelectionBackColor = hl;
                    }
                if (numStart >= 0) { rtb.Select(numStart, numLen); rtb.SelectionBackColor = hl; }
            }
            else if (targetStart >= 0 && targetEnd > targetStart)
            {
                // Abriu por referencia (livro/capitulo/versiculo): destaca o versiculo inteiro.
                rtb.Select(targetStart, targetEnd - targetStart);
                rtb.SelectionBackColor = hl;
            }
            rtb.Select(targetStart > 0 && tab.Verse > 1 ? targetStart : 0, 0);
            rtb.ScrollToCaret();
        }

        // ---- Diagramacao dos modos de rolagem ----

        // Paragrafos com recuo na primeira linha (sem linha em branco), texto justificado e entrelinha;
        // no modo versiculos, recuo deslocado: as linhas que continuam o versiculo alinham depois do numero.
        void FormatParagraphs(PassageTab tab, int bodyStart, int firstParaEnd, Font numFont)
        {
            RichTextBox rtb = tab.Reader;
            short align = justifyText ? ParaFormat.Justify : ParaFormat.Left;
            int rule = LineSpacingRule[lineSpacingIndex];
            int indent = readerFontSize * 30;            // 1,5 eme, em twips
            int gap = (int)(readerFontSize * 20 * 0.45); // pouco espaco antes do paragrafo, em twips
            int end = rtb.TextLength;
            // Cabecalho (nome do livro): sem justificar e sem recuo.
            ParaFormat.Apply(rtb, 0, Math.Max(0, bodyStart - 1), ParaFormat.Left, 0, 0, 0, 20);
            if (readingMode == 2)
            {
                ParaFormat.Apply(rtb, bodyStart, end - bodyStart, align, indent, -indent, gap, rule);
                // O primeiro paragrafo comeca com o numero grande do capitulo: sem recuo nem espaco.
                int fe = firstParaEnd > 0 ? firstParaEnd : end;
                ParaFormat.Apply(rtb, bodyStart, Math.Max(0, fe - bodyStart - 1), align, 0, 0, 0, rule);
            }
            else if (readingMode == 1)
            {
                ParaFormat.Apply(rtb, bodyStart, end - bodyStart, align, 0, 0, 0, rule);
            }
            else
            {
                int hang;
                using (Graphics g = rtb.CreateGraphics())
                    hang = (int)(TextRenderer.MeasureText(g, "000 ", numFont, Size.Empty, TextFormatFlags.NoPadding).Width * 1440 / g.DpiX);
                ParaFormat.Apply(rtb, bodyStart, end - bodyStart, align, 0, hang, (int)(readerFontSize * 20 * 0.2), rule);
                // Linha do numero grande do capitulo.
                ParaFormat.Apply(rtb, bodyStart, 1, ParaFormat.Left, 0, 0, 0, 20);
            }
        }

        // Largura maxima da coluna de texto: em tela larga o texto fica centralizado com margens.
        void ApplyColumn(PassageTab tab)
        {
            if (tab.ReadingHost == null) return;
            int side = 20;
            int chars = ColumnChars[columnIndex];
            if (readingMode != 3 && chars > 0)
            {
                const string sample = "Porque Deus amou o mundo de tal maneira que deu o seu Filho unigenito ";
                using (Font f = new Font(readerFontName, readerFontSize))
                {
                    double avg = (double)TextRenderer.MeasureText(sample, f).Width / sample.Length;
                    int col = (int)(avg * chars) + SystemInformation.VerticalScrollBarWidth + 8;
                    side = Math.Max(20, (tab.ReadingHost.ClientSize.Width - col) / 2);
                }
            }
            Padding p = new Padding(side, 12, side, 12);
            if (tab.ReadingHost.Padding != p) tab.ReadingHost.Padding = p;
        }

        ContextMenuStrip FormatMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            Action changed = () => { SaveSettings(); RefreshOpenChapter(); };
            ToolStripMenuItem just = new ToolStripMenuItem("Justificar o texto", null, delegate { justifyText = !justifyText; changed(); });
            just.Checked = justifyText;
            m.Items.Add(just);
            ToolStripMenuItem sp = new ToolStripMenuItem("Entrelinha");
            for (int i = 0; i < LineSpacingNames.Length; i++)
            {
                int k = i;
                ToolStripMenuItem it = new ToolStripMenuItem(LineSpacingNames[i], null, delegate { lineSpacingIndex = k; changed(); });
                it.Checked = i == lineSpacingIndex;
                sp.DropDownItems.Add(it);
            }
            m.Items.Add(sp);
            ToolStripMenuItem col = new ToolStripMenuItem("Largura da coluna");
            for (int i = 0; i < ColumnNames.Length; i++)
            {
                int k = i;
                ToolStripMenuItem it = new ToolStripMenuItem(ColumnNames[i], null, delegate { columnIndex = k; changed(); });
                it.Checked = i == columnIndex;
                col.DropDownItems.Add(it);
            }
            m.Items.Add(col);
            m.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem note = new ToolStripMenuItem("Vale para versiculos, texto corrido e paragrafos (o livro aberto tem paginacao propria)");
            note.Enabled = false;
            m.Items.Add(note);
            return m;
        }

        // ---- Seletor rapido: grade de livros, depois capitulos, depois versiculos ----

        // Grupos de livros com cor propria na grade (como as secoes de uma Biblia impressa).
        static readonly int[] GroupStart = { 1, 6, 18, 23, 28, 40, 45, 58, 66 };
        static readonly string[] GroupNames = { "Lei", "Historicos", "Poeticos", "Profetas maiores", "Profetas menores", "Evangelhos e Atos", "Cartas de Paulo", "Cartas gerais", "Profecia" };
        static readonly Color[] GroupColors =
        {
            Color.FromArgb(222, 235, 250), Color.FromArgb(226, 242, 226), Color.FromArgb(250, 238, 214), Color.FromArgb(244, 224, 232),
            Color.FromArgb(236, 228, 246), Color.FromArgb(214, 236, 240), Color.FromArgb(250, 230, 214), Color.FromArgb(232, 236, 218),
            Color.FromArgb(238, 226, 226)
        };
        static int GroupOf(int bookId)
        {
            int g = 0;
            for (int i = 0; i < GroupStart.Length; i++) if (bookId >= GroupStart[i]) g = i;
            return g;
        }

        ToolStripDropDown picker;

        void ShowPicker(PassageTab tab)
        {
            if (picker != null) { picker.Close(); picker.Dispose(); }
            picker = new ToolStripDropDown();
            picker.Padding = new Padding(4);
            Panel host = new Panel(); host.BackColor = SystemColors.Window; host.Size = new Size(850, 430);
            ToolStripControlHost ch = new ToolStripControlHost(host); ch.AutoSize = false; ch.Size = host.Size; ch.Margin = Padding.Empty; ch.Padding = Padding.Empty;
            picker.Items.Add(ch);
            FillBookGrid(tab, host);
            // Abre logo abaixo do titulo, mas dentro da janela (desloca para a esquerda se faltar espaco).
            Point below = PointToClient(tab.Title.PointToScreen(new Point(0, tab.Title.Height + 2)));
            int x = Math.Max(8, Math.Min(below.X, ClientSize.Width - host.Width - 16));
            picker.Show(this, new Point(x, below.Y));
        }

        FlowLayoutPanel PickerFlow(Panel host, string title, Action back)
        {
            host.Controls.Clear();
            Panel top = new Panel(); top.Dock = DockStyle.Top; top.Height = 34;
            Label t = new Label(); t.Text = title; t.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold); t.AutoSize = true; t.Location = new Point(back != null ? 96 : 8, 8);
            top.Controls.Add(t);
            if (back != null)
            {
                Button b = MakeButton("◀ Voltar"); b.Location = new Point(6, 4); b.Size = new Size(82, 26);
                b.Click += delegate { back(); };
                top.Controls.Add(b);
            }
            FlowLayoutPanel flow = new FlowLayoutPanel(); flow.Dock = DockStyle.Fill; flow.AutoScroll = true; flow.Padding = new Padding(6);
            host.Controls.Add(flow); host.Controls.Add(top);
            return flow;
        }

        void FillBookGrid(PassageTab tab, Panel host)
        {
            FlowLayoutPanel flow = PickerFlow(host, "Escolha o livro  (" + tab.Version.Abbr + ")", null);
            string lastTestament = null;
            foreach (Book b in tab.Version.Books)
            {
                if (b.Testament != lastTestament)
                {
                    lastTestament = b.Testament;
                    Label sec = new Label(); sec.Text = b.Testament == "AT" ? "Antigo Testamento" : "Novo Testamento";
                    sec.Font = new Font("Segoe UI", 9, FontStyle.Bold); sec.ForeColor = SystemColors.GrayText;
                    sec.AutoSize = false; sec.Size = new Size(810, 22); sec.TextAlign = ContentAlignment.BottomLeft;
                    flow.SetFlowBreak(sec, true);
                    flow.Controls.Add(sec);
                }
                Book book = b;
                Button btn = MakeButton(b.Name); btn.Size = new Size(112, 28); btn.Margin = new Padding(2);
                btn.FlatStyle = FlatStyle.Flat; btn.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
                btn.BackColor = b.Id >= 1 && b.Id <= 66 ? GroupColors[GroupOf(b.Id)] : SystemColors.Control;
                btn.Font = new Font("Segoe UI", 8.25f, b.Id == tab.BookId ? FontStyle.Bold : FontStyle.Regular);
                if (b.Id >= 1 && b.Id <= 66) tip.SetToolTip(btn, b.Name + " - " + GroupNames[GroupOf(b.Id)] + ", " + b.Chapters + " capitulo(s)");
                btn.Click += delegate
                {
                    if (book.Chapters == 1) FillVerseGrid(tab, host, book, 1);
                    else FillChapterGrid(tab, host, book);
                };
                flow.Controls.Add(btn);
            }
        }

        void FillChapterGrid(PassageTab tab, Panel host, Book book)
        {
            FlowLayoutPanel flow = PickerFlow(host, book.Name + " - escolha o capitulo", () => FillBookGrid(tab, host));
            for (int c = 1; c <= book.Chapters; c++)
            {
                int chapter = c;
                Button btn = MakeButton(c.ToString()); btn.Size = new Size(44, 30); btn.Margin = new Padding(2);
                btn.FlatStyle = FlatStyle.Flat; btn.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
                btn.BackColor = GroupColors[GroupOf(Math.Max(1, Math.Min(66, book.Id)))];
                if (book.Id == tab.BookId && c == tab.Chapter) btn.Font = new Font(btn.Font, FontStyle.Bold);
                btn.Click += delegate { FillVerseGrid(tab, host, book, chapter); };
                flow.Controls.Add(btn);
            }
        }

        void FillVerseGrid(PassageTab tab, Panel host, Book book, int chapter)
        {
            FlowLayoutPanel flow = PickerFlow(host, book.Name + " " + chapter + " - escolha o versiculo",
                book.Chapters == 1 ? (Action)(() => FillBookGrid(tab, host)) : () => FillChapterGrid(tab, host, book));
            Button whole = MakeButton("Abrir o capitulo " + chapter); whole.Size = new Size(160, 30); whole.Margin = new Padding(2, 2, 2, 8);
            whole.Click += delegate { picker.Close(); GoInTab(tab, book.Id, chapter, 1); };
            flow.Controls.Add(whole); flow.SetFlowBreak(whole, true);
            foreach (Verse v in tab.Version.GetChapter(book.Id, chapter))
            {
                int verse = v.V;
                Button btn = MakeButton(v.V.ToString()); btn.Size = new Size(44, 30); btn.Margin = new Padding(2);
                btn.FlatStyle = FlatStyle.Flat; btn.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
                tip.SetToolTip(btn, v.T.Length > 120 ? v.T.Substring(0, 120) + "..." : v.T);
                btn.Click += delegate { picker.Close(); GoInTab(tab, book.Id, chapter, verse); };
                flow.Controls.Add(btn);
            }
        }

        // Abre outra passagem na mesma aba (mesma versao), sem voltar ao Indice Digital.
        void GoInTab(PassageTab tab, int bookId, int chapter, int verse)
        {
            if (!tab.Version.BooksById.ContainsKey(bookId)) return;
            tab.BookId = bookId; tab.Chapter = chapter; tab.Verse = verse; tab.Marks = null;
            RenderPassage(tab);
            tabs.SelectedTab = tab.Page;
            lastPassage = tab; currentBookId = bookId; currentChapter = chapter; currentVerse = verse;
            NoteReading(tab);
        }

        // Acrescenta um trecho e aplica a formatacao depois, pela selecao: assim o RichEdit guarda fonte,
        // cor e elevacao mesmo numa aba que ainda nao foi exibida.
        static void Put(RichTextBox rtb, string text, Font font, Color color, int offset)
        {
            int s = rtb.TextLength;
            rtb.AppendText(text);
            int n = rtb.TextLength - s;
            if (n > 0)
            {
                rtb.Select(s, n);
                rtb.SelectionFont = font; rtb.SelectionColor = color; rtb.SelectionCharOffset = offset;
            }
            rtb.Select(rtb.TextLength, 0);
        }

        // Sinais depois do versiculo: ✎ tem anotacao, ★ esta nos favoritos.
        static string MarkerText(PassageTab tab, int verse)
        {
            string s = "";
            if (UserData.Get(UserData.Notes, tab.BookId, tab.Chapter, verse) != null) s += " ✎";
            if (UserData.Get(UserData.Favorites, tab.BookId, tab.Chapter, verse) != null) s += " ★";
            return s;
        }

        void AppendMarkers(RichTextBox rtb, PassageTab tab, int verse, Font markerFont)
        {
            string m = MarkerText(tab, verse);
            if (m.Length == 0) return;
            Put(rtb, m, markerFont, AccentColor(), 0);
        }

        // Posicoes (inicio, fim) no texto original das palavras buscadas, ignorando acentos e maiusculas.
        static List<int[]> FindMarks(string original, SearchMarks marks)
        {
            StringBuilder norm = new StringBuilder(original.Length);
            List<int> map = new List<int>(original.Length);
            for (int i = 0; i < original.Length; i++)
            {
                char ch = original[i];
                string n = char.IsWhiteSpace(ch) || char.IsSurrogate(ch) ? ch.ToString() : TextUtil.Norm(ch.ToString());
                foreach (char c in n) { norm.Append(c); map.Add(i); }
            }
            string text = norm.ToString();
            List<int[]> ranges = new List<int[]>();
            foreach (string term in marks.Terms)
            {
                if (term.Length == 0) continue;
                // Termos curtos ("o", "e") so como palavra inteira, para nao pintar o capitulo todo.
                bool whole = marks.WholeWords || term.Length <= 2;
                int idx = text.IndexOf(term, StringComparison.Ordinal);
                while (idx >= 0)
                {
                    int s = idx, e = idx + term.Length;
                    bool okStart = s == 0 || !char.IsLetterOrDigit(text[s - 1]);
                    bool okEnd = e >= text.Length || !char.IsLetterOrDigit(text[e]);
                    if (whole)
                    {
                        if (okStart && okEnd) ranges.Add(new[] { map[s], map[e - 1] + 1 });
                    }
                    else
                    {
                        // Busca parcial: destaca a palavra inteira que contem o trecho ("salv" -> "salvacao").
                        while (s > 0 && char.IsLetterOrDigit(text[s - 1])) s--;
                        while (e < text.Length && char.IsLetterOrDigit(text[e])) e++;
                        ranges.Add(new[] { map[s], map[e - 1] + 1 });
                    }
                    idx = text.IndexOf(term, idx + term.Length, StringComparison.Ordinal);
                }
            }
            return ranges;
        }

        // Procura o termo num dicionario: primeiro a entrada exata, depois as parecidas
        // ("salv" -> salvacao; "pecados" -> pecado).
        static List<string> Lookup(Dictionary<string, List<string>> dict, string clean)
        {
            List<string> outList = new List<string>();
            List<string> direct;
            if (dict.TryGetValue(clean, out direct))
                foreach (string s in direct) if (!outList.Contains(s)) outList.Add(s);
            foreach (KeyValuePair<string, List<string>> kv in dict)
            {
                if (kv.Key == clean) continue;
                bool typingPrefix = clean.Length >= 3 && kv.Key.StartsWith(clean, StringComparison.Ordinal);
                bool inflected = kv.Key.Length >= 4 && clean.StartsWith(kv.Key, StringComparison.Ordinal);
                if (typingPrefix || inflected)
                    foreach (string s in kv.Value) if (!outList.Contains(s)) outList.Add(s);
            }
            return outList;
        }

        List<string> TermSuggestions(string term)
        {
            string clean = TextUtil.Norm(term);
            if (clean.Length == 0) return new List<string>();
            List<string> outList;
            if (suggestSource == 1) outList = Lookup(sinonimos, clean);
            else if (suggestSource == 0) outList = Lookup(analogico, clean);
            else
            {
                // Ambos: alterna sinonimo e termo analogico.
                List<string> a = Lookup(sinonimos, clean), b = Lookup(analogico, clean);
                outList = new List<string>();
                for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
                {
                    if (i < a.Count && !outList.Contains(a[i])) outList.Add(a[i]);
                    if (i < b.Count && !outList.Contains(b[i])) outList.Add(b[i]);
                }
            }
            return outList.Where(x => TextUtil.Norm(x) != clean).Take(20).ToList();
        }

        // Desenha a celula da lista de resultados; no texto, pinta as palavras buscadas.
        void DrawResultCell(DrawListViewSubItemEventArgs e)
        {
            Rectangle cell = new Rectangle(e.Bounds.X, e.Bounds.Y, results.Columns[e.ColumnIndex].Width, e.Bounds.Height);
            bool selected = e.Item.Selected;
            Color back = selected ? SystemColors.Highlight : results.BackColor;
            Color fore = selected ? SystemColors.HighlightText : results.ForeColor;
            using (SolidBrush b = new SolidBrush(back)) e.Graphics.FillRectangle(b, cell);

            TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            string text = e.SubItem.Text;
            Font font = results.Font;
            Verse v = e.Item.Tag as Verse;
            List<int[]> marks = e.ColumnIndex == 1 && v != null && lastSearchMarks != null ? MergeRanges(FindMarks(text, lastSearchMarks)) : new List<int[]>();

            // Se a primeira palavra encontrada ficaria escondida alem da largura da coluna,
            // mostra o texto a partir de um pouco antes dela, com "..." no inicio.
            if (marks.Count > 0)
            {
                int before = TextRenderer.MeasureText(e.Graphics, text.Substring(0, marks[0][0]), font, new Size(int.MaxValue, cell.Height), flags).Width;
                if (before > cell.Width * 0.6)
                {
                    int cutAt = Math.Max(0, marks[0][0] - 25);
                    int space = text.IndexOf(' ', cutAt);
                    if (space >= 0 && space < marks[0][0]) cutAt = space + 1;
                    text = "… " + text.Substring(cutAt);
                    int shift = 2 - cutAt;
                    marks = marks.Where(m => m[0] >= cutAt).Select(m => new[] { m[0] + shift, m[1] + shift }).ToList();
                }
            }

            Region oldClip = e.Graphics.Clip;
            e.Graphics.SetClip(cell);
            int x = cell.X + 4, pos = 0;
            Action<string, bool> draw = (piece, hl) =>
            {
                if (piece.Length == 0 || x > cell.Right) return;
                Size sz = TextRenderer.MeasureText(e.Graphics, piece, font, new Size(int.MaxValue, cell.Height), flags);
                Rectangle r = new Rectangle(x, cell.Y, sz.Width, cell.Height);
                if (hl)
                {
                    using (SolidBrush hb = new SolidBrush(Color.FromArgb(255, 226, 110))) e.Graphics.FillRectangle(hb, r.X, r.Y + 1, r.Width, r.Height - 2);
                    TextRenderer.DrawText(e.Graphics, piece, font, r, Color.Black, flags);
                }
                else TextRenderer.DrawText(e.Graphics, piece, font, r, fore, flags);
                x += sz.Width;
            };
            foreach (int[] m in marks)
            {
                draw(text.Substring(pos, m[0] - pos), false);
                draw(text.Substring(m[0], m[1] - m[0]), true);
                pos = m[1];
            }
            draw(text.Substring(pos), false);
            e.Graphics.Clip = oldClip;
        }

        static List<int[]> MergeRanges(List<int[]> ranges)
        {
            List<int[]> merged = new List<int[]>();
            foreach (int[] r in ranges.OrderBy(r => r[0]))
            {
                if (merged.Count > 0 && r[0] <= merged[merged.Count - 1][1])
                    merged[merged.Count - 1][1] = Math.Max(merged[merged.Count - 1][1], r[1]);
                else merged.Add(new[] { r[0], r[1] });
            }
            return merged;
        }

        void UpdateSuggestions()
        {
            lstSuggest.BeginUpdate();
            lstSuggest.Items.Clear();
            foreach (string s in TermSuggestions(txtSearch.Text.Trim())) lstSuggest.Items.Add(s);
            lstSuggest.EndUpdate();
        }

        void DoSearch()
        {
            string term = txtSearch.Text.Trim();
            if (term.Length == 0) return;

            Stopwatch sw = Stopwatch.StartNew();
            Cursor = Cursors.WaitCursor;
            results.BeginUpdate();
            try
            {
                results.Items.Clear();

                // E/OU vale entre as palavras digitadas; os sinonimos (Flexivel) entram como alternativas.
                string exactNorm = TextUtil.Norm(term);
                string[] words = exactNorm.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                List<string> synonyms = new List<string>();
                List<string> synonymsShown = new List<string>();
                if (chkFlex.Checked)
                {
                    foreach (string s in TermSuggestions(term).Take(3))
                    {
                        string n = TextUtil.Norm(s);
                        if (n.Length > 0 && n != exactNorm && !synonyms.Contains(n)) { synonyms.Add(n); synonymsShown.Add(s); }
                    }
                }

                bool exact = chkExact.Checked, and = rbAnd.Checked;
                SearchMarks marks = new SearchMarks();
                marks.WholeWords = exact;
                if (exact) marks.Terms.Add(exactNorm);
                else { marks.Terms.AddRange(words); marks.Terms.AddRange(synonyms); }
                lastSearchMarks = marks;
                bool onlyOT = rbOT.Checked, onlyNT = rbNT.Checked, onlyBook = rbBook.Checked;
                List<ListViewItem> items = new List<ListViewItem>();
                const int limit = 1500;

                foreach (Verse v in current.Verses)
                {
                    if (onlyOT && v.Testament != "AT") continue;
                    if (onlyNT && v.Testament != "NT") continue;
                    if (onlyBook && v.B != currentBookId) continue;

                    string text = v.N;
                    bool match;
                    if (exact)
                    {
                        match = Search.ContainsWhole(text, exactNorm);
                    }
                    else
                    {
                        if (and)
                        {
                            match = true;
                            foreach (string w in words) if (!text.Contains(w)) { match = false; break; }
                        }
                        else
                        {
                            match = false;
                            foreach (string w in words) if (text.Contains(w)) { match = true; break; }
                        }
                        if (!match)
                            foreach (string s in synonyms) if (text.Contains(s)) { match = true; break; }
                    }
                    if (!match) continue;

                    Book book = BookById(v.B);
                    string reference = (book != null ? book.Name : v.B.ToString()) + " " + v.C + ":" + v.V;
                    ListViewItem item = new ListViewItem(reference);
                    item.SubItems.Add(v.T);
                    item.Tag = v;
                    item.ToolTipText = reference + " - " + v.T;
                    items.Add(item);
                    if (items.Count >= limit) break;
                }
                results.Items.AddRange(items.ToArray());
                sw.Stop();

                string tempo = sw.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture);
                string msg = items.Count + " referencias encontradas em " + tempo + " s.";
                if (items.Count >= limit) msg = "Mostrando as primeiras " + limit + " referencias (" + tempo + " s). Refine a busca para ver menos.";
                string usedLabel = suggestSource == 1 ? "Sinonimos usados" : suggestSource == 0 ? "Termos do dicionario analogico usados" : "Termos usados";
                if (!exact && synonymsShown.Count > 0) msg += " " + usedLabel + ": " + string.Join(", ", synonymsShown) + ".";
                lblCount.Text = msg;
            }
            finally
            {
                results.EndUpdate();
                Cursor = Cursors.Default;
            }
        }

#if AUTOTESTE
        // So na compilacao de teste (compilar-teste.bat): captura a propria janela para conferir o visual.
        // Fica fora do .exe distribuido, porque copiar a tela e um comportamento que antivirus vigiam.
        public string ScreenTest(string importFile)
        {
            Show();
            cmbVersion.SelectedIndex = 0;
            continuousText = false; readerFontName = "Georgia"; readerFontSize = 12; SetTheme(0);
            OpenReference(43, 3, 16, null, true);
            chkExact.Checked = false; chkFlex.Checked = true; rbAnd.Checked = true; rbAll.Checked = true;
            cmbSuggest.SelectedIndex = 1;
            txtSearch.Text = "alegria"; DoSearch();
            OpenReference(62, 4, 8, lastSearchMarks, true);
            tabs.SelectedTab = tabIndex;
            int ivebTela = versions.FindIndex(x => x.Key == "pt-veb");
            if (ivebTela >= 0)
            {
                // Mostra a versao propria aberta em Jonas, com o destaque de uma busca.
                cmbVersion.SelectedIndex = ivebTela;
                chkExact.Checked = true; txtSearch.Text = "Senhor Jeová"; DoSearch();
                OpenReference(32, 1, 17, lastSearchMarks, true);
            }
            Action<string> shot = name =>
            {
                TopMost = true; Activate();
                for (int i = 0; i < 20; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                using (Bitmap bmp = new Bitmap(Width, Height))
                {
                    using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(Left, Top, 0, 0, Size);
                    bmp.Save(Path.Combine(Program.AppDataDir(), name), System.Drawing.Imaging.ImageFormat.Png);
                }
                TopMost = false;
            };
            shot("tela.png");
            Size s0 = Size; Size = new Size(1120, 780); tabs.SelectedTab = tabIndex; shot("tela-indice.png");
            Size = MinimumSize; shot("tela-indice-minima.png"); Size = s0;
            // Modo livro aberto com tema Sepia (Salmo 119) e modo paragrafos (Genesis 1).
            readingMode = 3; SetTheme(ReadingTheme.IndexOf("Sepia"));
            OpenReference(19, 119, 33, null, true);
            shot("tela-livro.png");
            GoToSpread(lastPassage, 0); shot("tela-livro-inicio.png");
            string diag = "Tela livro -> folha " + lastPassage.LeftPage.ClientSize + ", paginada com " + lastPassage.PagedSize + ", maior pagina=" +
                lastPassage.BookPages.Max(p => p.Split('\n').Length) + " linhas\r\n";
            SetTheme(ReadingTheme.IndexOf("Azul noite")); RenderPassage(lastPassage);
            GoToSpread(lastPassage, int.MaxValue); shot("tela-livro-fim.png");
            readingMode = 2; SetTheme(0);
            OpenReference(1, 1, 1, null, true);
            shot("tela-paragrafos.png");
            OpenReference(43, 1, 1, null, true);
            shot("tela-paragrafos-nt.png");
            ShowPicker(lastPassage);
            shot("tela-seletor.png");
            picker.Close();
            readingMode = 0; OpenReference(43, 3, 16, null, true);
            shot("tela-versiculos.png");
            // Estudos: marca-texto, anotacao e favorito em Joao 3 (so em memoria) e a aba Meus estudos.
            readingMode = 0; SetTheme(0);
            UserData.SetMark(43, 3, 16, "amarelo"); UserData.SetMark(43, 3, 17, "verde");
            UserData.SetNote(43, 3, 16, "Exemplo de anotacao"); UserData.SetFavorite(43, 3, 16, true);
            OpenReference(43, 3, 14, null, true);
            shot("tela-estudos.png");
            tabs.SelectedTab = tabStudies;
            shot("tela-meus-estudos.png");
            UserData.SetMark(43, 3, 16, null); UserData.SetMark(43, 3, 17, null);
            UserData.SetNote(43, 3, 16, null); UserData.SetFavorite(43, 3, 16, false);
            readingMode = 0;
            // Janelas de versoes: um timer fotografa a janela modal aberta e a fecha.
            Action<string, Action> shotDialog = (name, open) =>
            {
                Timer t = new Timer(); t.Interval = 1500;
                t.Tick += delegate
                {
                    t.Stop();
                    Form af = Application.OpenForms.Cast<Form>().LastOrDefault(x => x != this && x.Visible && x.Modal);
                    if (af == null) return;
                    using (Bitmap bmp = new Bitmap(af.Width, af.Height))
                    {
                        using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(af.Left, af.Top, 0, 0, af.Size);
                        bmp.Save(Path.Combine(Program.AppDataDir(), name), System.Drawing.Imaging.ImageFormat.Png);
                    }
                    af.Close();
                };
                t.Start(); open(); t.Dispose();
            };
            tabs.SelectedTab = tabIndex;
            shotDialog("tela-versoes.png", () => ShowVersionsDialog());
            if (importFile != null && File.Exists(importFile))
            {
                ImportResult ir = VersionImport.Parse(importFile, versions.Where(v => v.UserFile == null).ToList());
                if (ir.Versions.Count > 0) shotDialog("tela-importar.png", () => ShowImportPreview(this, importFile, ir));
            }
            return diag + "Capturas salvas em tela.png, tela-livro.png, tela-paragrafos.png, tela-estudos.png, tela-meus-estudos.png, tela-versoes.png e tela-importar.png\r\n";
        }

#endif

        // Importacao de versoes no autoteste: arquivos de exemplo numa pasta temporaria, nunca na pasta real do usuario.
        void ImportSelfTest(StringBuilder sb, Action<string, Action> run)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "BibliaDigital-teste-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            string oldDir = Program.VersionsDirOverride;
            Program.VersionsDirOverride = Path.Combine(tmp, "versoes");
            try
            {
                List<BibleVersion> builtIn = versions.Where(v => v.UserFile == null).ToList();
                Func<ImportResult, string> show = r =>
                {
                    string s = r.Format + ", " + r.Versions.Count + " versao(oes), problemas " + r.ProblemCount;
                    foreach (BibleVersion v in r.Versions)
                        s += " | " + v.Abbr + ": " + v.Verses.Count + " vers., livros " + string.Join(",", v.Books.Select(b => b.Id + "=" + b.Name).Take(8).ToArray());
                    return s;
                };
                // 1) Colunas com ;, cabecalho, nomes/abreviacoes em portugues, texto entre aspas com ; dentro, linha ruim, ANSI.
                string csv = Path.Combine(tmp, "teste.csv");
                File.WriteAllText(csv, "livro;capitulo;versiculo;texto\r\nGênesis;1;1;No princípio era o teste.\r\nGn;1;2;\"Texto com ; dentro\"\r\n" +
                    "Jó;1;1;Havia um homem na terra de Uz.\r\nJo;3;16;Porque Deus amou o mundo de tal maneira.\r\n1 Samuel;1;1;Houve um homem.\r\n" +
                    "I Coríntios;13;4;O amor é sofredor.\r\nLivro Inventado;1;1;nao existe\r\nlinha sem colunas\r\nJo;3;16;repetido\r\n", Encoding.GetEncoding(1252));
                ImportResult rc = VersionImport.Parse(csv, builtIn);
                sb.AppendLine("Importar CSV -> " + show(rc));
                foreach (string p in rc.Problems) sb.AppendLine("    ! " + p);
                // 2) Colunas com id na frente (id,b,c,v,t), em ingles com nomes.
                string ids = Path.Combine(tmp, "t_web.csv");
                File.WriteAllText(ids, "id,b,c,v,t\r\n1001001,Genesis,1,1,\"In the beginning, God created.\"\r\n43003016,John,3,16,For God so loved the world.\r\n19023001,Psalms,23,1,The LORD is my shepherd.\r\n", Encoding.UTF8);
                sb.AppendLine("Importar CSV com id -> " + show(VersionImport.Parse(ids, builtIn)));
                // 3) JSON lista de livros com capitulos (66 livros, um versiculo cada).
                string jl = Path.Combine(tmp, "livros.json");
                StringBuilder js = new StringBuilder("[");
                for (int b = 1; b <= 66; b++) js.Append((b > 1 ? "," : "") + "{\"abbrev\":\"x" + b + "\",\"chapters\":[[\"Livro " + b + " versiculo 1\",\"Livro " + b + " versiculo 2\"]]}");
                File.WriteAllText(jl, js.Append("]").ToString(), Encoding.UTF8);
                sb.AppendLine("Importar JSON por livros -> " + show(VersionImport.Parse(jl, builtIn)));
                // 4) JSON lista de versiculos com nomes; 5) linhas "Livro cap:vers texto"; 6) formato do programa com linha quebrada.
                string jv = Path.Combine(tmp, "versiculos.json");
                File.WriteAllText(jv, "{\"abbr\":\"JV\",\"name\":\"Teste JSON\",\"verses\":[{\"book\":\"Mateus\",\"chapter\":5,\"verse\":9,\"text\":\"Bem-aventurados os pacificadores\"},{\"book\":\"Romanos\",\"chapter\":8,\"verse\":28,\"text\":\"E sabemos\"}]}", Encoding.UTF8);
                sb.AppendLine("Importar JSON por versiculos -> " + show(VersionImport.Parse(jv, builtIn)));
                string refs = Path.Combine(tmp, "referencias.txt");
                File.WriteAllText(refs, "João 3:16 Porque Deus amou o mundo.\r\n1 Coríntios 13:4 O amor é paciente.\r\nSalmos 23:1 O Senhor é o meu pastor.\r\n", Encoding.UTF8);
                sb.AppendLine("Importar referencias -> " + show(VersionImport.Parse(refs, builtIn)));
                string nat = Path.Combine(tmp, "nativo.txt");
                File.WriteAllText(nat, "@V\tpt-x\tPX\tpt-BR\tNativa\r\n43\t3\t16\tPorque Deus amou.\r\n43\tx\t17\tquebrada\r\n43\t3\t17\tPorque Deus enviou.\r\n", Encoding.UTF8);
                sb.AppendLine("Importar formato do programa -> " + show(VersionImport.Parse(nat, builtIn)));

                // Instalar, abrir, buscar, recarregar do disco e remover.
                BibleVersion cv = rc.Versions[0], replace;
                sb.AppendLine("Validar sigla BLIVRE -> " + ValidateImport(cv, "BLIVRE", "x", out replace) + " | sigla vazia -> " + ValidateImport(cv, "", "x", out replace) +
                    " | TST -> " + (ValidateImport(cv, "TST", "Teste", out replace) ?? "ok"));
                int before = versions.Count;
                BibleVersion inst = InstallVersion(cv, "TST", "Versao de teste", null);
                OpenReference(43, 3, 16, null, true);
                sb.AppendLine("Instalar TST -> versoes " + before + " -> " + versions.Count + ", atual=" + current.Abbr + ", combo '" + cmbVersion.SelectedItem +
                    "', arquivo " + Path.GetFileName(inst.UserFile) + " existe=" + File.Exists(inst.UserFile) + ", aba '" + tabs.SelectedTab.Text.Trim() + "', texto: " +
                    (txtChapter != null && txtChapter.Text.Contains("Porque Deus amou") ? "ok" : "FALHOU"));
                run("busca 'amor' na TST", () => { txtSearch.Text = "amor"; });
                List<string> warn = new List<string>();
                List<BibleVersion> again = DataLoader.LoadUserVersions(Program.VersionsDir(), builtIn, warn);
                sb.AppendLine("Recarregar do disco -> " + again.Count + " versao, " + (again.Count > 0 ? again[0].Abbr + " " + again[0].Verses.Count + " vers., " + again[0].Books.Count + " livros" : "-") +
                    ", avisos " + warn.Count);
                File.WriteAllText(Path.Combine(Program.VersionsDir(), "defeito.txt"), "sem cabecalho\r\n", Encoding.UTF8);
                warn.Clear(); DataLoader.LoadUserVersions(Program.VersionsDir(), builtIn, warn);
                sb.AppendLine("Arquivo com defeito na pasta -> avisos: " + string.Join(" / ", warn.ToArray()));
                InstallVersion(VersionImport.Parse(csv, builtIn).Versions[0], "tst", "Substituta", inst);
                sb.AppendLine("Substituir TST -> versoes " + versions.Count + ", nome '" + versions.Last().Label + "'");
                RemoveUserVersion(versions.Last());
                sb.AppendLine("Remover TST -> versoes " + versions.Count + ", atual=" + current.Abbr + ", guardado como .removida=" +
                    File.Exists(Path.Combine(Program.VersionsDir(), "usr-tst.txt.removida")) + ", abas abertas " + passageTabs.Count);
            }
            catch (Exception ex) { sb.AppendLine("Importacao -> ERRO: " + ex); }
            finally
            {
                Program.VersionsDirOverride = oldDir;
                foreach (BibleVersion v in versions.Where(x => x.UserFile != null && x.UserFile.StartsWith(tmp)).ToList()) versions.Remove(v);
                RebuildVersionCombo(versions[0]);
                CloseOtherPassages(null);
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        // Usado pelo modo de autoteste (BibliaDigital.exe /teste).
        public string SelfTest()
        {
            StringBuilder sb = new StringBuilder();
            // Parte sempre do mesmo estado, independentemente das preferencias salvas pelo usuario.
            cmbVersion.SelectedIndex = 0;
            chkNewTab.Checked = false; openInNewTab = false;
            readingMode = 0; readerFontName = "Georgia"; readerFontSize = 12; themeIndex = 0;
            Action<string, Action> run = (name, setup) =>
            {
                chkExact.Checked = false; chkFlex.Checked = true; rbAnd.Checked = true; rbAll.Checked = true;
                setup();
                DoSearch();
                sb.AppendLine(name + " -> " + lblCount.Text + " | 1o: " + (results.Items.Count > 0 ? results.Items[0].Text : "-"));
            };
            run("compaixao exata NT", () => { txtSearch.Text = "compaixão"; chkExact.Checked = true; rbNT.Checked = true; });
            run("suporta", () => { txtSearch.Text = "suporta"; });
            run("amor flex", () => { txtSearch.Text = "amor"; });
            run("Jesus Cristo E sem flex", () => { txtSearch.Text = "Jesus Cristo"; chkFlex.Checked = false; });
            run("Jesus Cristo OU sem flex", () => { txtSearch.Text = "Jesus Cristo"; chkFlex.Checked = false; rbOr.Checked = true; });
            run("o senhor e o meu pastor exata", () => { txtSearch.Text = "o senhor e o meu pastor"; chkExact.Checked = true; });
            run("amor exata (sem amorreus)", () => { txtSearch.Text = "amor"; chkExact.Checked = true; });
            chkFlex.Checked = true; chkExact.Checked = true;
            sb.AppendLine("Exata marcada desmarca Flexivel -> Flexivel=" + chkFlex.Checked + ", Operador habilitado=" + rbAnd.Parent.Enabled);
            chkFlex.Checked = true;
            sb.AppendLine("Flexivel marcada desmarca Exata -> Exata=" + chkExact.Checked + ", Operador habilitado=" + rbAnd.Parent.Enabled);
            OpenReference(19, 23, 1);
            sb.AppendLine("Abrir Salmos 23 -> aba '" + tabs.SelectedTab.Text + "', versos " + current.GetChapter(19, 23).Count);
            OpenReference(1, 1, 1);
            sb.AppendLine("Genesis 1 -> aba '" + tabs.SelectedTab.Text + "'");
            continuousText = false; OpenReference(19, 23, 4);
            int linhasVerso = txtChapter.Lines.Length;
            continuousText = true; OpenReference(19, 23, 4);
            sb.AppendLine("Salmos 23 versiculo a versiculo -> " + linhasVerso + " linhas | texto corrido -> " + txtChapter.Lines.Length +
                " linhas, destaque em: '" + txtChapter.Text.Substring(txtChapter.SelectionStart, 30) + "...'");
            continuousText = false;

            // Paragrafos reais (AT: WLC; NT: SBLGNT), agora com recuo e sem linha em branco:
            // o versiculo abre paragrafo quando o caractere antes do seu numero e a quebra de linha.
            readingMode = 2;
            Func<int, int, int, bool> breakBefore = (b, c, v) =>
            {
                OpenReference(b, c, 1);
                int[] r = lastPassage.VerseRanges.FirstOrDefault(x => x[2] == v);
                return r != null && r[0] > 0 && txtChapter.Text[r[0] - 1] == '\n';
            };
            bool g6 = breakBefore(1, 1, 6), g2 = breakBefore(1, 1, 2);
            int blocks = txtChapter.Text.Split('\n').Length;
            sb.AppendLine("Paragrafos (" + Paragraphs.Count + " inicios) Genesis 1 -> quebra antes de 1:6=" + g6 + ", antes de 1:2=" + g2 +
                ", linhas de paragrafo=" + blocks + ", linha em branco=" + txtChapter.Text.Contains("\n\n"));
            // NT: paragrafos do SBLGNT (Mateus 5 abre paragrafo em 5:1 e 5:3; Romanos 6 em 6:1 e 6:5, nao em 6:2)
            bool mt3 = breakBefore(40, 5, 3), rm5 = breakBefore(45, 6, 5), rm2 = breakBefore(45, 6, 2);
            OpenReference(40, 5, 1);
            sb.AppendLine("Paragrafos NT (SBLGNT) -> Mateus 5: quebra antes de 5:3=" + mt3 + ", aviso de fonte sem paragrafos=" +
                txtChapter.Text.Contains("nao tem marcas de paragrafo") + " | Romanos 6: antes de 6:5=" + rm5 + ", antes de 6:2=" + rm2 +
                " | livros do NT com paragrafos=" + Enumerable.Range(40, 27).Count(Paragraphs.HasBook));
            // Diagramacao 1.9: numero sobrescrito, capitular, recuo/justificado, coluna e seletor.
            IntPtr hfd = Handle; // a janela oculta precisa existir para o RichEdit guardar a formatacao
            OpenReference(43, 3, 1);
            {
                RichTextBox rtd = txtChapter; PassageTab pt = lastPassage;
                IntPtr hrd = rtd.Handle; RenderPassage(pt);
                int[] rd16 = pt.VerseRanges.First(x => x[2] == 16);
                rtd.Select(rd16[0], 2);
                bool sup = rtd.SelectionCharOffset > 0 && rtd.SelectionFont.Size < readerFontSize && rtd.SelectionColor == AccentColor();
                int dropAt = rtd.Text.IndexOf('\n') + 1;
                rtd.Select(dropAt, 1);
                bool drop = rtd.Text[dropAt] == '3' && rtd.SelectionFont.Size >= readerFontSize * 2;
                string copied = CopyText(pt, rtd, rd16[0], rd16[1]);
                rtd.Select(rd16[0], 2); string supInfo = " [num='" + rtd.SelectedText + "' off=" + rtd.SelectionCharOffset + " tam=" + rtd.SelectionFont.Size + " cor=" + rtd.SelectionColor.Name + "/" + AccentColor().Name + "]";
                rtd.Select(dropAt, 1); supInfo += " [cap='" + rtd.SelectedText + "' tam=" + rtd.SelectionFont.Size + " modo=" + readingMode + "]";
                sb.AppendLine("Diagramacao paragrafos Joao 3 -> numero 16 sobrescrito=" + sup + ", capitular '3'=" + drop + supInfo +
                    ", titulo da aba='" + pt.Title.Text + "', copiar 16 -> " + copied.Replace("\r\n", " / "));
                pt.Page.Bounds = tabs.DisplayRectangle; pt.Page.PerformLayout();
                columnIndex = 0; ApplyColumn(pt); int pNarrow = pt.ReadingHost.Padding.Left;
                columnIndex = 3; ApplyColumn(pt); int pFull = pt.ReadingHost.Padding.Left;
                columnIndex = 1; ApplyColumn(pt);
                sb.AppendLine("Coluna -> margem lateral estreita=" + pNarrow + " px, toda a janela=" + pFull + " px, media=" + pt.ReadingHost.Padding.Left + " px (host " + pt.ReadingHost.ClientSize.Width + " px)");
                justifyText = false; RenderPassage(pt); justifyText = true; RenderPassage(pt);
                GoInTab(pt, 45, 8, 28);
                sb.AppendLine("Seletor (GoInTab) -> aba '" + pt.Page.Text + "', versiculo " + pt.Verse + ", ultima leitura " + lastRead);
                ShowPicker(pt); Application.DoEvents();
                Panel ph = (Panel)((ToolStripControlHost)picker.Items[0]).Control;
                FlowLayoutPanel books = ph.Controls.OfType<FlowLayoutPanel>().First();
                int nb = books.Controls.OfType<Button>().Count();
                Button rom = books.Controls.OfType<Button>().First(x => x.Text == pt.Version.BooksById[45].Name);
                rom.PerformClick();
                int nc = ph.Controls.OfType<FlowLayoutPanel>().First().Controls.OfType<Button>().Count();
                picker.Close();
                sb.AppendLine("Seletor grade -> livros=" + nb + ", capitulos de Romanos=" + nc + ", cor dos grupos: Gn " + GroupNames[GroupOf(1)] + ", Rm " + GroupNames[GroupOf(45)]);
                ClosePassage(pt);
            }
            readingMode = 0;

            // Livro aberto: Salmo 119 inteiro nas paginas, sem cortar texto
            Size = new Size(1280, 860); IntPtr hForm = Handle;
            readingMode = 3; OpenReference(19, 119, 1, null, true);
            PassageTab tb = lastPassage;
            // Janela oculta: a TabControl nao dimensiona a aba sozinha.
            tb.Page.Bounds = tabs.DisplayRectangle; tb.Page.PerformLayout(); tb.BookLayout.PerformLayout(); IntPtr hPage = tb.LeftPage.Handle; hPage = tb.RightPage.Handle;
            RenderBook(tb, false);
            string all = string.Join("\n", tb.BookPages);
            int nums = 0;
            for (int n = 1; n <= 176; n++) if (System.Text.RegularExpressions.Regex.IsMatch(all, @"(?m)^" + n + @" ")) nums++;
            Func<string, int> words = t => t.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
            int expected = 2 + current.GetChapter(19, 119).Sum(x => 1 + words(x.T));
            int overflow = 0;
            for (int sp = 0; sp * 2 < tb.BookPages.Count; sp++)
            {
                tb.SpreadIndex = sp; DisplaySpread(tb);
                foreach (RichTextBox pg in new[] { tb.LeftPage, tb.RightPage })
                    if (pg.TextLength > 0 && pg.GetPositionFromCharIndex(pg.TextLength - 1).Y + pg.Font.Height > pg.ClientSize.Height) overflow++;
            }
            sb.AppendLine("Livro aberto Salmos 119 -> " + tb.BookPages.Count + " paginas, numeros 1-176 presentes=" + nums + ", palavras " + words(all) + "/" + expected +
                ", paginas que passam da altura=" + overflow + ", cor da pagina=" + (tb.LeftPage.BackColor == BackgroundColor()) + ", barras " + string.Join("/", ((TableLayoutPanel)tb.Page.Controls[0]).GetRowHeights().Take(2).Select(h => h.ToString()).ToArray()) + " px");
            // Varias fontes e tamanhos: nenhuma linha pode ser requebrada pelo RichEdit nem passar do pe da pagina.
            List<string> sizes = new List<string>();
            foreach (string fn in new[] { "Georgia", "Verdana", "Times New Roman", "Segoe UI", "Consolas" })
                foreach (int fs in new[] { 9, 12, 16, 22 })
                {
                    if (!readerFonts.Contains(fn)) continue;
                    readerFontName = fn; readerFontSize = fs; RenderBook(tb, false);
                    int lhTop, lh = MeasureLine(tb.LeftPage, tb.LeftPage.Font, out lhTop);
                    int rowsFit = (tb.LeftPage.ClientSize.Height - lhTop - 2) / lh;
                    int rewrap = 0, tall = 0, used = 0, cap = 0;
                    for (int sp = 0; sp * 2 < tb.BookPages.Count; sp++)
                    {
                        tb.SpreadIndex = sp; DisplaySpread(tb);
                        foreach (RichTextBox pg in new[] { tb.LeftPage, tb.RightPage })
                        {
                            if (pg.TextLength == 0) continue;
                            int textLines = pg.Text.Split('\n').Length, shown = pg.GetLineFromCharIndex(pg.TextLength) + 1;
                            if (shown != textLines) rewrap++;
                            if (pg.GetPositionFromCharIndex(pg.TextLength - 1).Y + lh > pg.ClientSize.Height) tall++;
                        }
                    }
                    for (int p = 0; p + 1 < tb.BookPages.Count; p++) { used += tb.BookPages[p].Split('\n').Length; cap++; }
                    sizes.Add(fn + " " + fs + ": " + tb.BookPages.Count + " pag., requebra=" + rewrap + ", estouro=" + tall + ", ocupacao " + (cap > 0 ? (100 * used / (cap * rowsFit)) : 100) + "%");
                }
            readerFontName = "Georgia"; readerFontSize = 12; RenderBook(tb, false);
            {
                int t0, l0 = MeasureLine(tb.LeftPage, tb.LeftPage.Font, out t0);
                SetPageText(tb.LeftPage, string.Join("\n", Enumerable.Repeat("Ág", 40).ToArray()));
                int fit = 0;
                for (int k = 0; k < 40; k++) if (tb.LeftPage.GetPositionFromCharIndex(k * 3).Y + l0 <= tb.LeftPage.ClientSize.Height) fit = k + 1;
                sb.AppendLine("Livro aberto medidas -> linha " + l0 + " px, topo " + t0 + ", folha " + tb.LeftPage.ClientSize + ", linhas que cabem=" + fit +
                    ", maior pagina=" + tb.BookPages.Max(p => p.Split('\n').Length) + " linhas, fonte.Height=" + tb.LeftPage.Font.Height);
                DisplaySpread(tb);
            }
            sb.AppendLine("Livro aberto fontes -> " + string.Join(" | ", sizes.ToArray()));
            // Folhear alem do fim abre o capitulo seguinte; voltar do inicio abre o fim do anterior.
            GoToSpread(tb, int.MaxValue); ChangeBookSpread(tb, 1);
            string fwd = tb.Page.Text.Trim() + " folha " + tb.SpreadIndex;
            ChangeBookSpread(tb, -1);
            sb.AppendLine("Livro aberto virar folhas -> apos o fim: " + fwd + " | voltando: " + tb.Page.Text.Trim() + " folha " + tb.SpreadIndex + "/" + MaxSpread(tb) +
                ", botao: '" + tb.NextPage.Text + "', cabecalho: '" + tb.LeftHead.Text + "' / '" + tb.RightHead.Text + "', folios " + tb.LeftFolio.Text + "/" + tb.RightFolio.Text);
            ClosePassage(tb);
            readingMode = 0;

            readerFontName = readerFonts.Contains("Verdana") ? "Verdana" : readerFonts[0];
            readerFontSize = 16; SetTheme(ReadingTheme.IndexOf("Sepia"));
            OpenReference(19, 23, 1);
            sb.AppendLine("Aparencia -> fonte " + txtChapter.Font.Name + " " + txtChapter.Font.Size + "pt, fundo " +
                txtChapter.BackColor.R + "," + txtChapter.BackColor.G + "," + txtChapter.BackColor.B + ", tema no indice: " + cmbBg.SelectedItem);
            sb.AppendLine("Fontes disponiveis: " + string.Join(", ", readerFonts));

            // Destaque so das palavras buscadas
            readerFontName = "Georgia"; readerFontSize = 12; SetTheme(0);
            chkFlex.Checked = false; rbAll.Checked = true; chkExact.Checked = true;
            txtSearch.Text = "amor"; DoSearch();
            OpenFromItem(results.Items[0]);
            RichTextBox rt = txtChapter;
            string rtText = rt.Text;
            int pos = -1;
            for (int i = rtText.IndexOf("amor", StringComparison.Ordinal); i >= 0; i = rtText.IndexOf("amor", i + 1, StringComparison.Ordinal))
                if ((i == 0 || !char.IsLetter(rtText[i - 1])) && (i + 4 >= rtText.Length || !char.IsLetter(rtText[i + 4]))) { pos = i; break; }
            bool wordHl = false, restHl = false;
            if (pos >= 3)
            {
                rt.Select(pos, 4); wordHl = rt.SelectionBackColor == HighlightColor();
                rt.Select(pos - 3, 1); restHl = rt.SelectionBackColor == HighlightColor();
            }
            sb.AppendLine("Destaque Exata 'amor' (" + tabs.SelectedTab.Text.Trim() + ") -> palavra destacada=" + wordHl + ", resto do versiculo destacado=" + restHl);
            chkExact.Checked = false; chkFlex.Checked = false;
            txtSearch.Text = "salv"; DoSearch();
            Verse fv = (Verse)results.Items[0].Tag;
            sb.AppendLine("Destaque parcial 'salv' em " + results.Items[0].Text + " -> " +
                string.Join(", ", FindMarks(fv.T, lastSearchMarks).Select(r => fv.T.Substring(r[0], r[1] - r[0]))));
            txtSearch.Text = "o senhor"; DoSearch();
            fv = (Verse)results.Items[0].Tag;
            sb.AppendLine("Destaque 'o senhor' em " + results.Items[0].Text + " -> " +
                string.Join(", ", FindMarks(fv.T, lastSearchMarks).Select(r => fv.T.Substring(r[0], r[1] - r[0]))));

            // Almeida 1911 com ortografia atualizada: a busca por grafia atual passa a encontrar
            {
                int ia = versions.FindIndex(x => x.Key == "pt-alm1911"), ib = versions.FindIndex(x => x.Key == "pt-alm1911a");
                if (ia >= 0 && ib >= 0)
                {
                    Func<int, string, string> conta = (iv, termo) => { cmbVersion.SelectedIndex = iv; chkExact.Checked = true; rbAll.Checked = true; txtSearch.Text = termo; DoSearch(); return results.Items.Count.ToString(); };
                    string r = "";
                    foreach (string termo in new[] { "Cristo", "profeta", "Egito", "ele" })
                        r += " | '" + termo + "' 1911=" + conta(ia, termo) + " atualizada=" + conta(ib, termo);
                    Verse gn2 = versions[ib].GetChapter(1, 1).FirstOrDefault(x => x.V == 2);
                    sb.AppendLine("Almeida 1911 atualizada" + r + " | Gn 1:2: " + (gn2 != null ? gn2.T : "-"));
                    cmbVersion.SelectedIndex = 0;
                }
            }
            sb.AppendLine("Versoes: " + string.Join(" | ", versions.Select(x => x.Abbr + " " + x.Verses.Count + " vers., AT " +
                x.Books.Count(b => b.Testament == "AT") + "/NT " + x.Books.Count(b => b.Testament == "NT"))));
            foreach (BibleVersion bv in versions)
            {
                // Versoes parciais (ex.: a traducao propria em andamento) podem nao ter Joao.
                Verse jo316 = bv.GetChapter(43, 3).FirstOrDefault(x => x.V == 16);
                sb.AppendLine("  " + bv.Abbr + " Jo 3:16 -> " + (jo316 != null ? jo316.T : "(livro ainda nao traduzido nesta versao)"));
            }

            // Versao propria: livros, navegacao e busca
            int iveb = versions.FindIndex(x => x.Key == "pt-veb");
            if (iveb >= 0)
            {
                BibleVersion veb = versions[iveb];
                sb.AppendLine("VEB livros: " + string.Join(", ", veb.Books.Select(b => b.Name + " (" + b.Testament + ", " + b.Chapters + " cap.)")));
                cmbVersion.SelectedIndex = iveb;
                sb.AppendLine("VEB caixas do indice -> AT: " + cmbAT.Items.Count + " livro(s), NT: " + cmbNT.Items.Count + " livro(s)");
                OpenReference(32, 4, 11, null, true);
                PassageTab tv = lastPassage;
                StepChapter(tv, +1);
                sb.AppendLine("VEB Jonas 4 -> proximo capitulo: aba '" + tv.Page.Text + "'");
                StepChapter(tv, -1);
                sb.AppendLine("VEB capitulo anterior -> aba '" + tv.Page.Text + "'");
                OpenReference(29, 3, 21, null, false); StepChapter(lastPassage, +1);
                sb.AppendLine("VEB Joel 3 -> proximo capitulo (Obadias ainda nao existe): aba '" + lastPassage.Page.Text + "'");
                OpenReference(50, 4, 23, null, false); StepChapter(lastPassage, +1);
                sb.AppendLine("VEB Filipenses 4 -> proximo capitulo (proximo livro da versao): aba '" + lastPassage.Page.Text + "'");
                chkExact.Checked = true; rbAll.Checked = true; txtSearch.Text = "Senhor Jeová"; DoSearch();
                sb.AppendLine("VEB busca exata 'Senhor Jeová' -> " + lblCount.Text);
                chkExact.Checked = false; chkFlex.Checked = false; txtSearch.Text = "suportar"; DoSearch();
                sb.AppendLine("VEB busca 'suportar' -> " + lblCount.Text + " | 1o: " + (results.Items.Count > 0 ? results.Items[0].Text : "-"));
                ClosePassage(tv);
                cmbVersion.SelectedIndex = 0;
            }

            // Sinonimos x dicionario analogico
            Func<int, string, string> sug = (src, t) => { cmbSuggest.SelectedIndex = src; return string.Join(", ", TermSuggestions(t).Take(6)); };
            sb.AppendLine("Sugestoes 'alegria' analogico -> [" + sug(0, "alegria") + "] | sinonimos -> [" + sug(1, "alegria") + "]");
            sb.AppendLine("Sinonimo reverso 'gozo' -> [" + sug(1, "gozo") + "] | 'jubilo' -> [" + sug(1, "jubilo") + "]");
            sb.AppendLine("Ambos 'amor' -> [" + sug(2, "amor") + "] | analogico 'salv' -> [" + sug(0, "salv") + "]");
            sb.AppendLine("Sinonimos carregados: " + sinonimos.Count + " termos");
            chkExact.Checked = false; chkFlex.Checked = true; rbAnd.Checked = true;
            cmbSuggest.SelectedIndex = 1; txtSearch.Text = "alegria"; DoSearch();
            sb.AppendLine("Busca 'alegria' flex com sinonimos -> " + lblCount.Text);
            Verse rv = (Verse)results.Items[0].Tag;
            sb.AppendLine("Marcas na lista em " + results.Items[0].Text + " -> " +
                string.Join(", ", MergeRanges(FindMarks(rv.T, lastSearchMarks)).Select(r => rv.T.Substring(r[0], r[1] - r[0]))));
            cmbSuggest.SelectedIndex = 0;

            // Varias abas
            int antes = passageTabs.Count;
            OpenReference(43, 3, 16, null, true);
            OpenReference(19, 23, 1, null, true);
            sb.AppendLine("Novas abas -> " + antes + " -> " + passageTabs.Count + ": " + string.Join(" | ", passageTabs.Select(p => p.Page.Text.Trim())));
            OpenReference(1, 1, 1);
            sb.AppendLine("Abrir sem nova aba reaproveita a atual -> " + passageTabs.Count + ": " + string.Join(" | ", passageTabs.Select(p => p.Page.Text.Trim())));
            StepChapter(passageTabs[0], +1);
            sb.AppendLine("Proximo capitulo so na 1a aba -> " + string.Join(" | ", passageTabs.Select(p => p.Page.Text.Trim())));
            ClosePassage(passageTabs[1]);
            sb.AppendLine("Fechar uma aba -> " + passageTabs.Count + ": " + string.Join(" | ", passageTabs.Select(p => p.Page.Text.Trim())));
            CloseOtherPassages(null);
            sb.AppendLine("Fechar todas -> " + passageTabs.Count + ", aba atual: " + tabs.SelectedTab.Text);

            // Estudos (so em memoria): marca-texto, anotacao, favorito, copiar com referencia, historico, retomar
            readingMode = 0; readerFontName = "Georgia"; readerFontSize = 12; SetTheme(0);
            UserData.SetMark(43, 3, 16, "amarelo"); UserData.SetNote(43, 3, 16, "Teste de anotacao\r\nsegunda linha"); UserData.SetFavorite(43, 3, 16, true);
            OpenReference(43, 3, 17, null, true);
            PassageTab ts = lastPassage;
            Verse j16 = current.GetChapter(43, 3).First(x => x.V == 16);
            string rtxt = ts.Reader.Text;
            int body = rtxt.IndexOf(j16.T, StringComparison.Ordinal);
            ts.Reader.Select(body + 3, 1);
            bool markOk = ts.Reader.SelectionBackColor == UserData.MarkColor("amarelo", BackgroundColor());
            int[] r16 = ts.VerseRanges.First(x => x[2] == 16), r17 = ts.VerseRanges.First(x => x[2] == 17);
            string line16 = rtxt.Substring(r16[0], r16[1] - r16[0]);
            sb.AppendLine("Estudos (em memoria) -> marca amarela=" + markOk + ", sinais anotacao/favorito=" + (line16.Contains("✎") && line16.Contains("★")) +
                ", versiculo no clique=" + VerseAt(ts, ts.Reader, body + 5) + ", no titulo=" + VerseAt(ts, ts.Reader, 2));
            sb.AppendLine("Copiar trecho -> " + CopyText(ts, ts.Reader, body, body + 22).Replace("\r\n", " / "));
            sb.AppendLine("Copiar 16-17 -> " + CopyText(ts, ts.Reader, r16[0], r17[1]).Replace("\r\n", " / "));
            readingMode = 3; RenderPassage(ts);
            ts.Page.Bounds = tabs.DisplayRectangle; ts.Page.PerformLayout(); ts.BookLayout.PerformLayout(); IntPtr hp = ts.LeftPage.Handle; hp = ts.RightPage.Handle;
            RenderBook(ts, false);
            int pgx = ts.BookPages.FindIndex(p => System.Text.RegularExpressions.Regex.IsMatch(p, @"(?m)^16  "));
            bool bookOk = false; int bookVerse = -1;
            if (pgx >= 0)
            {
                ts.SpreadIndex = pgx / 2; DisplaySpread(ts);
                RichTextBox bp = pgx % 2 == 0 ? ts.LeftPage : ts.RightPage;
                int at = System.Text.RegularExpressions.Regex.Match(bp.Text, @"(?m)^16  ").Index + 6;
                bookVerse = VerseAt(ts, bp, at);
                bp.Select(at, 1); bookOk = bp.SelectionBackColor == UserData.MarkColor("amarelo", BackgroundColor());
            }
            sb.AppendLine("Livro aberto estudos -> versiculo no clique=" + bookVerse + ", marca amarela=" + bookOk + ", 1o versiculo por pagina=" + string.Join(",", ts.PageFirstVerse.Select(x => x.ToString()).ToArray()));
            readingMode = 0;
            RefreshStudies();
            sb.AppendLine("Meus estudos -> favoritos " + lvFav.Items.Count + ", anotacoes " + lvNotes.Items.Count + " ('" + (lvNotes.Items.Count > 0 ? lvNotes.Items[0].SubItems[1].Text : "") +
                "'), marcas " + lvMarks.Items.Count + ", historico " + lvHist.Items.Count + " (mais recente: " + (lvHist.Items.Count > 0 ? lvHist.Items[0].Text : "-") + ")");
            sb.AppendLine("Retomar leitura -> " + lastRead + " | botao: " + btnResume.Text);
            UserData.SetMark(43, 3, 16, null); UserData.SetNote(43, 3, 16, null); UserData.SetFavorite(43, 3, 16, false);
            sb.AppendLine("Estudos limpos -> anotacoes " + UserData.Notes.Count + ", marcas " + UserData.Marks.Count + ", favoritos " + UserData.Favorites.Count +
                ", arquivos do usuario intocados=" + UserData.Memory);
            CloseOtherPassages(null);
            ImportSelfTest(sb, run);
            cmbVersion.SelectedIndex = Math.Max(0, versions.FindIndex(x => x.Key == "en-kjv"));
            run("love (" + current.Key + ")", () => { txtSearch.Text = "love"; });
            return sb.ToString();
        }
    }

    class SplashForm : Form
    {
        public SplashForm()
        {
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ShowInTaskbar = false;
            Size = new Size(360, 110);
            Text = "Biblia Digital";
            Label l = new Label();
            l.Text = "Carregando a Biblia, aguarde...";
            l.Dock = DockStyle.Fill;
            l.TextAlign = ContentAlignment.MiddleCenter;
            l.Font = new Font("Segoe UI", 11);
            Controls.Add(l);
        }
    }

    static class Program
    {
        // Pasta propria do aplicativo para logs (em vez de gravar soltos em %TEMP%).
        public static string AppDataDir()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BibliaDigital");
            Directory.CreateDirectory(dir);
            return dir;
        }

        // Versoes importadas pelo usuario (fora da pasta do programa: sobrevivem as atualizacoes).
        public static string VersionsDirOverride; // autoteste usa uma pasta temporaria
        public static string VersionsDir()
        {
            return VersionsDirOverride ?? Path.Combine(AppDataDir(), "versoes");
        }

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ShowError(e.Exception);
            bool selfTest = args.Length > 0 && args[0].Equals("/teste", StringComparison.OrdinalIgnoreCase);
#if AUTOTESTE
            bool importTest = args.Length > 1 && args[0].Equals("/importar", StringComparison.OrdinalIgnoreCase);
            selfTest |= importTest;
#endif

            SplashForm splash = null;
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string dataPath = Path.Combine(baseDir, "data\\biblias.txt");
                string jsonPath = Path.Combine(baseDir, "data\\bible_data.json");
                if (!File.Exists(dataPath) && File.Exists(jsonPath)) dataPath = jsonPath;
                string dictPath = Path.Combine(baseDir, "data\\dicionario_analogico.csv");
                string synPath = Path.Combine(baseDir, "data\\sinonimos.csv");

                if (!File.Exists(dataPath))
                {
                    MessageBox.Show("Arquivo de dados nao encontrado:\n" + dataPath +
                        "\n\nDescompacte o ZIP inteiro e mantenha a pasta 'data' ao lado do BibliaDigital.exe.",
                        "Biblia Digital", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }

                if (!selfTest)
                {
                    splash = new SplashForm();
                    splash.Show();
                    Application.DoEvents();
                }

                Stopwatch sw = Stopwatch.StartNew();
                List<BibleVersion> versions = DataLoader.LoadBible(dataPath);
                Dictionary<string, List<string>> analogico = DataLoader.LoadDictionary(dictPath);
                Dictionary<string, List<string>> sinonimos = DataLoader.LoadSynonyms(synPath);
                Paragraphs.Load(Path.Combine(baseDir, "data\\paragrafos.txt"));
                if (versions.Count == 0) throw new InvalidDataException("A base biblica nao contem nenhuma versao.");
                double loadSeconds = sw.Elapsed.TotalSeconds;
#if AUTOTESTE
                // BibliaDigital-teste.exe /importar arquivo: so analisa o arquivo e grava o relatorio em teste.txt.
                if (importTest)
                {
                    Stopwatch si = Stopwatch.StartNew();
                    ImportResult ir = VersionImport.Parse(args[1], versions);
                    StringBuilder rep = new StringBuilder("Importar " + args[1] + " -> formato " + ir.Format + ", " + ir.Versions.Count + " versao(oes), " +
                        ir.ProblemCount + " problema(s), " + si.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s\r\n");
                    foreach (string pr in ir.Problems.Take(20)) rep.AppendLine("  ! " + pr);
                    UserData.Memory = true;
                    MainForm mf = new MainForm(versions, analogico, sinonimos);
                    mf.NoSave = true;
                    foreach (BibleVersion iv in ir.Versions)
                        rep.AppendLine("  [" + iv.Key + "] sigla " + iv.Abbr + ", nome '" + iv.Label + "', idioma " + iv.Lang + "\r\n    " + mf.ImportSummary(iv).Replace("\r\n", "\r\n    ").TrimEnd());
                    File.WriteAllText(Path.Combine(AppDataDir(), "teste.txt"), rep.ToString(), Encoding.UTF8);
                    return 0;
                }
#endif
                // Versoes importadas: um arquivo com defeito vira aviso, nao impede de abrir.
                List<string> userWarnings = new List<string>();
                if (!selfTest) versions.AddRange(DataLoader.LoadUserVersions(VersionsDir(), versions, userWarnings));

                // Autoteste: estudos so em memoria, nunca mexe nas anotacoes do usuario.
                if (selfTest) UserData.Memory = true;
                MainForm form = new MainForm(versions, analogico, sinonimos);

                if (selfTest)
                {
                    form.NoSave = true;
                    string report = "Carregamento: " + loadSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s (" +
                        Path.GetFileName(dataPath) + "; " + DataLoader.LastTimings + ")\r\n" + form.SelfTest();
#if AUTOTESTE
                    if (args.Length > 1 && args[1].Equals("tela", StringComparison.OrdinalIgnoreCase)) report += form.ScreenTest(args.Length > 2 ? args[2] : null);
#endif
                    File.WriteAllText(Path.Combine(AppDataDir(), "teste.txt"), report, Encoding.UTF8);
                    return 0;
                }

                form.Shown += delegate
                {
                    if (splash != null) { splash.Close(); splash.Dispose(); splash = null; }
                    form.Activate();
                    if (userWarnings.Count > 0)
                        MessageBox.Show(form, "Algumas versoes importadas nao puderam ser carregadas:\r\n\r\n" + string.Join("\r\n", userWarnings.Take(10).ToArray()) +
                            "\r\n\r\nPasta: " + VersionsDir(), "Biblia Digital", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                };
                Application.Run(form);
                return 0;
            }
            catch (Exception ex)
            {
                if (splash != null) { try { splash.Close(); } catch { } }
                ShowError(ex);
                return 1;
            }
        }

        static void ShowError(Exception ex)
        {
            string msg = ex.ToString();
            try { File.WriteAllText(Path.Combine(AppDataDir(), "erro.log"), msg, Encoding.UTF8); } catch { }
            MessageBox.Show("Ocorreu um erro na Biblia Digital:\n\n" + ex.Message +
                "\n\nDetalhes salvos em %LOCALAPPDATA%\\BibliaDigital\\erro.log",
                "Biblia Digital", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
