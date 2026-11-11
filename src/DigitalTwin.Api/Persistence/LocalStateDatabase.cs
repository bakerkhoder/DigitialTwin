using Microsoft.Data.Sqlite;

namespace DigitalTwin.Api;

/// <summary>Local durable state. Short transactions never span a model or tool call.</summary>
public sealed class LocalStateDatabase
{
    private readonly string _connectionString;

    public LocalStateDatabase(string path)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, ForeignKeys = true, DefaultTimeout = 5, Pooling = false
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version > 2)
            throw new InvalidOperationException("The local state database was created by a newer application version.");
        command.CommandText = "PRAGMA journal_mode=WAL";
        command.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        if (version == 0)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS state_metadata (id INTEGER PRIMARY KEY CHECK(id=1), context_generation INTEGER NOT NULL);
                INSERT OR IGNORE INTO state_metadata VALUES(1, 0);
                CREATE TABLE IF NOT EXISTS conversations (
                    id TEXT PRIMARY KEY, title TEXT NOT NULL, created INTEGER NOT NULL,
                    updated INTEGER NOT NULL, private_context INTEGER NOT NULL DEFAULT 0,
                    agent_state TEXT, context_generation INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS conversation_messages (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                    role TEXT NOT NULL CHECK(role IN ('user','assistant')), text TEXT NOT NULL,
                    attachments TEXT NOT NULL DEFAULT '[]', error INTEGER NOT NULL DEFAULT 0);
                CREATE INDEX IF NOT EXISTS ix_messages_conversation ON conversation_messages(conversation_id, id);
                CREATE TABLE IF NOT EXISTS deleted_conversations (id TEXT PRIMARY KEY);
                CREATE TABLE IF NOT EXISTS memories (
                    id TEXT PRIMARY KEY, content TEXT NOT NULL, created INTEGER NOT NULL, updated INTEGER NOT NULL);
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
        }
        if (version < 2)
        {
            command.CommandText = """
                ALTER TABLE memories ADD COLUMN kind TEXT NOT NULL DEFAULT 'preference' CHECK(kind IN ('preference','fact'));
                CREATE VIRTUAL TABLE IF NOT EXISTS memories_search USING fts5(id UNINDEXED, content, tokenize='unicode61');
                CREATE TRIGGER IF NOT EXISTS memories_insert AFTER INSERT ON memories BEGIN
                    INSERT INTO memories_search(id,content) VALUES(new.id,new.content);
                END;
                CREATE TRIGGER IF NOT EXISTS memories_update AFTER UPDATE ON memories BEGIN
                    DELETE FROM memories_search WHERE id=old.id;
                    INSERT INTO memories_search(id,content) VALUES(new.id,new.content);
                END;
                CREATE TRIGGER IF NOT EXISTS memories_delete AFTER DELETE ON memories BEGIN
                    DELETE FROM memories_search WHERE id=old.id;
                END;
                DELETE FROM memories_search;
                INSERT INTO memories_search(id,content) SELECT id,content FROM memories;
                PRAGMA user_version=2;
                """;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    internal SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }
}
