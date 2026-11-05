using System.Text.Json;

namespace DigitalTwin.Api;

public sealed record ConnectedProject(
    string Id,
    string Name,
    string Path,
    DateTimeOffset ConnectedAt,
    bool ApprovedOutsideRoots = false);
public sealed record FolderAccessDraft(string Path);

/// <summary>
/// Explicitly approved, read-only links to existing folders. No project is copied or executed.
/// Folders under configured roots connect directly; other folders require explicit approval.
/// </summary>
public sealed class ProjectCatalog
{
    private readonly TwinSettings _settings;
    private readonly string _index;
    private readonly Dictionary<string, ConnectedProject> _projects;
    private readonly object _gate = new();

    public ProjectCatalog(TwinSettings settings)
    {
        _settings = settings;
        Directory.CreateDirectory(settings.Workspace);
        _index = Path.Combine(settings.Workspace, "connected-projects.json");
        try { _projects = File.Exists(_index)
            ? JsonSerializer.Deserialize<List<ConnectedProject>>(File.ReadAllText(_index))!
                .ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase)
            : new(); }
        catch (Exception ex) when (ex is JsonException or IOException) { _projects = new(); }
    }

    public IReadOnlyList<ConnectedProject> List()
    {
        lock (_gate) return _projects.Values.OrderBy(p => p.Name).ToArray();
    }

    public string ValidateCandidatePath(string absolutePath)
    {
        if (!Path.IsPathFullyQualified(absolutePath)) throw new ArgumentException("Enter a full path to a folder.");
        var full = Path.GetFullPath(absolutePath);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("The specified directory does not exist.");
        GuardLinks(full);
        return full;
    }

    public bool IsUnderAllowedRoot(string fullPath) =>
        _settings.AllowedProjectRoots.Any(root => IsUnder(fullPath, Path.GetFullPath(root)));

    public string ValidatePath(string absolutePath, bool explicitlyApprovedOutsideRoots = false)
    {
        var full = ValidateCandidatePath(absolutePath);
        if (!IsUnderAllowedRoot(full) && !explicitlyApprovedOutsideRoots)
            throw new UnauthorizedAccessException("Folder is outside Twin:AllowedProjectRoots.");
        return full;
    }

    public ConnectedProject Connect(string absolutePath, bool explicitlyApprovedOutsideRoots = false)
    {
        var full = ValidatePath(absolutePath, explicitlyApprovedOutsideRoots);
        var project = new ConnectedProject(
            "linked-" + Guid.NewGuid().ToString("N")[..10],
            Path.GetFileName(full),
            full,
            DateTimeOffset.UtcNow,
            explicitlyApprovedOutsideRoots && !IsUnderAllowedRoot(full));
        lock (_gate)
        {
            _projects[project.Id] = project;
            File.WriteAllText(_index, JsonSerializer.Serialize(_projects.Values, new JsonSerializerOptions { WriteIndented = true }));
        }
        return project;
    }

    public bool TryGet(string id, out ConnectedProject? project)
    {
        lock (_gate) return _projects.TryGetValue(id, out project);
    }

    public static bool IsUnder(string file, string root) =>
        file.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        file.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void GuardLinks(string fullPath)
    {
        var part = Path.GetPathRoot(fullPath)!;
        foreach (var segment in fullPath[part.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            part = Path.Combine(part, segment);
            if ((File.Exists(part) || Directory.Exists(part)) &&
                (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Symbolic links and reparse points are not allowed.");
        }
    }
}
