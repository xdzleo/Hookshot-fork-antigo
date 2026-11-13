using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpCompress.Archives;

namespace HookshotBuilder;

// (fork) "Baixar da sua fonte": o endereço da fonte onde a pessoa comprou o jogo (uma lista no formato do Hydra: name, e
// downloads com title, uris e às vezes platform). O Builder acha nela o Ocarina of Time do Switch 2, baixa (magnet pela ponte de
// libtorrent do Hydra, ou link direto), junta os arquivos compactados que vierem (.rar, .zip, .7z: o romfs num, o exefs noutro,
// .hookshot_exefs vale como exefs) e segue para a geração de sempre, com as pastas romfs e exefs

/// Um erro que a pessoa lê como está
sealed class SourceException(string message) : Exception(message);

static class Sources
{
    const string Term = "ocarina";
    // (como a Vitrine reconhece o Switch 2: no título, ou no campo platform. O nome do Hookshot também conta)
    static readonly string[] TitleMarks = { "switch 2", "ns2", "hookshot" };
    static readonly string[] PlatformMarks = { "ns2", "switch2", "switch 2", "nintendoswitch2" };
    const int MaxAddress = 2048;
    const long MaxList = 64L << 20;

    static string SavedFile => Path.Combine(BuilderHome.Base, "fonte.txt");

