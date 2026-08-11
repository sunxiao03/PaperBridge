using System.Globalization;
using Microsoft.Data.Sqlite;
using PaperBridge.Domain.Documents;

namespace PaperBridge.Infrastructure.Storage;

internal sealed class SqliteDocumentRepository
{
    private const int CurrentSchemaVersion = 7;
    private const char TagSeparator = '\u001F';
    private readonly string _connectionString;

    public SqliteDocumentRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA synchronous = NORMAL;", cancellationToken);

        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(
            await versionCommand.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);

        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"The database schema version {version} is newer than this application supports ({CurrentSchemaVersion}).");
        }

        if (version == 0)
        {
            await ApplyVersionOneAsync(connection, cancellationToken);
            version = 1;
        }

        if (version == 1)
        {
            await ApplyVersionTwoAsync(connection, cancellationToken);
            version = 2;
        }

        if (version == 2)
        {
            await ApplyVersionThreeAsync(connection, cancellationToken);
            version = 3;
        }

        if (version == 3)
        {
            await ApplyVersionFourAsync(connection, cancellationToken);
            version = 4;
        }

        if (version == 4)
        {
            await ApplyVersionFiveAsync(connection, cancellationToken);
            version = 5;
        }

        if (version == 5)
        {
            await ApplyVersionSixAsync(connection, cancellationToken);
            version = 6;
        }

        if (version == 6)
        {
            await ApplyVersionSevenAsync(connection, cancellationToken);
        }
    }

    public Task<IReadOnlyList<LibraryDocument>> GetAllAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT d.id, d.content_hash, d.managed_file_name, d.title, d.authors,
                   d.publication_year, d.journal, d.doi, d.imported_at_utc,
                   d.last_opened_at_utc, d.last_page_index, d.last_scroll_offset,
                   d.is_favorite, d.folder_id, f.name,
                   (SELECT GROUP_CONCAT(t.name, char(31))
                    FROM document_tags AS dt
                    INNER JOIN tags AS t ON t.id = dt.tag_id
                    WHERE dt.document_id = d.id)
            FROM documents AS d
            LEFT JOIN library_folders AS f ON f.id = d.folder_id
            ORDER BY COALESCE(d.last_opened_at_utc, d.imported_at_utc) DESC,
                     d.title COLLATE NOCASE;
            """;

        return QueryDocumentsAsync(sql, null, cancellationToken);
    }

    public Task<IReadOnlyList<LibraryDocument>> SearchAsync(
        string ftsQuery,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT d.id, d.content_hash, d.managed_file_name, d.title, d.authors,
                   d.publication_year, d.journal, d.doi, d.imported_at_utc,
                   d.last_opened_at_utc, d.last_page_index, d.last_scroll_offset,
                   d.is_favorite, d.folder_id, f.name,
                   (SELECT GROUP_CONCAT(t.name, char(31))
                    FROM document_tags AS dt
                    INNER JOIN tags AS t ON t.id = dt.tag_id
                    WHERE dt.document_id = d.id)
            FROM documents AS d
            LEFT JOIN library_folders AS f ON f.id = d.folder_id
            WHERE d.rowid IN (
                SELECT rowid FROM documents_fts WHERE documents_fts MATCH $query)
            ORDER BY d.title COLLATE NOCASE;
            """;

        return QueryDocumentsAsync(sql, ftsQuery, cancellationToken);
    }

    public async Task<LibraryDocument?> FindByContentHashAsync(
        string contentHash,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT d.id, d.content_hash, d.managed_file_name, d.title, d.authors,
                   d.publication_year, d.journal, d.doi, d.imported_at_utc,
                   d.last_opened_at_utc, d.last_page_index, d.last_scroll_offset,
                   d.is_favorite, d.folder_id, f.name,
                   (SELECT GROUP_CONCAT(t.name, char(31))
                    FROM document_tags AS dt
                    INNER JOIN tags AS t ON t.id = dt.tag_id
                    WHERE dt.document_id = d.id)
            FROM documents AS d
            LEFT JOIN library_folders AS f ON f.id = d.folder_id
            WHERE d.content_hash = $hash
            LIMIT 1;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$hash", contentHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDocument(reader) : null;
    }

    public async Task<LibraryDocument?> FindByIdAsync(
        DocumentId documentId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT d.id, d.content_hash, d.managed_file_name, d.title, d.authors,
                   d.publication_year, d.journal, d.doi, d.imported_at_utc,
                   d.last_opened_at_utc, d.last_page_index, d.last_scroll_offset,
                   d.is_favorite, d.folder_id, f.name,
                   (SELECT GROUP_CONCAT(t.name, char(31))
                    FROM document_tags AS dt
                    INNER JOIN tags AS t ON t.id = dt.tag_id
                    WHERE dt.document_id = d.id)
            FROM documents AS d
            LEFT JOIN library_folders AS f ON f.id = d.folder_id
            WHERE d.id = $id
            LIMIT 1;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", documentId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDocument(reader) : null;
    }

    public async Task<bool> InsertAsync(LibraryDocument document, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO documents (
                id, content_hash, managed_file_name, title, authors, publication_year,
                journal, doi, imported_at_utc, last_opened_at_utc, last_page_index,
                last_scroll_offset, is_favorite, folder_id)
            VALUES (
                $id, $hash, $file, $title, $authors, $year,
                $journal, $doi, $imported, $opened, $page, $offset, $favorite, $folder)
            ON CONFLICT(content_hash) DO NOTHING;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddDocumentParameters(command, document);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<LibraryFolder>> GetFoldersAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, name, created_at_utc
            FROM library_folders
            ORDER BY name COLLATE NOCASE;
            """;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var folders = new List<LibraryFolder>();
        while (await reader.ReadAsync(cancellationToken))
        {
            folders.Add(new LibraryFolder(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                ParseDatabaseDate(reader.GetString(2))));
        }

        return folders;
    }

    public async Task<LibraryFolder> CreateFolderAsync(string name, CancellationToken cancellationToken)
    {
        var folder = new LibraryFolder(Guid.NewGuid(), name, DateTimeOffset.UtcNow);
        const string sql = """
            INSERT INTO library_folders (id, name, created_at_utc)
            VALUES ($id, $name, $created);
            """;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", folder.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", folder.Name);
        command.Parameters.AddWithValue("$created", ToDatabaseDate(folder.CreatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return folder;
    }

    public async Task RenameFolderAsync(Guid folderId, string name, CancellationToken cancellationToken)
    {
        const string sql = "UPDATE library_folders SET name = $name WHERE id = $id;";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$id", folderId.ToString("D"));
        EnsureOneRowChanged(await command.ExecuteNonQueryAsync(cancellationToken), $"Folder '{folderId}' was not found.");
    }

    public async Task DeleteFolderAsync(Guid folderId, CancellationToken cancellationToken)
    {
        const string sql = "DELETE FROM library_folders WHERE id = $id;";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", folderId.ToString("D"));
        EnsureOneRowChanged(await command.ExecuteNonQueryAsync(cancellationToken), $"Folder '{folderId}' was not found.");
    }

    public async Task SetDocumentFolderAsync(
        DocumentId documentId,
        Guid? folderId,
        CancellationToken cancellationToken)
    {
        const string sql = "UPDATE documents SET folder_id = $folder WHERE id = $id;";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$folder", folderId is null ? DBNull.Value : folderId.Value.ToString("D"));
        command.Parameters.AddWithValue("$id", documentId.ToString());
        EnsureOneRowChanged(
            await command.ExecuteNonQueryAsync(cancellationToken),
            $"Document '{documentId}' was not found.");
    }

    public async Task SetDocumentTagsAsync(
        DocumentId documentId,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureDocumentExistsAsync(connection, transaction, documentId, cancellationToken);

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM document_tags WHERE document_id = $document;";
            deleteCommand.Parameters.AddWithValue("$document", documentId.ToString());
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var tag in tags)
        {
            await using var tagCommand = connection.CreateCommand();
            tagCommand.Transaction = transaction;
            tagCommand.CommandText = """
                INSERT INTO tags (name) VALUES ($name) ON CONFLICT(name) DO NOTHING;
                INSERT INTO document_tags (document_id, tag_id)
                SELECT $document, id FROM tags WHERE name = $name COLLATE NOCASE;
                """;
            tagCommand.Parameters.AddWithValue("$name", tag);
            tagCommand.Parameters.AddWithValue("$document", documentId.ToString());
            await tagCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cleanupCommand = connection.CreateCommand())
        {
            cleanupCommand.Transaction = transaction;
            cleanupCommand.CommandText = "DELETE FROM tags WHERE id NOT IN (SELECT tag_id FROM document_tags);";
            await cleanupCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetFavoriteAsync(
        DocumentId documentId,
        bool isFavorite,
        CancellationToken cancellationToken)
    {
        const string sql = "UPDATE documents SET is_favorite = $favorite WHERE id = $id;";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$favorite", isFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$id", documentId.ToString());
        EnsureOneRowChanged(
            await command.ExecuteNonQueryAsync(cancellationToken),
            $"Document '{documentId}' was not found.");
    }

    public async Task RemoveDocumentAsync(DocumentId documentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM documents WHERE id = $id;";
            deleteCommand.Parameters.AddWithValue("$id", documentId.ToString());
            EnsureOneRowChanged(
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken),
                $"Document '{documentId}' was not found.");
        }

        await using (var cleanupCommand = connection.CreateCommand())
        {
            cleanupCommand.Transaction = transaction;
            cleanupCommand.CommandText = "DELETE FROM tags WHERE id NOT IN (SELECT tag_id FROM document_tags);";
            await cleanupCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateReadingPositionAsync(
        DocumentId documentId,
        int pageIndex,
        double scrollOffset,
        DateTimeOffset openedAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE documents
            SET last_opened_at_utc = $opened,
                last_page_index = $page,
                last_scroll_offset = $offset
            WHERE id = $id;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$opened", ToDatabaseDate(openedAtUtc));
        command.Parameters.AddWithValue("$page", pageIndex);
        command.Parameters.AddWithValue("$offset", scrollOffset);
        command.Parameters.AddWithValue("$id", documentId.ToString());
        EnsureOneRowChanged(
            await command.ExecuteNonQueryAsync(cancellationToken),
            $"Document '{documentId}' was not found.");
    }

    private static async Task ApplyVersionOneAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string migration = """
            BEGIN IMMEDIATE;

            CREATE TABLE documents (
                id TEXT NOT NULL PRIMARY KEY,
                content_hash TEXT NOT NULL UNIQUE CHECK(length(content_hash) = 64),
                managed_file_name TEXT NOT NULL UNIQUE,
                title TEXT NOT NULL,
                authors TEXT NULL,
                publication_year INTEGER NULL,
                journal TEXT NULL,
                doi TEXT NULL,
                imported_at_utc TEXT NOT NULL,
                last_opened_at_utc TEXT NULL,
                last_page_index INTEGER NOT NULL DEFAULT 0 CHECK(last_page_index >= 0),
                last_scroll_offset REAL NOT NULL DEFAULT 0 CHECK(last_scroll_offset >= 0),
                is_favorite INTEGER NOT NULL DEFAULT 0 CHECK(is_favorite IN (0, 1))
            );

            CREATE INDEX ix_documents_recent
                ON documents(last_opened_at_utc DESC, imported_at_utc DESC);
            CREATE INDEX ix_documents_doi ON documents(doi) WHERE doi IS NOT NULL;

            CREATE VIRTUAL TABLE documents_fts USING fts5(
                title,
                authors,
                journal,
                doi,
                content = 'documents',
                content_rowid = 'rowid',
                tokenize = 'unicode61 remove_diacritics 2'
            );

            CREATE TRIGGER documents_ai AFTER INSERT ON documents BEGIN
                INSERT INTO documents_fts(rowid, title, authors, journal, doi)
                VALUES (new.rowid, new.title, new.authors, new.journal, new.doi);
            END;

            CREATE TRIGGER documents_ad AFTER DELETE ON documents BEGIN
                INSERT INTO documents_fts(documents_fts, rowid, title, authors, journal, doi)
                VALUES ('delete', old.rowid, old.title, old.authors, old.journal, old.doi);
            END;

            CREATE TRIGGER documents_au AFTER UPDATE ON documents BEGIN
                INSERT INTO documents_fts(documents_fts, rowid, title, authors, journal, doi)
                VALUES ('delete', old.rowid, old.title, old.authors, old.journal, old.doi);
                INSERT INTO documents_fts(rowid, title, authors, journal, doi)
                VALUES (new.rowid, new.title, new.authors, new.journal, new.doi);
            END;

            PRAGMA user_version = 1;
            COMMIT;
            """;

        await ExecuteNonQueryAsync(connection, migration, cancellationToken);
    }

    private static async Task ApplyVersionTwoAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string migration = """
            BEGIN IMMEDIATE;

            CREATE TABLE library_folders (
                id TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                created_at_utc TEXT NOT NULL
            );

            ALTER TABLE documents ADD COLUMN folder_id TEXT NULL
                REFERENCES library_folders(id) ON DELETE SET NULL;
            CREATE INDEX ix_documents_folder ON documents(folder_id);

            CREATE TABLE tags (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE
            );

            CREATE TABLE document_tags (
                document_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
                tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
                PRIMARY KEY (document_id, tag_id)
            );
            CREATE INDEX ix_document_tags_tag ON document_tags(tag_id);

            PRAGMA user_version = 2;
            COMMIT;
            """;

        await ExecuteNonQueryAsync(connection, migration, cancellationToken);
    }

    private static async Task ApplyVersionThreeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string migration = """
            BEGIN IMMEDIATE;

            CREATE TABLE translation_cache (
                cache_key TEXT NOT NULL PRIMARY KEY CHECK(length(cache_key) = 64),
                document_hash TEXT NOT NULL,
                source_text_hash TEXT NOT NULL CHECK(length(source_text_hash) = 64),
                provider TEXT NOT NULL,
                model TEXT NOT NULL,
                granularity TEXT NOT NULL,
                source_language TEXT NOT NULL,
                target_language TEXT NOT NULL,
                prompt_version TEXT NOT NULL,
                custom_instruction_version TEXT NOT NULL,
                glossary_version TEXT NOT NULL,
                translation TEXT NOT NULL,
                input_tokens INTEGER NULL,
                output_tokens INTEGER NULL,
                created_at_utc TEXT NOT NULL
            );
            CREATE INDEX ix_translation_cache_document ON translation_cache(document_hash);

            PRAGMA user_version = 3;
            COMMIT;
            """;

        await ExecuteNonQueryAsync(connection, migration, cancellationToken);
    }

    private static async Task ApplyVersionFourAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string migration = """
            BEGIN IMMEDIATE;

            CREATE TABLE glossaries (
                id TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                source INTEGER NOT NULL CHECK(source IN (100, 200)),
                is_enabled INTEGER NOT NULL DEFAULT 1 CHECK(is_enabled IN (0, 1)),
                priority INTEGER NOT NULL DEFAULT 0,
                topic TEXT NULL,
                description TEXT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE TABLE glossary_terms (
                id TEXT NOT NULL PRIMARY KEY,
                glossary_id TEXT NOT NULL REFERENCES glossaries(id) ON DELETE CASCADE,
                english TEXT NOT NULL,
                english_normalized TEXT NOT NULL,
                preferred_chinese TEXT NOT NULL,
                english_aliases_json TEXT NOT NULL DEFAULT '[]',
                chinese_aliases_json TEXT NOT NULL DEFAULT '[]',
                category TEXT NULL,
                explanation TEXT NULL,
                notes TEXT NULL,
                source_reference TEXT NULL,
                priority INTEGER NOT NULL DEFAULT 0,
                review_status INTEGER NOT NULL DEFAULT 0 CHECK(review_status IN (0, 1, 2)),
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                UNIQUE(glossary_id, english_normalized)
            );
            CREATE INDEX ix_glossary_terms_glossary ON glossary_terms(glossary_id);
            CREATE INDEX ix_glossary_terms_english ON glossary_terms(english_normalized);
            CREATE INDEX ix_glossary_terms_review ON glossary_terms(review_status);

            PRAGMA user_version = 4;
            COMMIT;
            """;

        await ExecuteNonQueryAsync(connection, migration, cancellationToken);
    }

    private static async Task ApplyVersionFiveAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string migration = """
            BEGIN IMMEDIATE;

            CREATE TABLE bilingual_segments (
                document_hash TEXT NOT NULL CHECK(length(document_hash) = 64),
                page_index INTEGER NOT NULL CHECK(page_index >= 0),
                segment_id TEXT NOT NULL,
                source_start INTEGER NOT NULL CHECK(source_start >= 0),
                source_length INTEGER NOT NULL CHECK(source_length > 0),
                source_text TEXT NOT NULL,
                source_text_hash TEXT NOT NULL CHECK(length(source_text_hash) = 64),
                machine_translation TEXT NOT NULL,
                user_translation TEXT NULL,
                provider TEXT NOT NULL,
                model TEXT NOT NULL,
                prompt_version TEXT NOT NULL,
                glossary_version TEXT NOT NULL,
                layout_mode INTEGER NOT NULL CHECK(layout_mode IN (0, 1)),
                layout_confidence REAL NOT NULL CHECK(layout_confidence >= 0 AND layout_confidence <= 1),
                degradation_reason TEXT NULL,
                machine_updated_at_utc TEXT NOT NULL,
                user_updated_at_utc TEXT NULL,
                PRIMARY KEY (document_hash, page_index, segment_id)
            );
            CREATE INDEX ix_bilingual_segments_page
                ON bilingual_segments(document_hash, page_index, source_start);
            CREATE INDEX ix_translation_cache_created
                ON translation_cache(created_at_utc DESC);

            PRAGMA user_version = 5;
            COMMIT;
            """;

        await ExecuteNonQueryAsync(connection, migration, cancellationToken);
    }

    private static async Task ApplyVersionSixAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string migration = """
            BEGIN IMMEDIATE;

            CREATE TABLE document_annotations (
                id TEXT NOT NULL PRIMARY KEY,
                document_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
                document_hash TEXT NOT NULL CHECK(length(document_hash) = 64),
                page_index INTEGER NOT NULL CHECK(page_index >= 0),
                kind INTEGER NOT NULL CHECK(kind IN (0, 1, 2, 3)),
                selected_text TEXT NULL,
                source_start INTEGER NOT NULL DEFAULT 0 CHECK(source_start >= 0),
                source_length INTEGER NOT NULL DEFAULT 0 CHECK(source_length >= 0),
                text_fingerprint TEXT NULL,
                prefix_context TEXT NULL,
                suffix_context TEXT NULL,
                rectangles_json TEXT NOT NULL DEFAULT '[]',
                note_text TEXT NULL,
                linked_translation TEXT NULL,
                color TEXT NOT NULL CHECK(length(color) = 7),
                anchor_status INTEGER NOT NULL CHECK(anchor_status IN (0, 1, 2, 3)),
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            CREATE INDEX ix_document_annotations_document_page
                ON document_annotations(document_id, page_index, created_at_utc);
            CREATE UNIQUE INDEX ux_document_annotations_bookmark
                ON document_annotations(document_id, page_index)
                WHERE kind = 3;

            PRAGMA user_version = 6;
            COMMIT;
            """;

        await ExecuteNonQueryAsync(connection, migration, cancellationToken);
    }

    private static async Task ApplyVersionSevenAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string migration = """
            BEGIN IMMEDIATE;

            CREATE TABLE reading_assistant_cache (
                cache_key TEXT NOT NULL PRIMARY KEY,
                document_hash TEXT NOT NULL CHECK(length(document_hash) = 64),
                task_kind INTEGER NOT NULL CHECK(task_kind BETWEEN 0 AND 6),
                input_hash TEXT NOT NULL CHECK(length(input_hash) = 64),
                provider TEXT NOT NULL,
                model TEXT NOT NULL,
                prompt_version TEXT NOT NULL,
                custom_instruction_version TEXT NOT NULL,
                content TEXT NOT NULL,
                input_tokens INTEGER NULL,
                output_tokens INTEGER NULL,
                created_at_utc TEXT NOT NULL
            );
            CREATE INDEX ix_reading_assistant_cache_document_task
                ON reading_assistant_cache(document_hash, task_kind, created_at_utc DESC);
            CREATE INDEX ix_reading_assistant_cache_created
                ON reading_assistant_cache(created_at_utc DESC);

            PRAGMA user_version = 7;
            COMMIT;
            """;

        await ExecuteNonQueryAsync(connection, migration, cancellationToken);
    }

    private async Task<IReadOnlyList<LibraryDocument>> QueryDocumentsAsync(
        string sql,
        string? ftsQuery,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (ftsQuery is not null)
        {
            command.Parameters.AddWithValue("$query", ftsQuery);
        }

        var documents = new List<LibraryDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            documents.Add(ReadDocument(reader));
        }

        return documents;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ExecuteNonQueryAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureDocumentExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DocumentId documentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM documents WHERE id = $id;";
        command.Parameters.AddWithValue("$id", documentId.ToString());
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (count != 1)
        {
            throw new KeyNotFoundException($"Document '{documentId}' was not found.");
        }
    }

    private static void AddDocumentParameters(SqliteCommand command, LibraryDocument document)
    {
        command.Parameters.AddWithValue("$id", document.Id.ToString());
        command.Parameters.AddWithValue("$hash", document.ContentHash);
        command.Parameters.AddWithValue("$file", document.ManagedFileName);
        command.Parameters.AddWithValue("$title", document.Title);
        command.Parameters.AddWithValue("$authors", (object?)document.Authors ?? DBNull.Value);
        command.Parameters.AddWithValue("$year", (object?)document.PublicationYear ?? DBNull.Value);
        command.Parameters.AddWithValue("$journal", (object?)document.Journal ?? DBNull.Value);
        command.Parameters.AddWithValue("$doi", (object?)document.Doi ?? DBNull.Value);
        command.Parameters.AddWithValue("$imported", ToDatabaseDate(document.ImportedAtUtc));
        command.Parameters.AddWithValue(
            "$opened",
            document.LastOpenedAtUtc is null ? DBNull.Value : ToDatabaseDate(document.LastOpenedAtUtc.Value));
        command.Parameters.AddWithValue("$page", document.LastPageIndex);
        command.Parameters.AddWithValue("$offset", document.LastScrollOffset);
        command.Parameters.AddWithValue("$favorite", document.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue(
            "$folder",
            document.FolderId is null ? DBNull.Value : document.FolderId.Value.ToString("D"));
    }

    private static LibraryDocument ReadDocument(SqliteDataReader reader)
    {
        var tags = reader.IsDBNull(15)
            ? Array.Empty<string>()
            : reader.GetString(15).Split(TagSeparator, StringSplitOptions.RemoveEmptyEntries);
        Array.Sort(tags, StringComparer.OrdinalIgnoreCase);

        return new LibraryDocument(
            new DocumentId(Guid.Parse(reader.GetString(0))),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            ParseDatabaseDate(reader.GetString(8)),
            reader.IsDBNull(9) ? null : ParseDatabaseDate(reader.GetString(9)),
            reader.GetInt32(10),
            reader.GetDouble(11),
            reader.GetInt32(12) != 0,
            reader.IsDBNull(13) ? null : Guid.Parse(reader.GetString(13)),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            tags);
    }

    private static void EnsureOneRowChanged(int changedRows, string message)
    {
        if (changedRows != 1)
        {
            throw new KeyNotFoundException(message);
        }
    }

    private static string ToDatabaseDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDatabaseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
