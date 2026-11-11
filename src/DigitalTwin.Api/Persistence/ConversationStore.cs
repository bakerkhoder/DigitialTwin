using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DigitalTwin.Api;

public sealed record ConversationMessage(string Role, string Text, string[]? Attachments = null, bool Error = false);
public sealed record ConversationSummary(string Id, string Title, long Created, long Updated);
public sealed record ConversationDetail(string Id, string Title, long Created, long Updated, ConversationMessage[] Messages);
public sealed record ConversationImport(string Id, string Title, long Created, ConversationMessage[] Messages);
internal sealed record StoredAgentContext(string? Json, long Generation, bool Private);

public sealed class ConversationDeletedException() : Exception("This conversation was deleted. Start a new conversation.");

/// <summary>Visible transcripts and opaque MAF snapshots are committed together.</summary>
public sealed class ConversationStore(LocalStateDatabase database)
{
    public long Generation
    {
        get
        {
            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT context_generation FROM state_metadata WHERE id=1";
            return (long)command.ExecuteScalar()!;
        }
    }

    public ConversationSummary[] List()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,title,created,updated FROM conversations ORDER BY updated DESC,id";
        using var reader = command.ExecuteReader();
        var items = new List<ConversationSummary>();
        while (reader.Read()) items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3)));
        return items.ToArray();
    }

    public ConversationDetail? Get(string id)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT title,created,updated FROM conversations WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        string title;
        long created, updated;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            title = reader.GetString(0); created = reader.GetInt64(1); updated = reader.GetInt64(2);
        }
        command.CommandText = "SELECT role,text,attachments,error FROM conversation_messages WHERE conversation_id=$id ORDER BY id";
        using var messages = command.ExecuteReader();
        var items = new List<ConversationMessage>();
        while (messages.Read()) items.Add(new(messages.GetString(0), messages.GetString(1),
            JsonSerializer.Deserialize<string[]>(messages.GetString(2)), messages.GetBoolean(3)));
        return new(id, title, created, updated, items.ToArray());
    }

    internal StoredAgentContext? LoadContext(string id, bool throwIfDeleted = true)
    {
        using var connection = database.Open();
        if (throwIfDeleted) ThrowIfDeleted(connection, id);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT agent_state,context_generation,private_context FROM conversations WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt64(1), reader.GetBoolean(2)) : null;
    }

    internal void SaveTurn(string id, string? state, long generation, bool isPrivate,
        ConversationMessage user, ConversationMessage assistant)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        ThrowIfDeleted(connection, id, transaction);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conversations(id,title,created,updated,private_context,agent_state,context_generation)
            VALUES($id,$title,$now,$now,$private,
                CASE WHEN $generation=(SELECT context_generation FROM state_metadata WHERE id=1) THEN $state END,$generation)
            ON CONFLICT(id) DO UPDATE SET updated=$now,private_context=MAX(private_context,$private),
                agent_state=excluded.agent_state,context_generation=$generation;
            """;
        command.Parameters.AddWithValue("$id", id);
        var title = string.IsNullOrWhiteSpace(user.Text) ? "Attachment conversation" : user.Text;
        command.Parameters.AddWithValue("$title", title[..Math.Min(title.Length, 80)]);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$private", isPrivate);
        command.Parameters.AddWithValue("$state", (object?)state ?? DBNull.Value);
        command.Parameters.AddWithValue("$generation", generation);
        command.ExecuteNonQuery();
        Append(connection, transaction, id, user);
        Append(connection, transaction, id, assistant);
        transaction.Commit();
    }

    public void Import(ConversationImport[] conversations)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var item in conversations)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO conversations(id,title,created,updated,context_generation)
                SELECT $id,$title,$created,$created,(SELECT context_generation FROM state_metadata WHERE id=1)
                WHERE NOT EXISTS(SELECT 1 FROM deleted_conversations WHERE id=$id);
                """;
            command.Parameters.AddWithValue("$id", Guid.Parse(item.Id).ToString());
            command.Parameters.AddWithValue("$title", item.Title);
            command.Parameters.AddWithValue("$created", item.Created);
            if (command.ExecuteNonQuery() == 0) continue;
            foreach (var message in item.Messages) Append(connection, transaction, Guid.Parse(item.Id).ToString(), message);
        }
        transaction.Commit();
    }

    private static void Append(SqliteConnection connection, SqliteTransaction transaction, string id, ConversationMessage message)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO conversation_messages(conversation_id,role,text,attachments,error) VALUES($id,$role,$text,$attachments,$error)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$role", message.Role);
        command.Parameters.AddWithValue("$text", message.Text);
        command.Parameters.AddWithValue("$attachments", JsonSerializer.Serialize(message.Attachments ?? []));
        command.Parameters.AddWithValue("$error", message.Error);
        command.ExecuteNonQuery();
    }

    public void Delete(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            BEGIN IMMEDIATE;
            INSERT OR IGNORE INTO deleted_conversations(id) VALUES($id);
            DELETE FROM conversations WHERE id=$id;
            COMMIT;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void DiscardContext(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE conversations SET agent_state=NULL WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void InvalidateAllContexts()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            BEGIN IMMEDIATE;
            UPDATE state_metadata SET context_generation=context_generation+1 WHERE id=1;
            UPDATE conversations SET agent_state=NULL;
            COMMIT;
            """;
        command.ExecuteNonQuery();
    }

    private static void ThrowIfDeleted(SqliteConnection connection, string id, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM deleted_conversations WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteScalar() is not null) throw new ConversationDeletedException();
    }
}