    /// A fonte usada da última vez
    public static string? Saved
    {
        get
        {
            try { return File.ReadAllText(SavedFile).Trim() is { Length: > 0 } t ? t : null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    public static void Save(string url)
    {
        try
        {
            Directory.CreateDirectory(BuilderHome.Base);
            File.WriteAllText(SavedFile, url);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// O endereço colado, se for http ou https e não tiver nada estranho
    public static string? Accept(string line)
    {
        var url = line.Trim();
        var lower = url.ToLowerInvariant();
        var rest = lower.StartsWith("https://") ? lower[8..] : lower.StartsWith("http://") ? lower[7..] : null;
        if (rest is not { Length: > 0 } || rest.StartsWith('/') || url.Length > MaxAddress) return null;
        return url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '"' or '<' or '>') ? null : url;
    }

    /// É o Ocarina do Switch 2: o título diz Ocarina e marca o Switch 2 (ou o campo platform marca)
    public static bool IsOcarina(string title, string? platform)
    {
        var t = title.ToLowerInvariant();
        var onTitle = TitleMarks.Any(t.Contains);
        var onPlatform = platform != null && PlatformMarks.Contains(platform.Trim().ToLowerInvariant());
        return t.Contains(Term) && (onTitle || onPlatform);
    }

    /// (título, endereço) do primeiro Ocarina do Switch 2 com endereço de download. Magnet antes de link direto: um link direto
    /// de fonte costuma ser a página de um site de arquivos, não o arquivo
    public static (string Title, string Uri) Find(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { throw new SourceException("Esse endereço não traz uma lista de fonte (JSON)."); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("downloads", out var downloads) ||
                downloads.ValueKind != JsonValueKind.Array)
                throw new SourceException("A lista dessa fonte não tem downloads.");
            foreach (var d in downloads.EnumerateArray())
            {
                if (d.ValueKind != JsonValueKind.Object || !d.TryGetProperty("title", out var t) || t.ValueKind != JsonValueKind.String) continue;
                var platform = d.TryGetProperty("platform", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                if (!IsOcarina(t.GetString()!, platform)) continue;
                if (!d.TryGetProperty("uris", out var uris) || uris.ValueKind != JsonValueKind.Array) continue;
                var links = uris.EnumerateArray().Where(u => u.ValueKind == JsonValueKind.String).Select(u => u.GetString()!).ToList();
                var uri = links.FirstOrDefault(u => u.StartsWith("magnet:?xt=urn:btih:", StringComparison.OrdinalIgnoreCase))
                          ?? links.FirstOrDefault(u => u.StartsWith("https://") || u.StartsWith("http://"));
                if (uri != null) return (t.GetString()!, uri);
            }
        }
        throw new SourceException("Essa fonte não tem o Ocarina of Time do Switch 2 com um endereço de download.");
    }

    public static async Task<(string Title, string Uri)> Fetch(string url, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HookshotBuilder");
        HttpResponseMessage r;
        try { r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException e) { throw new SourceException($"Não deu para abrir o endereço da fonte ({e.Message})."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new SourceException("O site da fonte não respondeu a tempo."); }
        using (r)
        {
            if (r.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable)
                throw new SourceException("O site da fonte bloqueou o programa (verificação do navegador): não dá para abrir essa fonte por aqui.");
            if (!r.IsSuccessStatusCode) throw new SourceException($"O site da fonte respondeu {(int)r.StatusCode}.");
            if (r.Content.Headers.ContentLength > MaxList) throw new SourceException("A lista é grande demais para ser uma fonte.");
            await using var s = await r.Content.ReadAsStreamAsync(ct);
            using var m = new MemoryStream();
            var b = new byte[81920];
            int n;
            while ((n = await s.ReadAsync(b, ct)) > 0)
            {
                m.Write(b, 0, n);
                if (m.Length > MaxList) throw new SourceException("A lista é grande demais para ser uma fonte.");
            }
            return Find(Encoding.UTF8.GetString(m.ToArray()));
        }
    }
}

/// Como vai o download
readonly record struct DownloadStatus(long Done, long Wanted, long BytesPerSecond, int Peers, int Seeds, bool Metadata)
{
    public double Fraction => Wanted > 0 ? Math.Clamp(Done / (double)Wanted, 0, 1) : 0;
}

/// O magnet pela ponte de libtorrent 2.1.1 do Hydra (Native/hydra_torrent_bridge.dll, MIT, sem alteração; Native/NOTICES.md).
/// A DLL só é carregada depois de conferida pelo SHA-256. Parar mantém o que já veio: a próxima vez continua de onde parou
static class Torrent
{
    const string Dll = "hydra_torrent_bridge";
    const string DllSha256 = "d8b4ee8e9c4195d4d594c4df18de98fe98c5f8a391a907d7159111c94c1de0a4";
    const int Finished = 4, Seeding = 5;
    static IntPtr module;

    static string Folder => Path.Combine(AppContext.BaseDirectory, "Native");

    /// Confere e carrega a DLL uma vez; as chamadas abaixo passam a achá-la por aqui
    static void Load()
    {
        if (module != IntPtr.Zero) return;
        var path = Path.Combine(Folder, Dll + ".dll");
        if (!File.Exists(path)) throw new SourceException($"O motor de torrent não está na pasta do Hookshot ({path}).");
        using (var f = File.OpenRead(path))
            if (Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant() != DllSha256)
                throw new SourceException("O motor de torrent da pasta do Hookshot não confere com o original. Instale o Hookshot de novo.");
        module = NativeLibrary.Load(path);
        NativeLibrary.SetDllImportResolver(typeof(Torrent).Assembly, (name, _, _) => name == Dll ? module : IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Status
    {
        public long Wanted, Done, Downloaded;
        public double Progress;
        public int DownloadRate, UploadRate, Peers, Seeds, State, Metadata;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr ht_error();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void ht_string_free(IntPtr s);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ht_session_new(ushort port, out IntPtr session);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void ht_session_free(IntPtr session);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ht_prepare(byte[] magnet, byte[] savePath, int a, int b, out IntPtr parameters);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void ht_params_free(IntPtr parameters);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ht_tracker_count(IntPtr parameters, out int count);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ht_tracker_get(IntPtr parameters, int i, out IntPtr url, out int tier);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ht_tracker_append(IntPtr parameters, byte[] url, int tier);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ht_add(IntPtr session, IntPtr parameters, out IntPtr handle);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ht_remove(IntPtr session, IntPtr handle, int deleteFiles);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void ht_handle_free(IntPtr handle);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ht_pause(IntPtr handle);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ht_resume(IntPtr handle);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ht_get_status(IntPtr handle, out Status status);

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    static string? Take(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        var s = Marshal.PtrToStringUTF8(p);
        ht_string_free(p);
        return s;
    }

    static void Ok(int code)
    {
        if (code != 0) throw new SourceException("O torrent falhou: " + (Take(ht_error()) ?? "a ponte não disse por quê") + ".");
    }

    /// Trackers de reserva, num nível depois dos do magnet (para magnet com trackers mortos)
    static void MoreTrackers(IntPtr parameters)
    {
        List<string> spare;
        try { spare = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(Path.Combine(Folder, "trackers.json"))) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { return; }
        Ok(ht_tracker_count(parameters, out var count));
        var known = new HashSet<string>();
        var top = -1;
        for (var i = 0; i < count; i++)
        {
            Ok(ht_tracker_get(parameters, i, out var url, out var tier));
            if (Take(url) is { } u) known.Add(u);
            top = Math.Max(top, tier);
        }
        foreach (var u in spare.Where(u => !known.Contains(u))) Ok(ht_tracker_append(parameters, Utf8(u), top + 1));
    }

    /// Baixa o magnet para a pasta e volta quando o conteúdo inteiro estiver no disco. Cancelado: OperationCanceledException
    public static void Download(string magnet, string folder, Action<DownloadStatus> progress, CancellationToken ct)
    {
        Load();
        Directory.CreateDirectory(folder);
        Ok(ht_session_new(0, out var session));
        try
        {
            Ok(ht_prepare(Utf8(magnet), Utf8(folder), 0, 0, out var parameters));
            IntPtr handle;
            try
            {
                MoreTrackers(parameters);
                Ok(ht_add(session, parameters, out handle));
            }
            finally { ht_params_free(parameters); }
            try
            {
                Ok(ht_resume(handle));
                double average = 0;   // (a velocidade do instante pula muito; a média amortecida dá um tempo que não salta)
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    Ok(ht_get_status(handle, out var s));
                    if (s.Metadata != 0 && s.State is Finished or Seeding) return;
                    var now = Math.Max(0, s.DownloadRate);
                    average = average == 0 ? now : average * 0.9 + now * 0.1;
                    progress(new DownloadStatus(s.Done, s.Wanted, (long)average, s.Peers, s.Seeds, s.Metadata != 0));
                    Thread.Sleep(500);
                }
            }
            finally
            {
                // (só depois de a sessão largar a transferência o disco está escrito; os arquivos ficam)
                ht_pause(handle);
                ht_remove(session, handle, 0);
                ht_handle_free(handle);
            }
        }
        finally { ht_session_free(session); }
    }

    /// Um link direto: o arquivo inteiro na pasta, continuando de onde parou se o servidor deixar
    public static async Task DownloadDirect(string url, string folder, Action<DownloadStatus> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HookshotBuilder");
        using var head = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!head.IsSuccessStatusCode) throw new SourceException($"O link de download respondeu {(int)head.StatusCode}.");
        if (head.Content.Headers.ContentType?.MediaType is "text/html")
            throw new SourceException("O link da fonte abre uma página, não um arquivo. Baixe pelo navegador e escolha o arquivo aqui.");
        var name = head.Content.Headers.ContentDisposition?.FileNameStar ?? head.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                   ?? Path.GetFileName(new Uri(url).LocalPath);
        name = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
        if (name.Length == 0) name = "download";
        var path = Path.Combine(folder, name);
        var total = head.Content.Headers.ContentLength ?? -1;
        if (total > 0 && File.Exists(path) && new FileInfo(path).Length == total) return;
        await using var from = await head.Content.ReadAsStreamAsync(ct);
        await using var to = File.Create(path);
        var b = new byte[1 << 20];
        long done = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int n;
        while ((n = await from.ReadAsync(b, ct)) > 0)
        {
            await to.WriteAsync(b.AsMemory(0, n), ct);
            done += n;
            progress(new DownloadStatus(done, total, (long)(done / Math.Max(clock.Elapsed.TotalSeconds, 0.001)), 0, 0, true));
        }
    }
}

/// Os arquivos compactados do download, juntos numa cópia do jogo: o .nup, ou as pastas romfs e exefs.
/// O QUE VEM DE DENTRO NÃO É CONFIÁVEL: todo nome passa por Safe antes de virar caminho
static class GameArchives
{
    static readonly string[] Exefs = { "exefs", ".hookshot_exefs" };
    const string Mark = "hookshot-fonte.txt";

    /// O que descompactar: o .nup (Nup) ou o que vem antes de romfs e exefs (Prefix), e quanto ocupa
    public sealed record Plan(List<List<string>> Archives, string? Nup, string Prefix, long Bytes);

    static bool IsArchive(string p) => Path.GetExtension(p).ToLowerInvariant() is ".rar" or ".zip" or ".7z";

    /// jogo.part2.rar: lido junto da primeira parte, não como arquivo à parte
    static int Part(string p)
    {
        var name = Path.GetFileName(p).ToLowerInvariant();
        if (!name.EndsWith(".rar")) return 0;
        var stem = name[..^4];
        var i = stem.LastIndexOf(".part", StringComparison.Ordinal);
        return i >= 0 && int.TryParse(stem[(i + 5)..], out var n) ? n : 0;
    }

    /// Os arquivos compactados da pasta (e duas abaixo), cada um com as suas partes, em ordem
    public static List<List<string>> In(string folder)
    {
        var files = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
                       .Where(IsArchive).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        var groups = new List<List<string>>();
        foreach (var f in files.Where(f => Part(f) <= 1))
        {
            if (Part(f) == 1)
            {
                var stem = f[..f.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase)];
                groups.Add(files.Where(g => Part(g) >= 1 && g.StartsWith(stem + ".part", StringComparison.OrdinalIgnoreCase)).OrderBy(Part).ToList());
            }
            else groups.Add(new List<string> { f });
        }
        return groups;
    }

    static IArchive Open(List<string> parts) =>
        parts.Count == 1 ? ArchiveFactory.Open(new FileInfo(parts[0])) : ArchiveFactory.Open(parts.Select(p => new FileInfo(p)));

    static string Key(string? key) => (key ?? "").Replace('\\', '/');

    static bool IsExefs(string part) => Exefs.Any(e => part.Equals(e, StringComparison.OrdinalIgnoreCase));

    static bool InGame(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var first = name[prefix.Length..].Split('/')[0];
        return first.Equals("romfs", StringComparison.OrdinalIgnoreCase) || IsExefs(first);
    }

    /// Junta as entradas de todos e escolhe: o maior .nup, ou romfs e exefs lado a lado
    public static Plan Check(List<List<string>> archives)
    {
        var entries = new List<(string Name, long Size, bool Dir)>();
        foreach (var a in archives)
        {
            try
            {
                using var arc = Open(a);
                entries.AddRange(arc.Entries.Select(e => (Key(e.Key), e.Size, e.IsDirectory)));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new SourceException($"Não deu para ler {Path.GetFileName(a[0])} ({e.Message}).");
            }
        }
        var nup = entries.Where(e => !e.Dir && e.Name.EndsWith(".nup", StringComparison.OrdinalIgnoreCase)).OrderByDescending(e => e.Size).FirstOrDefault();
        if (nup.Name != null) return new Plan(archives, nup.Name, "", nup.Size);
        List<string> Before(Func<string, bool> isIt) => entries.Select(e =>
        {
            var parts = e.Name.Split('/');
            var i = Array.FindIndex(parts, p => isIt(p));
            return i < 0 ? null : string.Concat(parts.Take(i).Select(p => p + "/"));
        }).Where(p => p != null).Select(p => p!).Distinct().ToList();
        var exefs = Before(IsExefs);
        var prefix = Before(p => p.Equals("romfs", StringComparison.OrdinalIgnoreCase)).Where(exefs.Contains).OrderBy(p => p.Length).FirstOrDefault()
                     ?? throw new SourceException("O download não tem o .nup do jogo nem as pastas romfs e exefs.");
        return new Plan(archives, null, prefix, entries.Where(e => InGame(e.Name, prefix)).Sum(e => e.Size));
    }

    /// O caminho de uma entrada dentro da pasta, ou nada se o nome tenta sair dela
    static string? Safe(string rel)
    {
        var parts = rel.Split('/').Where(p => p.Length > 0 && p != ".").ToList();
        if (parts.Count == 0 || parts.Any(p => p == ".." || p.Contains(':') || p.Contains('\\') || p.Any(char.IsControl))) return null;
        return Path.Combine(parts.ToArray());
    }

    /// Para onde uma entrada vai, relativo à pasta. .hookshot_exefs/main vai para exefs/main
    static string? Target(string name, Plan plan)
    {
        if (plan.Nup != null) return name == plan.Nup ? Safe(name.Split('/')[^1]) : null;
        if (!InGame(name, plan.Prefix)) return null;
        var rest = name[plan.Prefix.Length..];
        var slash = rest.IndexOf('/');
        var first = slash < 0 ? rest : rest[..slash];
        return IsExefs(first) ? Safe("exefs/" + (slash < 0 ? "" : rest[(slash + 1)..])) : Safe(rest);
    }

    static string Signature(Plan plan) => string.Join("\n", plan.Archives.SelectMany(a => a).Select(p =>
    {
        var f = new FileInfo(p);
        return $"{f.FullName}|{f.Length}|{f.LastWriteTimeUtc.Ticks}";
    }));

    /// O .nup ou a pasta com romfs e exefs, depois de descompactado em `to`
    static string Result(Plan plan, string to) => plan.Nup != null ? Path.Combine(to, Safe(plan.Nup.Split('/')[^1])!) : to;

    /// Descompacta a cópia em `to` e devolve o .nup ou a pasta com romfs e exefs. progress: (bytes gravados, qual arquivo, entrada).
    /// Os mesmos arquivos de novo: nada é descompactado outra vez
    public static string Unpack(Plan plan, string to, Action<long, int, string> progress, CancellationToken ct)
    {
        var result = Result(plan, to);
        var mark = Path.Combine(to, Mark);
        try
        {
            if (File.ReadAllText(mark) == Signature(plan) && (File.Exists(result) || Directory.Exists(Path.Combine(to, "exefs")))) return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (Directory.Exists(to)) Directory.Delete(to, recursive: true);
        Directory.CreateDirectory(to);
        File.WriteAllText(mark, "parcial");
        long done = 0;
        for (var i = 0; i < plan.Archives.Count; i++)
        {
            var parts = plan.Archives[i];
            progress(done, i, "");
            try
            {
                using var arc = Open(parts);
                // (num arquivo sólido, o que vem antes tem de ser lido para chegar ao que interessa: o leitor vai em ordem)
                using var reader = arc.ExtractAllEntries();
                while (reader.MoveToNextEntry())
                {
                    ct.ThrowIfCancellationRequested();
                    var e = reader.Entry;
                    var name = Key(e.Key);
                    progress(done, i, name);
                    if (e.IsDirectory || Target(name, plan) is not { } rel) continue;
                    var path = Path.Combine(to, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var file = File.Create(path);
                    using var counting = new CountingStream(file, n => { done += n; progress(done, i, name); }, ct);
                    reader.WriteEntryTo(counting);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException and not SourceException)
            {
                throw e is IOException { HResult: unchecked((int)0x80070070) or unchecked((int)0x80070027) }
                    ? new SourceException($"O disco ficou sem espaço descompactando {Path.GetFileName(parts[0])}.")
                    : new SourceException($"Não deu para descompactar {Path.GetFileName(parts[0])} ({e.Message}). O download pode estar incompleto.");
            }
        }
        var complete = plan.Nup != null ? File.Exists(result)
            : Directory.Exists(Path.Combine(to, "romfs")) && File.Exists(Path.Combine(to, "exefs", "main"));
        if (!complete) throw new SourceException("Os arquivos baixados não trazem a cópia do jogo inteira. Algum pode estar incompleto.");
        File.WriteAllText(mark, Signature(plan));
        return result;
    }

    /// Conta o que é gravado e olha o cancelamento a cada pedaço
    sealed class CountingStream(Stream inner, Action<int> wrote, CancellationToken ct) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            ct.ThrowIfCancellationRequested();
            inner.Write(buffer, offset, count);
            wrote(count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ct.ThrowIfCancellationRequested();
            inner.Write(buffer);
            wrote(buffer.Length);
        }
    }
}
