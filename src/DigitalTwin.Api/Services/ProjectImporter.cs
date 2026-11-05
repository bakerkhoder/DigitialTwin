using System.IO.Compression;

namespace DigitalTwin.Api;

/// <summary>Imports source as inert text. Never builds or runs imported files.</summary>
public sealed class ProjectImporter(WorkspaceTools workspace)
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    { ".cs", ".csproj", ".sln", ".slnx", ".ts", ".tsx", ".js", ".jsx", ".json", ".md", ".txt", ".xml", ".yml", ".yaml", ".sql", ".html", ".css", ".scss", ".py", ".java", ".config", ".gitignore", ".props", ".toml", ".go", ".rs", ".fs" };
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    { ".git", "node_modules", "bin", "obj", ".vs", ".next", "dist", "build", "vendor", ".venv" };

    public async Task<(string Project, int Files, string Tree)> Import(IFormFile zip, CancellationToken ct)
    {
        if (!Path.GetExtension(zip.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Please select a .zip archive. For large projects use Connect Folder.");
        if (zip.Length is <= 0 or > 50_000_000)
            throw new InvalidDataException("ZIP is too large (50 MB maximum). Use Connect Folder for larger projects.");
        var id = "projects/project-" + Guid.NewGuid().ToString("N")[..10];
        var destRoot = Path.Combine(workspace.Root, id);
        Directory.CreateDirectory(destRoot);
        try
        {
            await using var input = zip.OpenReadStream();
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            if (archive.Entries.Count > 4000) throw new InvalidDataException("More than 4,000 ZIP entries. Use Connect Folder.");
            var count = 0;
            long total = 0;
            foreach (var item in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (item.Name.Length == 0) continue;
                var normalized = item.FullName.Replace('\\', '/');
                var pieces = normalized.Split('/');
                if (normalized.StartsWith('/') || normalized.Contains(':') || pieces.Any(p => p is "." or ".."))
                    throw new InvalidDataException("ZIP contains an unsafe path.");
                if (pieces.Any(p => ExcludedDirs.Contains(p) || p.StartsWith(".env", StringComparison.OrdinalIgnoreCase))) continue;
                if (item.Name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                    item.Name.Contains("credential", StringComparison.OrdinalIgnoreCase)) continue;
                // Block symlinks and files unsuitable for text inspection.
                if (((item.ExternalAttributes >> 16) & 0xF000) == 0xA000) continue;
                if (item.Length > 600_000 || !Allowed.Contains(Path.GetExtension(item.Name))) continue;
                total += item.Length;
                if (total > 120_000_000) throw new InvalidDataException("Uncompressed source exceeds 120 MB. Use Connect Folder.");
                var output = Path.GetFullPath(Path.Combine(destRoot, normalized));
                if (!ProjectCatalog.IsUnder(output, destRoot) || output == destRoot)
                    throw new InvalidDataException("ZIP attempted to escape its extraction directory.");
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await using var target = File.Create(output);
                await using var source = item.Open();
                await source.CopyToAsync(target, ct);
                count++;
            }
            return (id, count, workspace.ListProject(id));
        }
        catch { Directory.Delete(destRoot, recursive: true); throw; }
    }
}
