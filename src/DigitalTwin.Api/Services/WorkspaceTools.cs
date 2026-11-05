using System.ComponentModel;
using System.Text.RegularExpressions;

namespace DigitalTwin.Api;

/// <summary>
/// Strict, read-only project access plus safe Markdown output. Tool-visible paths are
/// either inside data/workspace OR linked-ID paths resolved by ProjectCatalog.
/// </summary>
public sealed class WorkspaceTools(TwinSettings settings, ProjectCatalog catalog)
{
    private readonly string _root = Path.GetFullPath(settings.Workspace);
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    { ".git", "node_modules", "bin", "obj", ".next", "dist", ".vs", ".idea", "coverage", "vendor", ".venv" };
    private static readonly HashSet<string> Readable = new(StringComparer.OrdinalIgnoreCase)
    { ".cs", ".csproj", ".sln", ".slnx", ".md", ".txt", ".json", ".tsx", ".ts", ".js", ".jsx", ".css", ".html", ".xml", ".yaml", ".yml", ".py", ".java", ".sql", ".props", ".config", ".toml", ".go", ".rs", ".fs", ".gradle" };
    private string Resolve(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) relative = ".";
        relative = relative.Replace('\\', '/');
        string baseDir = _root;
        string subPath = relative;
        if (relative.StartsWith("linked-", StringComparison.OrdinalIgnoreCase))
        {
            var slash = relative.IndexOf('/');
            var id = slash < 0 ? relative : relative[..slash];
            if (!catalog.TryGet(id, out var record) || record is null) throw new DirectoryNotFoundException("Linked project not found.");
            baseDir = catalog.ValidatePath(record.Path, record.ApprovedOutsideRoots);
            subPath = slash < 0 ? "." : relative[(slash + 1)..];
        }
        if (Path.IsPathRooted(subPath) || subPath.Contains(':')) throw new UnauthorizedAccessException("Use a workspace-relative path or connected-project ID.");
        var result = Path.GetFullPath(Path.Combine(baseDir, subPath));
        if (!ProjectCatalog.IsUnder(result, baseDir)) throw new UnauthorizedAccessException("Path escapes the permitted project root.");
        ProjectCatalog.GuardLinks(result);
        return result;
    }
    [Description("Read the CONTENTS of one small approved source/configuration file only after locating it with ListProject. Use for concrete code-level questions; never execute it. Not for uploaded policy questions (use SearchKnowledge) or public current docs (use SearchWeb).")]
    public string ReadFile([Description("Required path returned by ListProject, e.g. projects/project-abc/src/Program.cs or linked-abc/README.md. Must remain within an approved root; maximum file size 250 KB.")] string relativePath)
    {
        using var toolTelemetry = DeveloperTwinTelemetry.BeginTool("ReadFile");
        try
        {
        var path = Resolve(relativePath);
        var name = Path.GetFileName(path);
        if (name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".user", StringComparison.OrdinalIgnoreCase) ||
            !Readable.Contains(Path.GetExtension(path)))
            return "This file is excluded from AI inspection for safety or unsupported format.";
        var file = new FileInfo(path);
        if (!file.Exists) return "File not found.";
        if (file.Length > 250_000) return "File exceeds 250 KB; please inspect a smaller file.";
        return File.ReadAllText(path);
        }
        catch { toolTelemetry.Fail(); throw; }
    }
    [Description("List filenames and directories in an imported ZIP or connected approved project, without reading or executing source code. Use first for project architecture questions, then selectively call ReadFile. Not for policy retrieval or public docs.")]
    public string ListProject([Description("Workspace-relative project folder like projects/project-abc or linked project ID like linked-abc. Default '.' lists the workspace.")] string relativePath = ".")
    {
        using var toolTelemetry = DeveloperTwinTelemetry.BeginTool("ListProject");
        try
        {
        var root = Resolve(relativePath);
        if (!Directory.Exists(root)) return "Directory not found.";
        var lines = new List<string>();
        void Visit(string folder, int depth)
        {
            if (depth > 6 || lines.Count >= 550) return;
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(folder).OrderBy(p => p).Take(160))
                {
                    var name = Path.GetFileName(entry);
                    if (Excluded.Contains(name) || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)) continue;
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue;
                    bool isDir = Directory.Exists(entry);
                    lines.Add(new string(' ', depth * 2) + name + (isDir ? "/" : ""));
                    if (isDir) Visit(entry, depth + 1);
                    if (lines.Count >= 550) break;
                }
            }
            catch (UnauthorizedAccessException) { lines.Add("[some files not accessible]"); }
        }
        Visit(root, 0);
        return lines.Count == 0 ? "[Empty folder]" : string.Join('\n', lines);
        }
        catch { toolTelemetry.Fail(); throw; }
    }
    public string ExportMarkdown(string filename, string text)
    {
        if (!Regex.IsMatch(filename, "^[a-zA-Z0-9_-]{1,80}\\.md$")) throw new ArgumentException("Use a simple .md filename.");
        var reports = Resolve("reports");
        Directory.CreateDirectory(reports);
        var dest = Resolve("reports/" + filename);
        File.WriteAllText(dest, text);
        return dest;
    }
    public string Root => _root;
    public string ResolveUploadName(string name) => Resolve("uploads/" + Path.GetFileName(name));
}
