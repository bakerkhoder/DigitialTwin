using System.Text.RegularExpressions;

namespace DigitalTwin.Api;

public sealed record SavedMemory(string Id, string Content, string Kind, long Created, long Updated);

/// <summary>Only explicit user actions write memories. Retrieval is local SQLite full-text search.</summary>
public sealed class MemoryStore(LocalStateDatabase database)
{
    public SavedMemory[] List() => Read(null, null);

    public SavedMemory[] Search(string query, int limit = 5)
        => Search(query, limit, null);

    public SavedMemory[] Relevant(string query) =>
        Read(null, 3, "preference").Concat(Search(query, 2, "fact")).ToArray();

    private SavedMemory[] Search(string query, int limit, string? kind)
    {
        var tokens = Regex.Matches(query, @"[\p{L}\p{N}_]+")
            .Select(match => match.Value).Where(token => token.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12).Select(token => "\"" + token[..Math.Min(token.Length, 50)] + "\"*").ToArray();
        if (!string.IsNullOrWhiteSpace(query) && tokens.Length == 0) return [];
        return Read(tokens.Length == 0 ? null : string.Join(" OR ", tokens), Math.Clamp(limit, 1, 5), kind);
    }

    private SavedMemory[] Read(string? match, int? limit, string? kind = null)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = match is null
            ? "SELECT id,content,kind,created,updated FROM memories WHERE ($kind IS NULL OR kind=$kind) ORDER BY updated DESC,id LIMIT $limit"
            : """
                SELECT m.id,m.content,m.kind,m.created,m.updated FROM memories_search
                JOIN memories m ON m.id=memories_search.id WHERE memories_search MATCH $match
                AND ($kind IS NULL OR m.kind=$kind)
                ORDER BY bm25(memories_search),m.updated DESC LIMIT $limit
                """;
        command.Parameters.AddWithValue("$limit", limit ?? -1);
        command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        if (match is not null) command.Parameters.AddWithValue("$match", match);
        using var reader = command.ExecuteReader();
        var items = new List<SavedMemory>();
        while (reader.Read()) items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4)));
        return items.ToArray();
    }

    public SavedMemory? Save(string? id, string content, string kind = "preference")
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var creating = id is null;
        id ??= Guid.NewGuid().ToString();
        command.CommandText = creating
            ? "INSERT INTO memories(id,content,kind,created,updated) VALUES($id,$content,$kind,$now,$now) RETURNING created"
            : "UPDATE memories SET content=$content,kind=$kind,updated=$now WHERE id=$id RETURNING created";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$content", content.Trim());
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$now", now);
        var created = command.ExecuteScalar();
        if (created is null) return null;
        // Drop old tool evidence atomically with edits, including across restarts.
        if (!creating)
        {
            command.CommandText = "UPDATE state_metadata SET context_generation=context_generation+1 WHERE id=1; UPDATE conversations SET agent_state=NULL";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return new(id, content.Trim(), kind, (long)created, now);
    }

    public bool Delete(string id)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM memories WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() == 0) return false;
        command.CommandText = "UPDATE state_metadata SET context_generation=context_generation+1 WHERE id=1; UPDATE conversations SET agent_state=NULL";
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }
}
