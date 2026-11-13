using System.Diagnostics;
using System.IO;
using Path = System.Windows.Shapes.Path;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace HookshotBuilder;

/// The installer: one 960x600 window. It opens with the hookshot in the middle while it looks for the game files, then walks
/// through Game files > Build > Play. The game's update offer (Updater.cs) opens the same window on its update screen.
public partial class MainWindow : Window
{
    const long NeededBytes = 45L << 30;   // (measured 2026-11-09: the game data 24.4 GB, the shader caches ~8 GB, the game 0.4 GB, the build files ~5 GB at their peak; the rest headroom)
    Language lang = Strings.Default();
    string T(S s) => (int)s < lang.Text.Length ? lang.Text[(int)s] : Strings.All[0].Text[(int)s];

    readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Stopwatch elapsed = new();
    readonly Stopwatch compileClock = new();
    readonly Dictionary<string, StepRow> rows = new();
    readonly Dictionary<string, (bool Ok, string Text)?> checks = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Dump> dumps = new();
    readonly System.Text.StringBuilder log = new();
    Runner? runner;
    string? current;
    string finished = "";
    string? chosenNup, romfs, exefs;
    // (issue 650) "Using the folders ..." above the list of .nup files, while romfs and exefs are the game files: FillDumps clears the
    // list, so the note is made there again each time (it was gone as soon as the list was first filled)
    string? foldersNote;
    TextBlock? foldersNoteBlock;
    bool updating, checking;
    double compiled;
    int filesDone, filesOf;
    int filesChanged = -1, filesCompiled;
    double compileEta = -1;
    long thisCompile, previousTotal = -1;
    string lastFailure = "";

    public MainWindow()
    {
        InitializeComponent();
        OutPath.Text = BuilderHome.DefaultGame;
        if (BuilderHome.Portable) DesktopTick.IsChecked = StartTick.IsChecked = false;
        LanguageBox.ItemsSource = Strings.All;
        LanguageBox.SelectedItem = lang;
        WorkerBox.ItemsSource = BuildWorkers.Choices(Environment.ProcessorCount);
        WorkerBox.SelectedIndex = 0;
        MemoryBox.ItemsSource = BuildMemory.Choices(BuildMemory.TotalGb());
        MemoryBox.SelectedIndex = 0;
        TrackBox.ItemsSource = TrackNames;
        TrackBox.SelectedIndex = Array.IndexOf(TrackIds, Track);
        TrackBox.SelectionChanged += TrackChanged;
        clock.Tick += (_, _) => Tick();
        SourceUrl.Text = Sources.Saved ?? "";   // (fork) a fonte usada da última vez
        Drop += Dropped;
        SourceInitialized += (_, _) =>
        {
            if (!Glass.Apply(this))   // (Windows 10, or no acrylic: plain Slate)
            {
                Background = (Brush)FindResource("Page");
                Root.Background = (Brush)FindResource("Page");
            }
        };
        Loaded += (_, _) => Start();
        var tools = Tools.Find();
        ToolsNote.Text = tools != null
            ? "Nada mais para instalar: as ferramentas de geração do Hookshot estão aqui, baixadas e conferidas pelo instalador dele."
            : "As ferramentas de geração do Hookshot não estão na pasta dele. Clique em Gerar e o Hookshot oferece baixá-las de novo.";
        SetStep(0);
        Refresh();
    }

    // ================= the start =================

