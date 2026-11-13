using System.Diagnostics;
using System.IO;
using System.Text;

namespace HookshotBuilder;

/// One home for the setup, app and their children. Portable homes are explicitly marked, never inferred from cwd.
static class BuilderHome
{
    public const string Marker = ".hookshot-portable";
    public const string MarkerText = "Hookshot Builder portable home v1";
    const string RootVariable = "HOOKSHOT_SETUP_BASE";
    const string PortableVariable = "HOOKSHOT_BUILDER_PORTABLE";
    internal readonly record struct Home(string Root, bool Portable);

    static readonly Lazy<Home> Current = new(() => Resolve(
        Environment.GetEnvironmentVariable(RootVariable), AppContext.BaseDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HookshotBuilder"),
        Environment.GetEnvironmentVariable(PortableVariable) == "1"));

    public static string Base => Current.Value.Root;
    public static bool Portable => Current.Value.Portable;
    public static string DefaultGame => Portable
        ? Path.Combine(Directory.GetParent(Base)!.FullName, "Hookshot")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games", "Hookshot");

    internal static Home Resolve(string? requested, string appDirectory, string standard, bool portableRequested = false)
    {
        if (!string.IsNullOrEmpty(requested))
        {
            var root = Absolute(requested);
            var marked = IsMarked(root);
            if (portableRequested && !marked)
                throw new IOException("A pasta portátil do Builder está sem a marca: " + root);
            return new(root, marked);
        }
        if (portableRequested)
            throw new IOException("A pasta portátil do Builder não foi passada para este processo.");

        // Setup versions/<tag>/HookshotBuilder and ZIP fallback builders/<tag>/HookshotBuilder have the same depth.
        DirectoryInfo? directory = new(Path.GetFullPath(appDirectory));
        for (var depth = 0; depth <= 3 && directory != null; depth++, directory = directory.Parent)
            if (IsMarked(directory.FullName)) return new(Absolute(directory.FullName), true);
        return new(Absolute(standard), false);
    }

    static string Absolute(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new IOException("Use uma pasta com caminho completo (disco ou UNC) para o Builder: " + path);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        // The native setup's paths use MAX_PATH. Refuse an unrepresentable root instead of truncating it.
        if (full.Length >= 260)
            throw new IOException("O caminho da pasta do Builder é longo demais para o instalador do Windows: " + path);
        return full;
    }

    static bool IsMarked(string root)
    {
        try
        {
            using var file = File.OpenRead(Path.Combine(root, Marker));
            var bytes = new byte[MarkerText.Length + 3];
            var length = file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (length > MarkerText.Length + 2)
                throw new IOException("A marca da pasta portátil do Builder é longa demais: " + root);
            var text = new UTF8Encoding(false, true).GetString(bytes, 0, length);
            if (text.TrimEnd('\r', '\n') != MarkerText)
                throw new IOException("A marca da pasta portátil do Builder não é válida: " + root);
            if (Directory.GetParent(Path.GetFullPath(root)) == null)
                throw new IOException("A pasta portátil do Builder tem de ser uma pasta só dela, não a raiz de um disco.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public static void PassTo(ProcessStartInfo child)
    {
        child.Environment[RootVariable] = Base;
        if (Portable) child.Environment[PortableVariable] = "1";
        else child.Environment.Remove(PortableVariable);
    }

    /// Paths on this portable volume travel with its drive letter; external volumes keep their explicit absolute path.
    public static string? SavePath(string? path) => SavePath(Current.Value, path);

    internal static string? SavePath(Home home, string? path)
    {
        if (!home.Portable || string.IsNullOrEmpty(path)) return path;
        if (Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path))
            throw new IOException("Um caminho portátil tem de ser relativo ou completo: " + path);
        var full = Path.GetFullPath(path, home.Root);
        return string.Equals(Path.GetPathRoot(full), Path.GetPathRoot(home.Root), StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(home.Root, full) : full;
    }

    public static string? LoadPath(string? path) => LoadPath(Current.Value, path);

    internal static string? LoadPath(Home home, string? path)
    {
        if (!home.Portable || string.IsNullOrEmpty(path) || Path.IsPathFullyQualified(path)) return path;
        if (Path.IsPathRooted(path)) throw new IOException("Um caminho portátil tem de ser relativo ou completo: " + path);
        return Path.GetFullPath(path, home.Root);
    }
}