    async void Start()
    {
        // (development) HOOKSHOT_TEST_SHORTCUTS=<game folder>: both shortcuts made for it, the result in the log beside the app, and out
        if (Environment.GetEnvironmentVariable("HOOKSHOT_TEST_SHORTCUTS") is { Length: > 0 } testFolder)
        {
            Finder.Shortcuts(testFolder, true, true);
            File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "shortcut-test.txt"),
                              $"desktop {File.Exists(Finder.DesktopLink)} start {File.Exists(Finder.StartLink)} error {Finder.ShortcutError}");
            Application.Current.Shutdown();
            return;
        }
        Animate();
        var started = Stopwatch.StartNew();
        if (Environment.GetCommandLineArgs().Contains("--update"))
        {
            SplashText.Text = "Baixando a atualização…";
            await Task.Delay(900);
            ShowMain();
            Updater.FromCommandLine(this);
            return;
        }
        if (LaunchArg() is { } launchFolder)   // (Hookshot Launcher.exe in the game folder)
        {
            SplashText.Text = "Abrindo o Hookshot…";
            await Task.Delay(700);
            ShowMain();
            ShowLauncher(launchFolder);
            return;
        }
        SplashText.Text = "Procurando os arquivos do seu jogo…";
        var found = await Task.Run(Finder.FindDumps);
        dumps.AddRange(found);
        SplashText.Text = found.Count switch { 0 => "Nenhum arquivo do jogo achado ainda", 1 => "1 arquivo do jogo achado", _ => $"{found.Count} arquivos do jogo achados" };
        var wait = 1700 - (int)started.ElapsedMilliseconds;
        if (wait > 0) await Task.Delay(wait);
        ApplySettings();
        FillDumps();
        filesReady = true;
        ShowMain();
        if (Environment.GetEnvironmentVariable("HOOKSHOT_UI_PREVIEW") is { Length: > 0 } preview) { Preview(preview); return; }
        CheckAll();
        latestTag = Environment.GetEnvironmentVariable("HOOKSHOT_LATEST_TAG") is { Length: > 0 } fake ? (fake == "none" ? null : fake) : await Updater.LatestTag(Track);
        RefreshMode();
    }

    bool filesReady;   // (the first page has its game files and saved choices: a start with --update or --launch goes past that)

    /// The first page again, from a build or an update that stopped or was cancelled. RefreshMode leaves the button alone while
    /// an update runs, and one that stopped left it so for good: the page kept the button it had before. After a start with
    /// --update (the game's or the setup's, once the new builder is in) that was a grey "Build the game" over a page with no
    /// game files and no saved choices, and the Update button only came back when the app was closed and opened again (#2129).
    /// The update is over here: the page is set up if it never was, for the update's folder, and its button says what that needs
    async void ToFiles()
    {
        SetStep(0);
        ShowPage(FilesPage);
        if (!updating) return;
        updating = false;
        if (!filesReady)
        {
            filesReady = true;
            var folder = OutPath.Text;   // (the update's: the saved one is the last build's)
            dumps.AddRange(await Task.Run(Finder.FindDumps));
            ApplySettings();
            OutPath.Text = folder;
            FillDumps();
            CheckAll();
            latestTag ??= Environment.GetEnvironmentVariable("HOOKSHOT_LATEST_TAG") is { Length: > 0 } fake ? (fake == "none" ? null : fake) : await Updater.LatestTag(Track);
        }
        Refresh();
    }

    // ================= what the button does: build, update the game, play it, or get the newer builder =================
    // (as NX2navi's launcher: whatever version the player opens, it checks the latest release and says what is there)

    enum Mode { Build, UpdateGame, UpToDate, UpdateBuilder }
    Mode mode = Mode.Build;
    string? latestTag, updateTarget;
    bool sourcePicked;   // (the player chose game files here: a build from them, even over a game that is up to date)

    /// The release a game folder was built with (its hookshot_version.txt), when it keeps its game files for updates
    static string? GameVersion(string folder)
    {
        try
        {
            if (!Directory.Exists(System.IO.Path.Combine(folder, ".hookshot_exefs")) || !Directory.Exists(System.IO.Path.Combine(folder, "romfs")))
                return null;
            foreach (var line in File.ReadAllLines(System.IO.Path.Combine(folder, "hookshot_version.txt")))
                if (line.StartsWith("version=")) return line[8..].Trim() is { Length: > 0 } v ? v : null;
        }
        catch (Exception) { }
        return null;
    }

    void RefreshMode()
    {
        if (BuildButton == null || updating || runner != null) return;
        var game = OutPath.Text.Length > 2 ? GameVersion(OutPath.Text) : null;
        var builder = Tools.Find()?.Version;
        // (the newest release known: GitHub's, else this builder's own. v2.0.0 took a GitHub that did not answer for "up to date")
        updateTarget = Updater.Target(latestTag, builder, Track);
        if (game != null && updateTarget != null && game != updateTarget && !sourcePicked) mode = Mode.UpdateGame;
        else if (game != null && !sourcePicked) mode = Mode.UpToDate;
        else if (latestTag != null && builder != null && builder != updateTarget) mode = Mode.UpdateBuilder;
        else mode = Mode.Build;
        switch (mode)
        {
            case Mode.UpdateGame:
                FilesTitle.Text = "Atualização disponível";
                FilesSub.Text = $"O Hookshot {updateTarget} está disponível. O seu jogo é o {game}: Atualizar gera de novo a partir dos arquivos do jogo guardados " +
                                "na pasta dele, compilando só o que mudou.";
                BuildButton.Content = "Atualizar";
                break;
            case Mode.UpToDate:
                FilesTitle.Text = "O seu jogo está em dia";
                FilesSub.Text = latestTag == null
                    ? $"O seu jogo é o {game}, igual a este Builder. O GitHub não respondeu, então não deu para procurar versões mais novas."
                    : $"O {game} é a versão mais nova. Escolha arquivos do jogo abaixo só se quiser gerar de novo do zero.";
                BuildButton.Content = "Jogar";
                break;
            case Mode.UpdateBuilder:
                FilesTitle.Text = "Gere o seu jogo";
                FilesSub.Text = $"O Hookshot Builder {updateTarget} está disponível (este é o {builder}): atualize primeiro, depois gere.";
                BuildButton.Content = "Atualizar o Builder";
                break;
            default:
                FilesTitle.Text = "Gere o seu jogo";
                FilesSub.Text = "Escolha os arquivos do jogo e onde ele vai ficar. O Hookshot faz o resto, uma vez só.";
                BuildButton.Content = "Gerar o jogo";
                break;
        }
        BuildButton.IsEnabled = mode != Mode.Build || (SourceReady && OutPath.Text.Length > 2);
        AboutText.Visibility = mode is Mode.Build ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- the launcher: Hookshot Launcher.exe in a game folder starts this with --launch <folder> (builder/setup/setup.c) ----
    string launchFolder = "";

    static string? LaunchArg()
    {
        var a = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(a, "--launch");
        return i >= 0 && i + 1 < a.Length ? a[i + 1].TrimEnd('\\', '/') : null;
    }

    async void ShowLauncher(string folder)
    {
        launchFolder = folder;
        OutPath.Text = folder;
        StepPill.Visibility = Visibility.Collapsed;
        var game = GameVersion(folder) ?? ReadVersion(folder);
        LaunchVersion.Text = (game != null ? $"Hookshot {game}" : "Hookshot") + " · " + folder;
        LaunchPlay.IsEnabled = File.Exists(GameExe(folder));
        ShowPage(LaunchPage);
        latestTag = Environment.GetEnvironmentVariable("HOOKSHOT_LATEST_TAG") is { Length: > 0 } fake ? (fake == "none" ? null : fake) : await Updater.LatestTag(Track);
        updateTarget = Updater.Target(latestTag, Tools.Find()?.Version, Track);   // (GitHub's newest, else this builder's own)
        if (updateTarget == null) LaunchStatus.Text = "Não deu para procurar atualizações (o GitHub não respondeu)";
        else if (game != null && game != updateTarget && GameVersion(folder) != null)
        {
            LaunchStatus.Text = $"Atualização disponível: {updateTarget}";
            LaunchStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0xC8, 0xFF));
            LaunchStatus.FontWeight = FontWeights.SemiBold;
            LaunchUpdate.Visibility = Visibility.Visible;
        }
        else if (game != null && game != updateTarget)
            LaunchStatus.Text = $"O {updateTarget} saiu: gere esta pasta de novo com o Hookshot Builder para ter ele";
        else LaunchStatus.Text = latestTag == null ? "Em dia com este Builder (o GitHub não respondeu)" : "Em dia";
    }

    /// (a folder built before it kept its game files has a version too: only shown)
    static string? ReadVersion(string folder)
    {
        try
        {
            foreach (var line in File.ReadAllLines(System.IO.Path.Combine(folder, "hookshot_version.txt")))
                if (line.StartsWith("version=")) return line[8..].Trim() is { Length: > 0 } v ? v : null;
        }
        catch (Exception) { }
        return null;
    }

    /// The game in a folder: bin\hookshot.exe below its launcher (hookshot.exe), or hookshot.exe in a folder built before the launcher
    static string GameExe(string folder)
    {
        var game = System.IO.Path.Combine(folder, "bin", "hookshot.exe");
        return File.Exists(game) ? game : System.IO.Path.Combine(folder, "hookshot.exe");
    }

    /// (the launcher already checked for updates: the game does not ask again)
    static void StartGame(string folder)
    {
        var si = new ProcessStartInfo(GameExe(folder)) { UseShellExecute = false, WorkingDirectory = folder };
        si.Environment["HOOKSHOT_NO_UPDATE_CHECK"] = "1";
        Process.Start(si);
    }

    void LaunchUpdateClick(object sender, RoutedEventArgs e)
    {
        if (OlderOk(GameVersion(launchFolder))) Updater.UpdateFolder(this, launchFolder, updateTarget, fetched: false, named: Switching);
    }

    // ---- the update track (Updater.cs): the one chosen in the Track menu, saved; by default these tools' own ----
    static readonly string[] TrackIds = { Updater.Release, Updater.Candidate, Updater.Nightly };
    static readonly string[] TrackNames = { "Estável", "Candidata", "Noturna" };
    string? chosenTrack;
    string Track
    {
        get
        {
            if (chosenTrack != null) return chosenTrack;
            var saved = BuilderSettings.Load(SettingsPath)?.Track;
            return saved != null && TrackIds.Contains(saved) ? saved : Updater.TrackOf(Tools.Find()?.Version);
        }
    }
    /// (a track other than these tools' own: its newest is installed, even an older version)
    bool Switching => Track != Updater.TrackOf(Tools.Find()?.Version);

    async void TrackChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TrackBox.SelectedIndex < 0 || TrackIds[TrackBox.SelectedIndex] == Track) return;
        chosenTrack = TrackIds[TrackBox.SelectedIndex];
        SaveSettings();
        latestTag = null;
        RefreshMode();
        latestTag = await Updater.LatestTag(Track);
        RefreshMode();
    }

    /// (going to an older version, from another track: asked first)
    bool OlderOk(string? have) =>
        !Switching || !Updater.Newer(have, updateTarget) ||
        MessageBox.Show(this, $"O {updateTarget} é mais antigo que o {have}, a versão que você tem agora. Instalar o {updateTarget}?", "Hookshot",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    void LaunchPlayClick(object sender, RoutedEventArgs e)
    {
        StartGame(launchFolder);
        Application.Current.Shutdown();
    }

    void LaunchFolderClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(launchFolder) { UseShellExecute = true });

    // ---- Save my pipeline list: the game folder's pipeline list and the "Submit your pipelines" issue's template, filled in,
    // zipped into the Downloads folder by nx2/pipeline_share.py (as the Linux builder and the game's menu do), then the template
    // on the clipboard, the zip shown in its folder and the issue opened. Nothing is uploaded: the player attaches the zip ----
    const string ShareIssue = "https://github.com/NotRexed/Hookshot/issues/2034";

    async void LaunchShareClick(object sender, RoutedEventArgs e)
    {
        var tools = Tools.Find();
        LaunchShareStatus.Visibility = Visibility.Visible;
        if (tools == null)
        {
            LaunchShareStatus.Text = "As ferramentas do Builder não foram achadas: abra ele de novo pela pasta dele.";
            return;
        }
        LaunchShare.IsEnabled = false;
        LaunchShareStatus.Text = "Salvando…";
        var folder = launchFolder;
        var (zip, template, count, error) = await Task.Run(() => SharePipelines(tools, folder, DownloadsFolder()));
        LaunchShare.IsEnabled = true;
        if (zip == null)
        {
            LaunchShareStatus.Text = error ?? "Não deu para salvar a lista de pipelines.";
            return;
        }
        var copied = false;
        try { Clipboard.SetText(template ?? ""); copied = true; } catch (Exception) { }
        var explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");   // (not one found on the path)
        try { Process.Start(new ProcessStartInfo(File.Exists(explorer) ? explorer : "explorer.exe", $"/select,\"{zip}\"") { UseShellExecute = true }); } catch (Exception) { }
        var opened = false;
        try { Process.Start(new ProcessStartInfo(ShareIssue) { UseShellExecute = true }); opened = true; } catch (Exception) { }
        LaunchShareStatus.Text = $"{System.IO.Path.GetFileName(zip)} salvo ({count} pipelines) em {System.IO.Path.GetDirectoryName(zip)}. " +
            (copied ? "O relatório foi copiado. " : "O relatório está no zip. ") +
            (opened ? "A issue #2034 está aberta no navegador: anexe" : $"Abra {ShareIssue} e anexe") +
            " o zip num comentário, depois cole o relatório e diga onde você jogou.";
    }

    /// nx2/pipeline_share.py --json: (the zip, the template, the pipelines) or the reason there is none
    static (string? Zip, string? Template, int Count, string? Error) SharePipelines(Tools tools, string folder, string outDir)
    {
        var si = new ProcessStartInfo(tools.Python)
        {
            WorkingDirectory = tools.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        si.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var a in tools.PythonArgs ?? Array.Empty<string>()) si.ArgumentList.Add(a);
        si.ArgumentList.Add("-I");
        si.ArgumentList.Add(System.IO.Path.Combine(tools.Root, "nx2", "pipeline_share.py"));
        si.ArgumentList.Add(folder);
        si.ArgumentList.Add("--out-dir");
        si.ArgumentList.Add(outDir);
        si.ArgumentList.Add("--json");
        try
        {
            using var p = Process.Start(si)!;
            var stderr = p.StandardError.ReadToEndAsync();   // (drained, so a long complaint cannot stall it)
            var line = p.StandardOutput.ReadToEnd().Trim().Split('\n').LastOrDefault() ?? "";
            p.WaitForExit();
            if (!line.StartsWith("{"))   // (the script stopped before its answer: what it said last)
            {
                var last = stderr.Result.Trim().Split('\n').LastOrDefault()?.Trim();
                return (null, null, 0, "Não deu para salvar a lista de pipelines" + (string.IsNullOrEmpty(last) ? "." : ": " + last));
            }
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (r.GetProperty("ok").GetBoolean())
                return (r.GetProperty("zip").GetString(), r.GetProperty("template").GetString(), r.GetProperty("pipelines").GetInt32(), null);
            return (null, null, 0, r.GetProperty("error").GetString());
        }
        catch (Exception ex)
        {
            return (null, null, 0, tools.OwnPythonMissing ? Tools.OwnPythonMissingText : "Não deu para salvar a lista de pipelines: " + ex.Message);
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
    static extern int SHGetKnownFolderPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid id,
                                           uint flags, IntPtr token, out string path);

    /// Where a browser's file picker opens: the Downloads folder, else the user's own
    static string DownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var path) == 0 && Directory.Exists(path))
                return path;
        }
        catch (Exception) { }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    // ---- what the player chose last time (BuilderSettings.cs): builder-settings.json beside the versions ----
    static string SettingsPath => System.IO.Path.Combine(Storage.Base, "builder-settings.json");

    void ApplySettings()
    {
        var s = BuilderSettings.Load(SettingsPath);
        if (s == null) return;
        s.Out = BuilderHome.LoadPath(s.Out);
        s.Nup = BuilderHome.LoadPath(s.Nup);
        s.Romfs = BuilderHome.LoadPath(s.Romfs);
        s.Exefs = BuilderHome.LoadPath(s.Exefs);
        PrepareTick.IsChecked = !s.PrepareLater;
        if (!string.IsNullOrEmpty(s.Out)) OutPath.Text = s.Out;
        DlssTick.IsChecked = s.Dlss;
        dlssLicense = s.DlssLicense;
        FsrTick.IsChecked = s.Fsr;
        fsrLicense = s.FsrLicense;
        MemoryBox.SelectedItem = BuildMemory.Saved(MemoryBox.ItemsSource.Cast<object>(), s.MemoryGb);
        if (s.Romfs != null && s.Exefs != null && Directory.Exists(s.Romfs) && File.Exists(System.IO.Path.Combine(s.Exefs, "main")))
        {
            romfs = s.Romfs;
            exefs = s.Exefs;
            foldersNote = $"Usando as pastas {romfs} e {exefs} (como da última vez)";
        }
        else if (s.Nup != null && File.Exists(s.Nup))
        {
            if (!dumps.Any(d => string.Equals(d.Path, s.Nup, StringComparison.OrdinalIgnoreCase)))
            {
                var info = new FileInfo(s.Nup);
                dumps.Insert(0, new Dump(info.FullName, "Da última vez", info.Length, info.LastWriteTime));
            }
            chosenNup = s.Nup;
        }
    }

    void SaveSettings()
    {
        try
        {
            var s = new BuilderSettings { Out = BuilderHome.SavePath(OutPath.Text),
                                          Nup = BuilderHome.SavePath(romfs == null ? chosenNup : null),
                                          Romfs = BuilderHome.SavePath(romfs), Exefs = BuilderHome.SavePath(exefs),
                                          Dlss = DlssTick.IsChecked == true, DlssLicense = dlssLicense,
                                          Fsr = FsrTick.IsChecked == true,
                                          FsrLicense = fsrLicense,
                                          PrepareLater = PrepareTick.IsChecked != true,
                                          MemoryGb = MemoryBox.SelectedItem is int gb ? gb : null,
                                          Track = chosenTrack ?? BuilderSettings.Load(SettingsPath)?.Track };
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(s));
        }
        catch (Exception) { }
    }

    /// (development) HOOKSHOT_UI_PREVIEW=build|ready|problem|update: one screen with made-up progress, nothing run
    void Preview(string what)
    {
        switch (what)
        {
            case "build":
                MakeRows(update: false, shaders: true);
                SetStep(1);
                ShowPage(BuildPage);
                rows["read"].Begin(); rows["read"].Done(); rows["read"].Small("Versão conferida");
                rows["lift"].Begin(); rows["lift"].Done();
                rows["compile"].Begin(); rows["compile"].Small("31 de 58 arquivos");
                SetProgress(0.54);
                BuildNote.Text = "Faltam uns 19 minutos · o seu PC continua livre para usar";
                break;
            case "fonte":   // (fork) baixando da fonte
                StageList.Children.Clear();
                rows.Clear();
                foreach (var (id, text, i) in new[] { ("find", "Procurando o jogo na sua fonte", 1), ("get", "Baixando o jogo", 2), ("unpack", "Juntando e descompactando os arquivos", 3) })
                {
                    var row = new StepRow(i, text, last: i == 3);
                    rows[id] = row;
                    StageList.Children.Add(row.Root);
                }
                SetStep(1);
                ShowPage(BuildPage);
                BuildTitle.Text = "Baixando o seu jogo";
                rows["find"].Begin(); rows["find"].Done(); rows["find"].Small("The Legend of Zelda: Ocarina of Time (Switch 2)");
                rows["get"].Begin(); rows["get"].Small("10,3 GB de 22,6 GB · 14,2 MB/s · 38 pares, 12 fontes");
                SetProgress(0.7 * 10.3 / 22.6);
                BuildNote.Text = "Faltam uns 15 minutos para o download";
                break;
            case "ready":
                finished = OutPath.Text;
                ShowReady();
                break;
            case "problem":
                ShowProblem("Faltam arquivos do compilador do Builder (clang.exe em tools\\llvm-mingw\\bin).", tools: true);
                break;
            case "update":
                BeginUpdate(OutPath.Text, "honest-eta");
                rows["download"].Done(); rows["download"].Small("3 MB · ferramentas sem mudança");
                rows["read"].Begin(); rows["read"].Done();
                rows["lift"].Begin(); rows["lift"].Done();
                rows["compile"].Begin(); rows["compile"].Small("7 de 516 arquivos");
                SetProgress(0.62);
                BuildNote.Text = "Faltam uns 3 minutos. O seu jogo atual fica como está até o novo ficar pronto.";
                break;
        }
    }

    void Animate()
    {
        var forever = RepeatBehavior.Forever;
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        HaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1.04, TimeSpan.FromSeconds(1.2)) { AutoReverse = true, RepeatBehavior = forever, EasingFunction = ease });
        HaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1.04, TimeSpan.FromSeconds(1.2)) { AutoReverse = true, RepeatBehavior = forever, EasingFunction = ease });
        Halo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.6, 1, TimeSpan.FromSeconds(1.2)) { AutoReverse = true, RepeatBehavior = forever, EasingFunction = ease });
        BobLift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -6, TimeSpan.FromSeconds(1.2)) { AutoReverse = true, RepeatBehavior = forever, EasingFunction = ease });
        BobTurn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-4, 2, TimeSpan.FromSeconds(1.2)) { AutoReverse = true, RepeatBehavior = forever, EasingFunction = ease });
        Sweep.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-88, 208, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = forever, EasingFunction = ease });
    }

    /// The hookshot fades out and the first step fades in, in the same window
    void ShowMain()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(450));
        fade.Completed += (_, _) => { Splash.Visibility = Visibility.Collapsed; StopSplash(); };
        Splash.BeginAnimation(OpacityProperty, fade);
        SplashScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.04, TimeSpan.FromMilliseconds(500)));
        SplashScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.04, TimeSpan.FromMilliseconds(500)));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Main.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(500)) { BeginTime = TimeSpan.FromMilliseconds(150) });
        MainLift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(600)) { BeginTime = TimeSpan.FromMilliseconds(150), EasingFunction = ease });
        Main.IsHitTestVisible = true;
    }

    void StopSplash()
    {
        foreach (var (o, p) in new (Animatable, DependencyProperty)[] { (HaloScale, ScaleTransform.ScaleXProperty), (HaloScale, ScaleTransform.ScaleYProperty),
                     (BobLift, TranslateTransform.YProperty), (BobTurn, RotateTransform.AngleProperty), (Sweep, TranslateTransform.XProperty) })
            o.BeginAnimation(p, null);
        Halo.BeginAnimation(OpacityProperty, null);
    }

    // ================= the step pill: Game files > Build > Play =================

    void SetStep(int on)
    {
        StepItems.Children.Clear();
        var names = new[] { "Arquivos do jogo", "Geração", "Jogar" };
        for (var i = 0; i < names.Length; i++)
        {
            if (i > 0)
                StepItems.Children.Add(new Path { Data = Geometry.Parse("M0,0 L4,4 L0,8"), Stroke = (Brush)FindResource("InkSoft"), StrokeThickness = 1.6,
                                                  Opacity = 0.45, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 5, 0) });
            var done = i < on;
            var text = new TextBlock { Text = names[i], FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                                       FontWeight = i == on ? FontWeights.SemiBold : FontWeights.Medium,
                                       Foreground = (Brush)FindResource(i == on ? "Ink" : done ? "InkSoft" : "InkFaint") };
            var inner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (done)
            {
                var tick = new Grid { Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0) };
                tick.Children.Add(new Ellipse { Fill = (Brush)FindResource("GoSoft") });
                tick.Children.Add(new Path { Data = Geometry.Parse("M0,4 L3.5,7.5 L10,0"), Stroke = (Brush)FindResource("Go"), StrokeThickness = 1.8,
                                             HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                inner.Children.Add(tick);
            }
            inner.Children.Add(text);
            var pill = new Border { Height = 30, CornerRadius = new CornerRadius(15), Padding = done ? new Thickness(4, 0, 14, 0) : new Thickness(14, 0, 14, 0), Child = inner };
            if (i == on)
            {
                text.Foreground = Brushes.White;
                pill.Background = new LinearGradientBrush(Color.FromRgb(0x4B, 0x70, 0xF0), Color.FromRgb(0x35, 0x57, 0xD6), 90);
                pill.Effect = new DropShadowEffect { Color = Color.FromRgb(0x3D, 0x63, 0xE6), BlurRadius = 18, ShadowDepth = 6, Direction = 270, Opacity = 0.6 };
            }
            StepItems.Children.Add(pill);
        }
        StepPill.Visibility = Visibility.Visible;
        VersionChip.Visibility = Visibility.Collapsed;
    }

    void ShowPage(FrameworkElement page)
    {
        foreach (var p in new FrameworkElement[] { FilesPage, BuildPage, ReadyPage, ProblemPage, LaunchPage })
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        page.Opacity = 0;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)));
    }

    // ================= 1: game files =================

    void FillDumps()
    {
        DumpList.Children.Clear();
        foldersNoteBlock = null;
        if (romfs != null && exefs != null && foldersNote != null)
        {
            foldersNoteBlock = new TextBlock { Text = foldersNote, Style = (Style)FindResource("Note"), Margin = new Thickness(16, 12, 16, 12) };
            DumpList.Children.Add(foldersNoteBlock);
        }
        if (dumps.Count == 0)
        {
            DumpList.Children.Add(new TextBlock
            {
                Text = "Nenhum arquivo .nup em Downloads, Área de Trabalho, Documentos ou nas primeiras pastas dos seus discos. Procure o seu, solte ele nesta janela ou baixe da sua fonte abaixo.",
                Style = (Style)FindResource("Note"), Margin = new Thickness(16, 14, 16, 14),
            });
            return;
        }
        for (var i = 0; i < dumps.Count; i++)
        {
            var d = dumps[i];
            if (i > 0) DumpList.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("GlassLine") });
            var name = new TextBlock { Text = System.IO.Path.GetFileName(d.Path), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var meta = new TextBlock { Text = $"{d.Where} · {Finder.Size(d.Size)} · {Finder.Ago(d.Modified)}", Style = (Style)FindResource("Faint") };
            var texts = new StackPanel { Children = { name, meta }, Margin = new Thickness(0, 0, 12, 0) };
            // (issue 563: "Can't be read" said why only in the chip's tooltip, which players did not find; the reason is in the row)
            if (checks.TryGetValue(d.Path, out var why) && why is { Ok: false } && !why.Value.Text.StartsWith("This is build"))
                texts.Children.Add(new TextBlock { Text = why.Value.Text, Style = (Style)FindResource("Faint"), TextWrapping = TextWrapping.Wrap,
                                                   Foreground = (Brush)FindResource("Bad"), Margin = new Thickness(0, 4, 0, 0) });
            var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
            var chip = Chip(d.Path);
            Grid.SetColumn(chip, 1);
            grid.Children.Add(texts);
            grid.Children.Add(chip);
            var row = new RadioButton { Style = (Style)FindResource("DumpRow"), GroupName = "Dumps", Content = grid, Tag = d.Path, ToolTip = d.Path,
                                        IsChecked = string.Equals(d.Path, chosenNup, StringComparison.OrdinalIgnoreCase) };
            row.Checked += (_, _) => Choose(d.Path);
            DumpList.Children.Add(row);
        }
    }

    Border Chip(string path)
    {
        checks.TryGetValue(path, out var c);
        var (text, brush) = c == null ? ("Conferindo…", "InkFaint")
            : c.Value.Ok ? ("✓ Versão compatível", "Go")
            : c.Value.Text.StartsWith("This is build") ? ("Não é a versão compatível", "Warn")
            : ("Não dá para ler", "Bad");
        var chip = new Border { Style = (Style)FindResource("Chip"), Child = new TextBlock { Text = text, FontSize = 12, Foreground = (Brush)FindResource(brush), VerticalAlignment = VerticalAlignment.Center } };
        if (c is { Ok: false }) chip.ToolTip = c.Value.Text;
        if (c is { Ok: true }) chip.Background = (Brush)FindResource("GoSoft");
        return chip;
    }

    /// Each found file's version, one at a time (the check unpacks into one probe folder); the first good one is chosen
    async void CheckAll()
    {
        if (checking) return;
        checking = true;
        var tools = Tools.Find();
        while (tools != null && dumps.FirstOrDefault(d => !checks.ContainsKey(d.Path)) is { } d)
        {
            checks[d.Path] = null;
            FillDumps();
            var r = await Runner.Check(tools, new[] { "--check", "--nup", d.Path });
            checks[d.Path] = r;
            if (!r.Ok && r.Text.Contains("antivirus", StringComparison.OrdinalIgnoreCase))
            {
                checking = false;
                ShowProblem(r.Text, tools: true);
                return;
            }
            // (issue 650: not over the romfs and exefs folders the player chose, here or last time: the found .nup showed as chosen)
            if (chosenNup == null && romfs == null && r.Ok) chosenNup = d.Path;
            FillDumps();
            Refresh();
        }
        if (chosenNup == null && romfs == null && dumps.Count > 0 && tools != null) { chosenNup = dumps[0].Path; FillDumps(); }
        checking = false;
        Refresh();
        // (fork) o .nup baixado da fonte passou na conferência: a geração segue sozinha
        if (buildWhenChecked)
        {
            buildWhenChecked = false;
            if (SourceReady) BuildNow();
        }
    }

    void Choose(string path)
    {
        sourcePicked = true;
        chosenNup = path;
        romfs = exefs = null;
        if (foldersNoteBlock != null) DumpList.Children.Remove(foldersNoteBlock);   // (not the whole list again: this row's own event)
        foldersNoteBlock = null;
        Refresh();
    }

    void AddDump(string path, string where)
    {
        var info = new FileInfo(path);
        if (!dumps.Any(d => string.Equals(d.Path, info.FullName, StringComparison.OrdinalIgnoreCase)))
            dumps.Insert(0, new Dump(info.FullName, where, info.Length, info.LastWriteTime));
        sourcePicked = true;
        chosenNup = info.FullName;
        romfs = exefs = null;
        FillDumps();
        CheckAll();
        Refresh();
    }

    bool SourceReady => romfs != null && exefs != null
        || chosenNup != null && checks.TryGetValue(chosenNup, out var c) && c is { Ok: true };

    void PathsChanged(object sender, RoutedEventArgs e) => Refresh();

    void Refresh()
    {
        if (BuildButton == null) return;
        BuildButton.IsEnabled = SourceReady && OutPath.Text.Length > 2;
        ShowWork();
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(OutPath.Text))!;
            var free = new DriveInfo(root).AvailableFreeSpace;
            SpaceBar.Value = Math.Min(1, NeededBytes / (double)Math.Max(1, free));
            SpaceBar.Foreground = (Brush)FindResource(free >= NeededBytes ? "Go" : "Bad");
            SpaceText.Text = $"Precisa de 45 GB · {root.TrimEnd('\\')} tem {free >> 30} GB livres";
            SpaceText.Foreground = (Brush)FindResource(free >= NeededBytes ? "InkSoft" : "Bad");
        }
        catch (Exception)
        {
            SpaceText.Text = "Precisa de 45 GB";
        }
        RefreshMode();
    }

    /// The game folder or the build files folder on a hard drive: the build works, but much more slowly than on an SSD. Null
    /// when both are on an SSD (or their drive cannot be told, as on a network share)
    string? HardDriveWarning()
    {
        foreach (var (what, folder) in new[] { ("pasta do jogo", OutPath.Text), ("pasta dos arquivos de geração", Storage.Work) })
        {
            if (Disk.Spinning(folder) != true) continue;
            string root;
            try { root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(folder))!.TrimEnd('\\'); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { root = folder; }
            return $"A {what} está num HD ({root}). Num SSD o jogo é gerado bem mais rápido: gere lá se puder.\n\n" +
                   "Escolha OK para gerar aqui mesmo assim, ou Cancelar para escolher outra pasta.";
        }
        return null;
    }

    void Dropped(object sender, DragEventArgs e)
    {
        if (runner != null || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        foreach (var p in paths)
        {
            if (p.EndsWith(".nup", StringComparison.OrdinalIgnoreCase) && File.Exists(p)) AddDump(p, "Solto na janela");
            else if (Directory.Exists(System.IO.Path.Combine(p, "romfs")) && Directory.Exists(System.IO.Path.Combine(p, "exefs")))
                UseFolders(System.IO.Path.Combine(p, "romfs"), System.IO.Path.Combine(p, "exefs"));
        }
    }

    void BrowseNup(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "Arquivo do jogo (*.nup)|*.nup", Title = "Escolha o arquivo do seu jogo" };
        if (d.ShowDialog(this) == true) AddDump(d.FileName, "Escolhido");
    }

    /// The other way in: an unpacked game, its romfs and exefs folders (or one folder holding both)
    void BrowseFolders(object sender, RoutedEventArgs e)
    {
        var both = PickFolder("Escolha a pasta com romfs e exefs (ou a pasta romfs)");
        if (both == null) return;
        if (Directory.Exists(System.IO.Path.Combine(both, "romfs")) && Directory.Exists(System.IO.Path.Combine(both, "exefs")))
        {
            UseFolders(System.IO.Path.Combine(both, "romfs"), System.IO.Path.Combine(both, "exefs"));
            return;
        }
        var ex = PickFolder("Agora escolha a pasta exefs");
        if (ex != null) UseFolders(both, ex);
    }

    void UseFolders(string r, string x)
    {
        if (!File.Exists(System.IO.Path.Combine(x, "main")))
        {
            MessageBox.Show(this, "Essa pasta exefs não tem o arquivo main.", "Hookshot");
            return;
        }
        sourcePicked = true;
        romfs = r;
        exefs = x;
        chosenNup = null;
        foldersNote = $"Usando as pastas {r} e {x}";
        FillDumps();
        Refresh();
    }

    static string? PickFolder(string title)
    {
        var d = new OpenFolderDialog { Title = title };
        return d.ShowDialog() == true ? d.FolderName : null;
    }

    void BrowseOut(object sender, RoutedEventArgs e) => OutPath.Text = PickFolder("Escolha onde o jogo vai ficar") ?? OutPath.Text;

    void BrowseWork(object sender, RoutedEventArgs e)
    {
        if (PickFolder("Escolha onde o Hookshot guarda os arquivos de geração") is not { } picked) return;
        ChangeWork(picked);
    }

    void ResetWork(object sender, RoutedEventArgs e) => ChangeWork(null);

    void ChangeWork(string? picked)
    {
        try { Storage.MoveWork(picked); }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"O Hookshot não pode guardar os arquivos de geração aí: {x.Message}", "Hookshot");
        }
        Refresh();
    }

    /// How much the build files take and where, said before the build (they stay after it, for quick updates)
    void ShowWork()
    {
        if (WorkNote == null) return;
        var work = Storage.Work;
        WorkReset.Visibility = Storage.WorkChosen ? Visibility.Visible : Visibility.Collapsed;
        WorkNote.Text = Storage.KeepBuildFiles
            ? $"Arquivos de geração: uns {Storage.Gb(Storage.BuildFilesBytes)}, guardados em {work} para as atualizações serem rápidas (HOOKSHOT_KEEP_BUILD_FILES)."
            : $"Arquivos de geração: uns {Storage.Gb(Storage.BuildFilesBytes)} em {work} durante a geração; com o jogo pronto, fica 1 GB (o código compilado) para as atualizações compilarem só o que mudou.";
        WorkNote.Foreground = (Brush)FindResource("InkSoft");
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(work))!;
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < Storage.BuildFilesBytes)
            {
                WorkNote.Text += $" {root.TrimEnd('\\')} só tem {Storage.Gb(free)} livres.";
                WorkNote.Foreground = (Brush)FindResource("Bad");
            }
        }
        catch (Exception) { }
    }

    void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedItem is not Language l) return;
        lang = l;
        // (fork: a janela fica em português; o idioma escolhido é o do jogo)
        CancelButton.Content = "Cancelar";
        BackButton.Content = "Voltar";
        OpenButton.Content = "Abrir a pasta";
        PlayText.Text = "Jogar";
        ShowLog.Content = ShowLog.IsChecked == true ? "Esconder detalhes" : "Mostrar detalhes";
    }

    // ================= 2: the build =================

    async void StartBuild(object sender, RoutedEventArgs e)
    {
        switch (mode)
        {
            case Mode.UpdateGame:   // (the kept game files; the newer builder first when this one is older)
                if (!OlderOk(GameVersion(OutPath.Text))) return;
                SaveSettings();
                Updater.UpdateFolder(this, OutPath.Text, updateTarget, fetched: false, named: Switching);
                return;
            case Mode.UpToDate:
                finished = OutPath.Text;
                Play(sender, e);
                return;
            case Mode.UpdateBuilder:
                if (!OlderOk(Tools.Find()?.Version)) return;
                SaveSettings();
                BuildButton.IsEnabled = false;
                BuildButton.Content = "Baixando o Builder novo…";
                try { await Updater.RunLatestSetup(this, Array.Empty<string>(), updateTarget, Switching); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message + " Você também pode baixar o HookshotBuilder.exe na página de versões.", "Hookshot");
                    RefreshMode();
                }
                return;
        }
        BuildNow();
    }

    /// The build itself, from the chosen game files (also the end of "Baixar da sua fonte", whatever the button would say)
    void BuildNow()
    {
        SaveSettings();
        var tools = Tools.Find();
        if (tools == null)
        {
            ShowProblem("As ferramentas de geração não estão na pasta do Hookshot.", tools: true);
            return;
        }
        var args = new List<string>();
        if (romfs != null && exefs != null) args.AddRange(new[] { "--romfs", romfs, "--exefs", exefs });
        else if (chosenNup != null) args.AddRange(new[] { "--nup", chosenNup });
        else return;
        args.AddRange(new[] { "--out", OutPath.Text, "--language", lang.Code });
        if (FsrTick.IsChecked == true) args.Add("--fsr");   // (an --update of the folder keeps it: make_game.py's mark)
        prepareNow = PrepareTick.IsChecked == true;
        Run(tools, ShaderStep.Args(args, prepareNow), dlss: DlssTick.IsChecked == true,
            fsr: FsrTick.IsChecked == true && fsrLicense);
    }

    const string DlssLicense =
        "O NVIDIA DLSS é da NVIDIA, não do Hookshot. Marcado, a geração baixa o SDK do DLSS da NVIDIA (versão v310.9.16) de " +
        "github.com/NVIDIA/DLSS, sob a licença da própria NVIDIA, a licença dos NVIDIA RTX SDKs:\n\n" +
        "https://github.com/NVIDIA/DLSS/blob/9981c6350dc58c0ce21f024fd65e31e851c31689/LICENSE.txt\n\n" +
        "Os arquivos dela ficam neste PC (na pasta ngx-sdk do Builder, e nvngx_dlss.dll e nvngx_dlssg.dll na pasta ngx " +
        "da pasta do jogo). " +
        "O Hookshot nunca inclui nem compartilha esses arquivos: nunca compartilhe uma pasta de jogo gerada com eles.\n\n" +
        "Você aceita a licença da NVIDIA?";

    /// NVIDIA's license accepted on the DLSS tick, kept in the settings (DlssLicense): an update keeps a folder's DLSS by it.
    /// It was the SDK's LICENSE.txt, a download: with the SDK gone, the update built without DLSS and its runtime went
    bool dlssLicense;

    /// The NVIDIA DLSS tick: NVIDIA's license first, and the tick stays only when it is accepted
    void DlssChanged(object sender, RoutedEventArgs e)
    {
        if (DlssTick.IsChecked == true &&
            MessageBox.Show(this, DlssLicense, "NVIDIA DLSS", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            DlssTick.IsChecked = false;
        else if (DlssTick.IsChecked == true)
            dlssLicense = true;
        SaveSettings();   // (kept at once, not only when Build is pressed: ticked and closed, it is still ticked next time)
    }

    const string FsrLicense =
        "O AMD FSR 4 é da AMD, não do Hookshot. Marcado, a geração baixa o runtime do FSR da AMD para Vulkan (FidelityFX SDK " +
        "v2.4.0: amd_fidelityfx_loader_vk.dll e amd_fidelityfx_upscaler_vk.dll, assinados pela AMD) de " +
        "github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK, cada arquivo conferido pelo SHA-256, sob a licença do FSR SDK da AMD:\n\n" +
        "https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/7539ae8b6568ad29ed78a753404351daf3b5cca7/Kits/FidelityFX/docs/license.md\n\n" +
        "Os arquivos dela ficam neste PC (na pasta ffx-sdk do Builder, e as duas DLLs com o LICENSE-AMD-FSR-SDK.md na pasta " +
        "do jogo).\n\n" +
        "Você aceita a licença da AMD?";

    /// AMD's license accepted on the FSR 4 tick. A tick saved before the builder asked builds with FSR 4 as it did, its DLLs
    /// the player's own, and fetches nothing until it is ticked again
    bool fsrLicense;

    /// The AMD FSR 4 tick: AMD's license first, and the tick stays only when it is accepted (as DLSS's; this builder is
    /// Windows', where AMD publishes the runtime)
    void FsrChanged(object sender, RoutedEventArgs e)
    {
        if (FsrTick.IsChecked == true)
        {
            fsrLicense = MessageBox.Show(this, FsrLicense, "AMD FSR 4", MessageBoxButton.YesNo, MessageBoxImage.Information) ==
                         MessageBoxResult.Yes;
            if (!fsrLicense) FsrTick.IsChecked = false;
        }
        SaveSettings();
    }

    static readonly (string Id, string Build, string Update)[] Stages =
    {
        ("download", "", "Baixar a atualização"),
        ("read", "Ler os arquivos do seu jogo", "Ler os arquivos do jogo guardados na pasta"),
        ("lift", "Traduzir o jogo para código de PC", "Traduzir o jogo para código de PC"),
        ("compile", "Compilar", "Compilar os arquivos que mudaram"),
        ("finish", "Montar o jogo", "Trocar pelo jogo novo"),
        (ShaderStep.Id, "Preparar os shaders", "Preparar os shaders"),
    };
    static readonly Dictionary<string, (double From, double Size)> WithShaders = new()
    {
        ["download"] = (0, 0.04), ["read"] = (0.04, 0.04), ["lift"] = (0.08, 0.14), ["compile"] = (0.22, 0.68), ["finish"] = (0.90, 0.02),
        [ShaderStep.Id] = (0.92, 0.08),   // (hookshot --prepare-shaders: minutes on some GPUs, the first start's wait moved here)
    };
    static readonly Dictionary<string, (double From, double Size)> WithoutShaders = new()
    {
        ["download"] = (0, 0.04), ["read"] = (0.04, 0.04), ["lift"] = (0.08, 0.14), ["compile"] = (0.22, 0.74), ["finish"] = (0.96, 0.04),
    };
    Dictionary<string, (double From, double Size)> Weight = WithoutShaders;
    // (this build prepares the shaders: the tick for a build, the saved choice for an update. The game's @@stage shaders came, and
    // make_game's line when it left them to the first start: the row's end says which)
    bool prepareNow, shadersBegan;
    string? shadersLater;
    TimeSpan? builtIn;   // (at @@times: the build without the shaders' minutes, as build_times.json keeps it)

    void MakeRows(bool update, bool shaders)
    {
        StageList.Children.Clear();
        rows.Clear();
        Weight = shaders ? WithShaders : WithoutShaders;
        var list = Stages.Where(s => (update || s.Id != "download") && (shaders || s.Id != ShaderStep.Id)).ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var row = new StepRow(i + 1, update ? list[i].Update : list[i].Build, last: i == list.Count - 1);
            rows[list[i].Id] = row;
            StageList.Children.Add(row.Root);
        }
        if (update) rows["read"].Small("Os arquivos do seu jogo estão guardados: o .nup não é necessário");
    }

    /// dlss: NVIDIA DLSS, its license accepted: NVIDIA's SDK fetched into Storage.NgxSdk first (only what is missing), and built in.
    /// fsr: AMD FSR 4, its license accepted: built with it, AMD's runtime fetched into Storage.FfxSdk and put in the game folder
    void Run(Tools tools, List<string> args, bool keepRows = false, bool dlss = false, bool fsr = false)
    {
        // (a warning, not a stop: the player may have no other folder with the room. An update keeps its rows and its own flow)
        if (!keepRows && HardDriveWarning() is { } slow &&
            MessageBox.Show(this, slow, "Hookshot", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        int? workers, memoryGb;
        try
        {
            workers = BuildWorkers.Selected(WorkerBox.SelectedItem);
            memoryGb = BuildMemory.Selected(MemoryBox.SelectedItem);
        }
        catch (ArgumentException e)
        {
            MessageBox.Show(this, e.Message, "Hookshot");
            return;
        }
        if (!keepRows)
        {
            updating = false;
            MakeRows(update: false, shaders: prepareNow);
            SetStep(1);
            BuildTitle.Text = "Gerando o seu jogo";
            UpdateBar.Visibility = Visibility.Collapsed;
            RingBox.Visibility = Visibility.Visible;
        }
        log.Clear();
        Log.Clear();
        current = null;
        finished = "";
        compiled = 0;
        filesDone = filesOf = 0;
        filesChanged = -1;
        filesCompiled = 0;
        compileEta = -1;
        thisCompile = 0;
        previousTotal = -1;
        shadersBegan = false;
        shadersLater = null;
        builtIn = null;
        ShowPage(BuildPage);
        CancelButton.Visibility = Visibility.Visible;
        SetProgress(updating ? Weight["read"].From : 0);
        elapsed.Restart();
        compileClock.Reset();
        clock.Start();
        Tick();
        if (Storage.WorkChosen && !args.Contains("--work")) args.AddRange(new[] { "--work", Storage.Work });   // (the build files where the player chose)
        if (dlss && !args.Contains("--fetch-ngx-sdk")) args.AddRange(new[] { "--fetch-ngx-sdk", "--accept-nvidia-license" });
        if (fsr && !args.Contains("--fsr")) args.Add("--fsr");
        if (fsr && !args.Contains("--fetch-fsr-runtime")) args.AddRange(new[] { "--fetch-fsr-runtime", "--accept-amd-license" });
        runner = new Runner(tools, args, workers, dlss ? Storage.NgxSdk : null, memoryGb, fsr ? Storage.FfxSdk : null);
        runner.Line += l => Dispatcher.BeginInvoke(() => Line(l));
        runner.Finished += failure => Dispatcher.BeginInvoke(() => Ended(failure));
        runner.Start();
    }

    static readonly Regex FilesLine = new(@"^(\d+) files, "), FileLine = new(@"^\s+\S+\s+\d+ MB\s+\d+s");

    void Line(string line)
    {
        if (line.StartsWith("@@stage "))
        {
            var id = line.Split(' ')[1];
            if (current != null && rows.TryGetValue(current, out var was)) was.Done();
            current = id;
            if (rows.TryGetValue(id, out var row)) row.Begin();
            if (id == "compile") compileClock.Restart();
            if (id == ShaderStep.Id) shadersBegan = true;
            if (Weight.TryGetValue(id, out var w)) SetProgress(w.From);
        }
        else if (line.StartsWith("@@progress "))
        {
            if (double.TryParse(line[11..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) && current != null
                && Weight.TryGetValue(current, out var w))
            {
                if (current == "compile") compiled = f;
                SetProgress(w.From + w.Size * Math.Clamp(f, 0, 1));
            }
            return;
        }
        else if (line.StartsWith("@@plan ") || line.StartsWith("@@file ") || line.StartsWith("@@eta ") || line.StartsWith("@@times "))
        {
            Number(line);
            return;
        }
        else if (line.StartsWith("@@done ")) finished = line[7..];
        else if (ShaderStep.Reason(line) is { } later) shadersLater = later;
        else if (FilesLine.Match(line) is { Success: true } m) filesOf = int.Parse(m.Groups[1].Value);
        else if (FileLine.IsMatch(line)) filesDone++;
        if (current == "compile" && filesChanged < 0 && filesOf > 0 && rows.TryGetValue("compile", out var c))
            c.Small($"{filesDone} de {filesOf} arquivos");
        AddLog(line);
    }

    /// The compile timer's lines: build.py says which files this run really rebuilds and how long each took before (the
    /// countdown), and make_game.py gives this build's times beside the last one's. Not shown in details: the compiler's
    /// own lines there already carry each file's seconds.
    void Number(string line)
    {
        var p = line.Split(' ');
        switch (p[0])
        {
            case "@@plan" when p.Length == 3 && int.TryParse(p[1], out var changed) && int.TryParse(p[2], out var total):
                filesChanged = changed;
                filesOf = total;
                filesCompiled = 0;
                ChangedFiles();
                break;
            case "@@file" when p.Length == 4 && (p[3] is "new" or "again"):
                filesCompiled++;
                ChangedFiles();
                break;
            case "@@eta" when p.Length == 2 && double.TryParse(p[1], System.Globalization.NumberStyles.Float,
                                                              System.Globalization.CultureInfo.InvariantCulture, out var eta):
                compileEta = eta;
                break;
            case "@@times" when p.Length == 4:
                builtIn = elapsed.Elapsed;
                long.TryParse(p[2], out thisCompile);
                long.TryParse(p[3], out previousTotal);
                break;
        }
    }

    void ChangedFiles()
    {
        if (filesChanged < 0 || !rows.TryGetValue("compile", out var row)) return;
        row.Small(filesChanged == 0 ? "Tudo já está compilado"
                : filesChanged >= filesOf ? $"{filesCompiled} de {filesChanged} arquivos"
                : $"{filesCompiled} de {filesChanged} arquivos que mudaram");
    }

    void AddLog(string line)
    {
        log.AppendLine(line);
        if (log.Length > 600_000) log.Remove(0, log.Length - 400_000);
        if (Log.Visibility == Visibility.Visible)
        {
            Log.AppendText(line + "\n");
            Log.ScrollToEnd();
        }
    }

    void SetProgress(double f)
    {
        f = Math.Clamp(f, 0, 1);
        RingText.Text = $"{(int)(f * 100)}%";
        UpdateBar.Value = f;
        const double r = 55, c = 60;
        if (f >= 0.999) { RingArc.Data = new EllipseGeometry(new Point(c, c), r, r); return; }
        var a = f * 2 * Math.PI;
        var end = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a));
        var fig = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, f > 0.5, SweepDirection.Clockwise, true));
        RingArc.Data = f < 0.002 ? null : new PathGeometry(new[] { fig });
    }

    void Tick()
    {
        foreach (var r in rows.Values) r.Tick();
        if (runner == null) return;
        var tail = updating ? ". O seu jogo atual fica como está até o novo ficar pronto." : " · o seu PC continua livre para usar";
        if (current == "compile")
        {
            // (the timer of the compile itself, and the countdown build.py works out from what this PC measured before)
            var left = compileEta >= 0 ? TimeLeft(compileEta)
                : compiled > 0.03 ? TimeLeft(compileClock.Elapsed.TotalSeconds / compiled * (1 - compiled) + 60)
                : "Calculando…";
            BuildNote.Text = $"Compilando {Clock(compileClock.Elapsed)} · {left}{tail}";
            return;
        }
        BuildNote.Text = (updating ? "Trabalhando…" : "Costuma levar uns 45 minutos") + tail;
    }

    static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    static string TimeLeft(double seconds)
    {
        if (seconds < 45) return "Falta menos de um minuto";
        if (seconds < 90) return "Falta mais ou menos um minuto";
        var minutes = (int)Math.Round(seconds / 60);
        return minutes >= 60 ? $"Faltam uns {minutes / 60} h {minutes % 60} min" : $"Faltam uns {minutes} minutos";
    }

    void Ended(string? failure)
    {
        clock.Stop();
        elapsed.Stop();
        runner = null;
        if (finished.Length > 0)
        {
            foreach (var (id, r) in rows)
            {
                if (id != ShaderStep.Id) r.Done();
                else if (ShaderStep.Skipped(shadersBegan, shadersLater) is { } why) { r.Skipped(); r.Small(why); }
                else r.Done();
            }
            SetProgress(1);
            ShowReady();
            return;
        }
        if (failure == "cancelled")
        {
            ToFiles();
            return;
        }
        if (current != null && rows.TryGetValue(current, out var row)) row.Stopped();
        ShowProblem(failure ?? "As ferramentas de geração pararam sem dizer por quê.", tools: failure?.Contains("antivirus", StringComparison.OrdinalIgnoreCase) == true);
    }

    void LogToggled(object sender, RoutedEventArgs e)
    {
        var show = ShowLog.IsChecked == true;
        Log.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        StepCard.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        ShowLog.Content = show ? "Esconder detalhes" : "Mostrar detalhes";
        if (show)
        {
            Log.Text = log.ToString();
            Log.ScrollToEnd();
        }
    }

    void CancelBuild(object sender, RoutedEventArgs e)
    {
        download?.Cancel();
        runner?.Cancel();
    }

    // ================= 3: play =================

    void ShowReady()
    {
        SetStep(2);
        ReadyTitle.Text = updating ? "O seu jogo foi atualizado" : "O seu jogo está pronto";
        var compiledNote = thisCompile > 0 ? $" (compilado em {Span(TimeSpan.FromSeconds(thisCompile))})" : "";
        ReadyNote.Text = $"{(updating ? "Atualizado" : "Gerado")} em {Span(elapsed.Elapsed)}{compiledNote}{Against(builtIn ?? elapsed.Elapsed)} · {finished}";
        if (!updating && Environment.GetEnvironmentVariable("HOOKSHOT_UI_PREVIEW") == null)
            Finder.Shortcuts(finished, DesktopTick.IsChecked == true, StartTick.IsChecked == true);
        else if (updating && Environment.GetEnvironmentVariable("HOOKSHOT_UI_PREVIEW") == null)
        {   // (the ones the player has, onto the folder's launcher; the boxes say which there are, and ticking one makes it)
            DesktopTick.IsChecked = File.Exists(Finder.DesktopLink);
            StartTick.IsChecked = File.Exists(Finder.StartLink);
            Finder.RefreshShortcuts(finished);
        }
        if (Finder.ShortcutError != null) AddLog("shortcut not made: " + Finder.ShortcutError);
        if (fromSource && !updating)
        {
            // (fork) a cópia descompactada já está no jogo (o romfs por link, o exefs em .hookshot_exefs): sai. O download fica, para
            // gerar de novo sem baixar; a nota diz onde está e quanto ocupa
            fromSource = false;
            try { if (Directory.Exists(UnpackFolder)) Directory.Delete(UnpackFolder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AddLog("cópia descompactada não apagada: " + ex.Message); }
            try
            {
                var bytes = new DirectoryInfo(DownloadFolder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                ReadyNote.Text += $"\nO download da fonte ({Gb(bytes)}) ficou em {DownloadFolder}. Se precisar do espaço, pode apagar.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        ShowPage(ReadyPage);
        _ = FreeAfterBuild();
    }

    // (issue: 5 GB and more left in %LOCALAPPDATA% and never said. Said here, with the way to let it go)
    async Task ShowStorageUse()
    {
        StorageRow.Visibility = Visibility.Collapsed;
        var (total, freeable) = await Task.Run(Storage.Measure);
        if (total < 100L << 20) return;
        // (both places when the build files are in a folder the player chose: the total counts both)
        var where = Storage.WorkChosen ? $"{Storage.Base} e {Storage.Work}" : Storage.Base;
        StorageNote.Text = freeable >= 100L << 20
            ? $"O Hookshot Builder guarda {Storage.Gb(total)} neste PC ({where}). {Storage.Gb(freeable)} disso só deixa a próxima " +
              "atualização mais rápida e pode ser liberado: o seu jogo continua funcionando, e a próxima atualização leva mais ou menos o tempo desta geração."
            : $"O Hookshot Builder guarda {Storage.Gb(total)} neste PC ({where}): o próprio Builder.";
        FreeButton.Visibility = freeable >= 100L << 20 ? Visibility.Visible : Visibility.Collapsed;
        FreeButton.IsEnabled = true;
        FreeButton.Content = "Liberar espaço";
        StorageRow.Visibility = Visibility.Visible;
    }

    // (players asked for the space back: the translated code, the setup's and updater's downloads and older builder versions go as soon
    // as the game is ready. The compiled objects stay (~1 GB): without them every update compiled all ~600 files again, an hour and
    // more (issue 1287). Free up space lets them go too. HOOKSHOT_KEEP_BUILD_FILES=1 keeps everything)
    async Task FreeAfterBuild()
    {
        if (!Storage.KeepBuildFiles)
        {
            var left = await Task.Run(() => Storage.Free(afterBuild: true));
            if (left >= 100L << 20) AddLog($"build files: {Storage.Gb(left)} could not be deleted (a file in use); they go after the next build");
        }
        await ShowStorageUse();
    }

    async void FreeSpace(object sender, RoutedEventArgs e)
    {
        FreeButton.IsEnabled = false;
        FreeButton.Content = "Liberando…";
        var left = await Task.Run(() => Storage.Free());
        await ShowStorageUse();
        if (left >= 100L << 20)
            StorageNote.Text += $" ({Storage.Gb(left)} não puderam ser apagados: um arquivo está em uso. Feche o jogo e o Builder e apague o conteúdo da pasta à mão.)";
    }

    void ShowStorage(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{(Storage.WorkChosen ? Storage.Work : Storage.Base)}\"") { UseShellExecute = true });   // (the bigger one)

    void ShortcutsChanged(object sender, RoutedEventArgs e)
    {
        if (finished.Length > 0) Finder.Shortcuts(finished, DesktopTick.IsChecked == true, StartTick.IsChecked == true);
    }

    static string Span(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} minutos" : $"{t.Seconds} segundos";

    /// This build beside the last one on this PC (build_times.json, through make_game.py's @@times): "last time 45 minutes
    /// (4 minutes faster)"
    string Against(TimeSpan now)
    {
        if (previousTotal <= 0) return "";
        var last = TimeSpan.FromSeconds(previousTotal);
        var diff = last.TotalSeconds - now.TotalSeconds;
        var how = Math.Abs(diff) < 30 ? "mais ou menos igual"
                : diff > 0 ? $"{Span(TimeSpan.FromSeconds(diff))} mais rápido" : $"{Span(TimeSpan.FromSeconds(-diff))} mais devagar";
        return $" · da última vez {Span(last)} ({how})";
    }

    void OpenFolder(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{finished}\"") { UseShellExecute = true });

    void Play(object sender, RoutedEventArgs e)
    {
        StartGame(finished);
        WindowState = WindowState.Minimized;
    }

    // ================= when something goes wrong =================

    bool problemIsTools;

    void ShowProblem(string why, bool tools)
    {
        lastFailure = why;
        problemIsTools = tools;
        ProblemSteps.Children.Clear();
        ProblemSub.Text = updating ? "O seu jogo atual não mudou e continua funcionando." : "Nada mudou na pasta do seu jogo.";
        if (tools)
        {
            ProblemTitle.Text = "As ferramentas de geração precisam de reparo";
            ProblemHead.Text = "O seu antivírus tirou parte das ferramentas de geração";
            ProblemText.Text = why + "\n\nAntivírus às vezes marcam por engano ferramentas de desenvolvimento sem assinatura.";
            var base_ = Storage.Base;
            foreach (var (n, s) in new[]
                     {
                         (1, "Abra Segurança do Windows → Histórico de proteção e restaure os arquivos removidos."),
                         (2, $"Ou adicione {base_} às exclusões dele."),
                         (3, "Depois clique em Reparar: o Hookshot baixa as ferramentas de novo das versões oficiais e confere cada uma."),
                     })
                ProblemSteps.Children.Add(new TextBlock { Text = $"{n}.  {s}", Style = (Style)FindResource("Note"), FontSize = 14, Margin = new Thickness(0, 6, 0, 0) });
        }
        else
        {
            ProblemTitle.Text = updating ? "A atualização parou" : "A geração parou";
            ProblemHead.Text = "O que deu errado";
            ProblemText.Text = why;
        }
        RepairButton.Visibility = tools ? Visibility.Visible : Visibility.Collapsed;
        ProblemDetails.Content = "Mostrar detalhes";
        if (!updating) SetStep(1);   // (the update keeps its versions in the head)
        ShowPage(ProblemPage);
    }

    void ProblemShowDetails(object sender, RoutedEventArgs e)
    {
        var lines = log.ToString().Split('\n');
        ProblemSteps.Children.Clear();
        ProblemSteps.Children.Add(new TextBox
        {
            Text = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 80))), IsReadOnly = true, FontFamily = (FontFamily)FindResource("Mono"), FontSize = 11,
            Foreground = (Brush)FindResource("InkSoft"), Background = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)), BorderThickness = new Thickness(0),
            Padding = new Thickness(8), TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
    }

    void CopyForReport(object sender, RoutedEventArgs e)
    {
        var lines = log.ToString().Split('\n');
        var text = $"Hookshot Builder {Tools.Find()?.Version ?? "(source)"} on Windows {Environment.OSVersion.Version}\n" +
                   $"{ProblemTitle.Text}: {lastFailure}\n\n" + string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 200)));
        try { Clipboard.SetText(text); CopyReport.Content = "Copiado"; } catch (Exception) { }
    }

    void Back(object sender, RoutedEventArgs e) => ToFiles();

    /// The setup (HookshotBuilder-Setup.exe beside this app) checks every part against its checksum and downloads the missing ones
    void Repair(object sender, RoutedEventArgs e)
    {
        var setup = System.IO.Path.Combine(AppContext.BaseDirectory, "HookshotBuilder-Setup.exe");
        if (File.Exists(setup))
        {
            Process.Start(new ProcessStartInfo(setup, "--repair") { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
            Application.Current.Shutdown();
            return;
        }
        Process.Start(new ProcessStartInfo("https://github.com/xdzleo/Hookshot/releases/latest") { UseShellExecute = true });
    }

    // ================= (fork) baixar da sua fonte: SourceDownload.cs =================

    bool fromSource, buildWhenChecked;

    /// Onde o download e a cópia descompactada ficam: ao lado da pasta do jogo, no mesmo disco, para o romfs entrar no jogo por
    /// link (make_game.py link_or_copy) em vez de ocupar o espaço duas vezes. HOOKSHOT_FONTE_DOWNLOAD: um download que já está no PC
    string SourceFolder
    {
        get
        {
            var game = System.IO.Path.GetFullPath(OutPath.Text);
            return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(game) ?? game, "Hookshot - download da fonte");
        }
    }

    string DownloadFolder => Environment.GetEnvironmentVariable("HOOKSHOT_FONTE_DOWNLOAD") is { Length: > 0 } d ? d : System.IO.Path.Combine(SourceFolder, "download");
    string UnpackFolder => System.IO.Path.Combine(SourceFolder, "jogo");

    static string Gb(long bytes) => $"{bytes / (double)(1L << 30):0.0} GB";

    async void DownloadFromSource(object sender, RoutedEventArgs e)
    {
        if (runner != null || download != null) return;
        if (Sources.Accept(SourceUrl.Text) is not { } url)
        {
            MessageBox.Show(this, "Cole o endereço da fonte, começando com https://", "Hookshot");
            return;
        }
        if (OutPath.Text.Length <= 2)
        {
            MessageBox.Show(this, "Escolha onde o jogo vai ficar.", "Hookshot");
            return;
        }
        if (Tools.Find() == null)
        {
            ShowProblem("As ferramentas de geração não estão na pasta do Hookshot.", tools: true);
            return;
        }
        SaveSettings();
        fromSource = true;
        updating = false;
        StageList.Children.Clear();
        rows.Clear();
        var steps = new[] { ("find", "Procurando o jogo na sua fonte"), ("get", "Baixando o jogo"), ("unpack", "Juntando e descompactando os arquivos") };
        for (var i = 0; i < steps.Length; i++)
        {
            var row = new StepRow(i + 1, steps[i].Item2, last: i == steps.Length - 1);
            rows[steps[i].Item1] = row;
            StageList.Children.Add(row.Root);
        }
        SetStep(1);
        BuildTitle.Text = "Baixando o seu jogo";
        BuildNote.Text = "Procurando na sua fonte…";
        UpdateBar.Visibility = Visibility.Collapsed;
        RingBox.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        log.Clear();
        Log.Clear();
        SetProgress(0);
        ShowPage(BuildPage);
        elapsed.Restart();
        clock.Start();
        var cts = download = new CancellationTokenSource();
        var ct = cts.Token;
        var at = "find";
        try
        {
            // 1. a fonte
            rows["find"].Begin();
            var (title, uri) = await Sources.Fetch(url, ct);
            Sources.Save(url);
            rows["find"].Done();
            rows["find"].Small(title);
            AddLog($"fonte: {url}\njogo: {title}\nendereço: {uri}");

            // 2. o download (até 70% do anel)
            at = "get";
            rows["get"].Begin();
            var folder = DownloadFolder;
            AddLog("download em " + folder);
            var started = Stopwatch.StartNew();
            void Got(DownloadStatus s) => Dispatcher.BeginInvoke(() =>
            {
                if (!s.Metadata) { rows["get"].Small($"Procurando os dados do torrent… {s.Peers} pares"); return; }
                var speed = s.BytesPerSecond > 0 ? $" · {s.BytesPerSecond / (double)(1 << 20):0.0} MB/s" : "";
                var peers = s.Peers > 0 || s.Seeds > 0 ? $" · {s.Peers} pares, {s.Seeds} fontes" : "";
                rows["get"].Small(s.Wanted > 0 ? $"{Gb(s.Done)} de {Gb(s.Wanted)}{speed}{peers}" : $"{Gb(s.Done)}{speed}");
                SetProgress(0.7 * s.Fraction);
                BuildNote.Text = s.BytesPerSecond >= 1024 && s.Wanted > s.Done
                    ? TimeLeft((s.Wanted - s.Done) / (double)s.BytesPerSecond) + " para o download"
                    : "Baixando… o seu PC continua livre para usar";
            });
            if (uri.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                await Task.Run(() => Torrent.Download(uri, folder, Got, ct), ct);
            else
                await Torrent.DownloadDirect(uri, folder, Got, ct);
            rows["get"].Done();
            rows["get"].Small($"Baixado em {Span(started.Elapsed)}");

            // 3. juntar e descompactar (de 70% a 100%)
            at = "unpack";
            rows["unpack"].Begin();
            var archives = GameArchives.In(folder);
            string game;
            if (archives.Count == 0)
            {
                // (sem nada compactado: o .nup, ou as pastas, como vieram)
                game = Directory.EnumerateFiles(folder, "*.nup", SearchOption.AllDirectories).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault()
                       ?? Directory.EnumerateDirectories(folder, "romfs", SearchOption.AllDirectories).Select(d => System.IO.Path.GetDirectoryName(d)!).FirstOrDefault()
                       ?? throw new SourceException("O download não trouxe arquivos compactados, nem o .nup, nem as pastas romfs e exefs.");
                rows["unpack"].Small("Nada para descompactar");
            }
            else
            {
                var plan = await Task.Run(() => GameArchives.Check(archives), ct);
                var names = string.Join(", ", archives.Select(a => System.IO.Path.GetFileName(a[0])));
                AddLog($"arquivos: {names}\ncópia: {Gb(plan.Bytes)} em {UnpackFolder}");
                var total = Math.Max(1, plan.Bytes);
                var unpackClock = Stopwatch.StartNew();
                var shown = Stopwatch.StartNew();
                game = await Task.Run(() => GameArchives.Unpack(plan, UnpackFolder, (done, which, entry) =>
                {
                    if (shown.ElapsedMilliseconds < 200 && done < total) return;
                    shown.Restart();
                    Dispatcher.BeginInvoke(() =>
                    {
                        var seconds = unpackClock.Elapsed.TotalSeconds;
                        var rate = seconds >= 1 ? done / seconds : 0;
                        var file = archives.Count > 1 ? $"{System.IO.Path.GetFileName(archives[which][0])} ({which + 1} de {archives.Count})" : System.IO.Path.GetFileName(archives[which][0]);
                        rows["unpack"].Small($"{Gb(done)} de {Gb(total)}{(rate > 0 ? $" · {rate / (1 << 20):0} MB/s" : "")} · {file}");
                        SetProgress(0.7 + 0.3 * Math.Min(1, done / (double)total));
                        BuildNote.Text = seconds >= 5 && rate > 0 ? TimeLeft((total - done) / rate) + " para juntar os arquivos" : entry;
                    });
                }, ct), ct);
                rows["unpack"].Small($"{names} → {UnpackFolder}");
            }
            rows["unpack"].Done();
            SetProgress(1);
            download = null;
            clock.Stop();

            // 4. a geração de sempre, com o que veio
            if (game.EndsWith(".nup", StringComparison.OrdinalIgnoreCase))
            {
                buildWhenChecked = true;
                ToFiles();
                AddDump(game, "Baixado da fonte");
                return;
            }
            var exefsFolder = System.IO.Path.Combine(game, "exefs");
            if (!Directory.Exists(exefsFolder) && Directory.Exists(System.IO.Path.Combine(game, ".hookshot_exefs")))
                Directory.Move(System.IO.Path.Combine(game, ".hookshot_exefs"), exefsFolder);
            ToFiles();
            UseFolders(System.IO.Path.Combine(game, "romfs"), exefsFolder);
            if (romfs != null && exefs != null) BuildNow();
        }
        catch (OperationCanceledException)
        {
            download = null;
            clock.Stop();
            fromSource = false;
            ToFiles();
        }
        catch (Exception ex)
        {
            download = null;
            clock.Stop();
            fromSource = false;
            if (rows.TryGetValue(at, out var row)) row.Stopped();
            AddLog(ex.ToString());
            ShowProblem(ex is SourceException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}", tools: false);
            ProblemTitle.Text = "O download parou";
            ProblemSub.Text = "O que já foi baixado fica guardado: tentando de novo, o download continua de onde parou.";
        }
    }

    // ================= the update (Updater.cs): the game started this app with --update <folder> =================

    CancellationTokenSource? download;

    public void BeginUpdate(string folder, string? tag)
    {
        updating = true;
        OutPath.Text = folder;
        finished = "";
        var to = tag ?? "a versão mais nova";
        var from = GameVersion(folder) ?? Tools.Find()?.Version;
        StepPill.Visibility = Visibility.Collapsed;
        VersionChip.Visibility = Visibility.Visible;
        VersionChipText.Text = from != null && from != to ? $"{from} → {to}" : to;
        prepareNow = BuilderSettings.PrepareNow(SettingsPath);
        // (the updates show no menu: the saved choice, as ApplySettings may not have run)
        MemoryBox.SelectedItem = BuildMemory.Saved(MemoryBox.ItemsSource.Cast<object>(), BuilderSettings.Load(SettingsPath)?.MemoryGb);
        MakeRows(update: true, shaders: prepareNow);
        BuildTitle.Text = "Atualizando o seu jogo";
        BuildNote.Text = "Baixando a atualização…";
        RingBox.Visibility = Visibility.Collapsed;
        UpdateBar.Visibility = Visibility.Visible;
        SetProgress(0);
        rows["download"].Begin();
        CancelButton.Visibility = Visibility.Visible;
        ShowPage(BuildPage);
    }

    public CancellationToken DownloadToken => (download = new CancellationTokenSource()).Token;

    public void DownloadProgress(double f)
    {
        rows.GetValueOrDefault("download")?.Small($"{(int)(f * 100)}%");
        SetProgress(Weight["download"].Size * f);
    }

    public void Note(string line)
    {
        AddLog(line);
        rows.GetValueOrDefault("download")?.Small(line);
    }

    public void DownloadFailed(string why)
    {
        download = null;
        rows.GetValueOrDefault("download")?.Stopped();
        ShowProblem(why, tools: false);
        ProblemTitle.Text = "Não deu para baixar a atualização";
    }

    /// The new tools are here: the folder built again with them (only what changed is compiled)
    internal void UpdateBuild(Tools tools, string folder)
    {
        download = null;
        if (rows.TryGetValue("download", out var d)) { d.Done(); d.Small("Ferramentas de geração conferidas"); }
        // (the saved tick also opts an existing game into DLSS. After a self-update ApplySettings has not run yet;
        // a folder already built with DLSS keeps it, its license accepted then: the settings' record, or for a license accepted
        // before the record, the SDK's LICENSE.txt)
        var saved = BuilderSettings.Load(SettingsPath);
        var dlss = saved?.Dlss == true ||
                   (File.Exists(System.IO.Path.Combine(folder, "ngx", "nvngx_dlss.dll")) &&
                    (saved?.DlssLicense == true || File.Exists(System.IO.Path.Combine(Storage.NgxSdk, "LICENSE.txt"))));
        // (AMD FSR 4, the same: the saved tick with AMD's license accepted, or a folder built with FSR 4 whose AMD runtime
        // was fetched, AMD's license saved beside it then, keeps it and its runtime)
        var fsr = (saved?.Fsr == true && saved.FsrLicense) ||
                  (File.Exists(System.IO.Path.Combine(folder, ".hookshot_fsr")) &&
                   File.Exists(System.IO.Path.Combine(folder, "amd_fidelityfx_upscaler_vk.dll")) &&
                   File.Exists(System.IO.Path.Combine(folder, "LICENSE-AMD-FSR-SDK.md")));
        // (prepareNow: the saved choice, read in BeginUpdate. The launcher's Update and the --update start after a self-update never
        // ran ApplySettings, so the tick there is only its default)
        Run(tools, ShaderStep.Args(new List<string> { "--update", folder }, prepareNow), keepRows: true, dlss: dlss, fsr: fsr);
    }

    // ================= the window =================

    void Minimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void CloseWindow(object sender, RoutedEventArgs e)
    {
        if (runner != null && MessageBox.Show(this, "Parar a geração e fechar?", "Hookshot", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        runner?.Cancel();
        Close();
    }
}

/// One step of the build: its mark (done, now, waiting), its name, a line under it and its time.
sealed class StepRow
{
    public readonly Grid Root = new() { Margin = new Thickness(0, 10, 0, 10) };
    readonly Grid mark = new() { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
    readonly Ellipse disc = new(), halo = new() { Width = 32, Height = 32, Margin = new Thickness(-5) };
    readonly TextBlock number = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Path tick = new() { Data = Geometry.Parse("M0,4 L3.5,7.5 L10,0"), StrokeThickness = 1.8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock name = new() { FontWeight = FontWeights.SemiBold };
    readonly TextBlock small = new() { FontSize = 12, Visibility = Visibility.Collapsed, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock time = new() { FontSize = 12, Text = "—", VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
    readonly Stopwatch watch = new();
    readonly int n;
    bool running;

    static Brush B(string key) => (Brush)Application.Current.FindResource(key);

    public StepRow(int n, string text, bool last)
    {
        this.n = n;
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        Root.ColumnDefinitions.Add(new ColumnDefinition());
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mark.Children.Add(halo);
        mark.Children.Add(disc);
        mark.Children.Add(number);
        mark.Children.Add(tick);
        tick.Stroke = B("Go");
        time.FontFamily = (FontFamily)Application.Current.FindResource("Mono");
        var texts = new StackPanel { Children = { name, small } };
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(time, 2);
        if (!last)
        {
            var link = new Rectangle { Width = 1, Fill = B("GlassLine"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(10.5, 28, 0, -19) };
            Root.Children.Add(link);
        }
        Root.Children.Add(mark);
        Root.Children.Add(texts);
        Root.Children.Add(time);
        name.Text = text;
        Wait();
    }

    public void Small(string text)
    {
        small.Text = text;
        small.Visibility = Visibility.Visible;
    }

    void Wait()
    {
        disc.Fill = Brushes.Transparent;
        disc.Stroke = B("GlassLineStrong");
        disc.StrokeThickness = 1.5;
        halo.Fill = Brushes.Transparent;
        number.Text = "";
        tick.Visibility = Visibility.Collapsed;
        name.Foreground = B("InkFaint");
        name.FontWeight = FontWeights.Medium;
        small.Foreground = B("InkFaint");
        time.Foreground = B("InkFaint");
    }

    public void Begin()
    {
        running = true;
        watch.Restart();
        disc.Fill = B("Brand");
        disc.Stroke = null;
        halo.Fill = B("BrandSoft");
        number.Text = n.ToString();
        number.Foreground = Brushes.White;
        tick.Visibility = Visibility.Collapsed;
        name.Foreground = B("Ink");
        name.FontWeight = FontWeights.SemiBold;
        time.Foreground = B("Ink");
        Tick();
    }

    public void Tick()
    {
        if (running) time.Text = $"{(int)watch.Elapsed.TotalMinutes}:{watch.Elapsed.Seconds:00}";
    }

    public void Done()
    {
        if (running) Tick();
        running = false;
        watch.Stop();
        disc.Fill = B("GoSoft");
        disc.Stroke = null;
        halo.Fill = Brushes.Transparent;
        number.Text = "";
        tick.Visibility = Visibility.Visible;
        name.Foreground = B("Ink");
        name.FontWeight = FontWeights.SemiBold;
        time.Foreground = B("InkFaint");
    }

    /// (not done: left to later, the shaders' step when they were not prepared now)
    public void Skipped()
    {
        if (running) Tick();
        running = false;
        watch.Stop();
        disc.Fill = Brushes.Transparent;
        disc.Stroke = B("GlassLineStrong");
        disc.StrokeThickness = 1.5;
        halo.Fill = Brushes.Transparent;
        number.Text = "–";
        number.Foreground = B("InkFaint");
        tick.Visibility = Visibility.Collapsed;
        name.Foreground = B("InkFaint");
        name.FontWeight = FontWeights.Medium;
        time.Foreground = B("InkFaint");
    }

    public void Stopped()
    {
        running = false;
        watch.Stop();
        disc.Fill = B("BadSoft");
        halo.Fill = Brushes.Transparent;
        number.Text = "!";
        number.Foreground = B("Bad");
    }
}
