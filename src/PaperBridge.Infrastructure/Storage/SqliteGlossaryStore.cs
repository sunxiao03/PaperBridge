using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PaperBridge.Application.Abstractions;
using PaperBridge.Domain.Glossaries;

namespace PaperBridge.Infrastructure.Storage;

public sealed class SqliteGlossaryStore : IGlossaryStore
{
    public static readonly Guid PersonalGlossaryId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly Guid ReactorPhysicsGlossaryId = Guid.Parse("00000000-0000-0000-0000-000000001001");
    private readonly AppDataPaths _paths;
    private readonly string _connectionString;

    public SqliteGlossaryStore(AppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectoriesExist();
        await new SqliteDocumentRepository(_paths.DatabasePath).InitializeAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await UpsertGlossaryAsync(new GlossaryDefinition(
            PersonalGlossaryId,
            "个人术语库",
            GlossarySource.User,
            priority: 1000,
            topic: "个人覆盖",
            description: "用户确认的首选译名；始终覆盖内置术语。",
            updatedAtUtc: now), cancellationToken);
        await UpsertGlossaryAsync(new GlossaryDefinition(
            ReactorPhysicsGlossaryId,
            "核反应堆物理（待审核）",
            GlossarySource.BuiltIn,
            priority: 100,
            topic: "核反应堆物理",
            description: "随程序提供的小批量起始词条；审核通过后才参与翻译。",
            updatedAtUtc: now), cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM glossary_terms WHERE glossary_id = $id;";
        countCommand.Parameters.AddWithValue("$id", ReactorPhysicsGlossaryId.ToString("D"));
        var count = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (count == 0)
        {
            foreach (var seed in BuiltInSeeds)
            {
                await SaveTermAsync(new GlossaryTerm(
                    seed.English,
                    seed.Chinese,
                    GlossarySource.BuiltIn,
                    category: seed.Category,
                    explanation: seed.Explanation,
                    sourceReference: seed.SourceReference,
                    id: CreateStableSeedId(seed.English),
                    glossaryId: ReactorPhysicsGlossaryId,
                    englishAliases: seed.Aliases,
                    reviewStatus: GlossaryReviewStatus.Pending,
                    updatedAtUtc: now), cancellationToken);
            }
        }
    }

    public async Task<GlossarySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var glossaries = new List<GlossaryDefinition>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, name, source, is_enabled, priority, topic, description, updated_at_utc
                FROM glossaries
                ORDER BY source DESC, priority DESC, name COLLATE NOCASE;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                glossaries.Add(new GlossaryDefinition(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    (GlossarySource)reader.GetInt32(2),
                    reader.GetInt32(3) != 0,
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
            }
        }

        var sources = glossaries.ToDictionary(glossary => glossary.Id, glossary => glossary.Source);
        var terms = new List<GlossaryTerm>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, glossary_id, english, preferred_chinese, english_aliases_json,
                       chinese_aliases_json, category, explanation, notes, source_reference,
                       priority, review_status, updated_at_utc
                FROM glossary_terms
                ORDER BY english_normalized;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var glossaryId = Guid.Parse(reader.GetString(1));
                terms.Add(new GlossaryTerm(
                    reader.GetString(2),
                    reader.GetString(3),
                    sources[glossaryId],
                    reader.GetInt32(10),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    Guid.Parse(reader.GetString(0)),
                    glossaryId,
                    DeserializeAliases(reader.GetString(4)),
                    DeserializeAliases(reader.GetString(5)),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    (GlossaryReviewStatus)reader.GetInt32(11),
                    DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
            }
        }

        return new GlossarySnapshot(glossaries, terms);
    }

    public async Task<GlossaryDefinition> CreatePersonalGlossaryAsync(
        string name,
        string? topic = null,
        CancellationToken cancellationToken = default)
    {
        var glossary = new GlossaryDefinition(Guid.NewGuid(), name, GlossarySource.User, topic: topic);
        await UpsertGlossaryAsync(glossary, cancellationToken);
        return glossary;
    }

    public async Task SetGlossaryEnabledAsync(
        Guid glossaryId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE glossaries SET is_enabled = $enabled, updated_at_utc = $updated WHERE id = $id;";
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", glossaryId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("术语库不存在。");
        }
    }

    public async Task SaveTermAsync(GlossaryTerm term, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(term);
        if (term.GlossaryId == Guid.Empty)
        {
            throw new ArgumentException("Term must belong to a glossary.", nameof(term));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO glossary_terms (
                id, glossary_id, english, english_normalized, preferred_chinese,
                english_aliases_json, chinese_aliases_json, category, explanation, notes,
                source_reference, priority, review_status, created_at_utc, updated_at_utc)
            VALUES (
                $id, $glossary, $english, $normalized, $chinese,
                $englishAliases, $chineseAliases, $category, $explanation, $notes,
                $sourceReference, $priority, $reviewStatus, $created, $updated)
            ON CONFLICT(glossary_id, english_normalized) DO UPDATE SET
                preferred_chinese = excluded.preferred_chinese,
                english_aliases_json = excluded.english_aliases_json,
                chinese_aliases_json = excluded.chinese_aliases_json,
                category = excluded.category,
                explanation = excluded.explanation,
                notes = excluded.notes,
                source_reference = excluded.source_reference,
                priority = excluded.priority,
                review_status = excluded.review_status,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$id", term.Id.ToString("D"));
        command.Parameters.AddWithValue("$glossary", term.GlossaryId.ToString("D"));
        command.Parameters.AddWithValue("$english", term.English);
        command.Parameters.AddWithValue("$normalized", GlossaryTerm.NormalizeEnglish(term.English));
        command.Parameters.AddWithValue("$chinese", term.PreferredChinese);
        command.Parameters.AddWithValue("$englishAliases", JsonSerializer.Serialize(term.EnglishAliases));
        command.Parameters.AddWithValue("$chineseAliases", JsonSerializer.Serialize(term.ChineseAliases));
        command.Parameters.AddWithValue("$category", (object?)term.Category ?? DBNull.Value);
        command.Parameters.AddWithValue("$explanation", (object?)term.Explanation ?? DBNull.Value);
        command.Parameters.AddWithValue("$notes", (object?)term.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceReference", (object?)term.SourceReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$priority", term.Priority);
        command.Parameters.AddWithValue("$reviewStatus", (int)term.ReviewStatus);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$updated", term.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteTermAsync(Guid termId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM glossary_terms WHERE id = $id;";
        command.Parameters.AddWithValue("$id", termId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpsertGlossaryAsync(
        GlossaryDefinition glossary,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO glossaries (
                id, name, source, is_enabled, priority, topic, description, created_at_utc, updated_at_utc)
            VALUES ($id, $name, $source, $enabled, $priority, $topic, $description, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                priority = excluded.priority,
                topic = excluded.topic,
                description = excluded.description;
            """;
        command.Parameters.AddWithValue("$id", glossary.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", glossary.Name);
        command.Parameters.AddWithValue("$source", (int)glossary.Source);
        command.Parameters.AddWithValue("$enabled", glossary.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$priority", glossary.Priority);
        command.Parameters.AddWithValue("$topic", (object?)glossary.Topic ?? DBNull.Value);
        command.Parameters.AddWithValue("$description", (object?)glossary.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$updated", glossary.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static IReadOnlyList<string> DeserializeAliases(string json) =>
        JsonSerializer.Deserialize<string[]>(json) ?? [];

    private static Guid CreateStableSeedId(string english)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(english));
        return new Guid(bytes);
    }

    private static readonly BuiltInSeed[] BuiltInSeeds =
    [
        new("neutron flux", "中子通量", "中子学", "单位时间穿过单位面积的中子轨迹长度表征。", "IAEA Safety Glossary", ["flux"]),
        new("neutron fluence", "中子注量", "中子学", "中子通量对时间的积分。", "IAEA Safety Glossary"),
        new("prompt neutron", "瞬发中子", "中子学", "裂变后极短时间内发射的中子。", "IAEA Nuclear Energy Series"),
        new("delayed neutron", "缓发中子", "中子学", "由裂变产物先驱核衰变产生的中子。", "IAEA Nuclear Energy Series"),
        new("thermal neutron", "热中子", "中子学", "与介质热运动近似达到平衡的中子。", "IAEA Safety Glossary"),
        new("fast neutron", "快中子", "中子学", "能量处于快能区的中子。", "IAEA Safety Glossary"),
        new("neutron moderation", "中子慢化", "中子学", "中子通过散射降低能量的过程。", "IAEA Nuclear Energy Series"),
        new("neutron diffusion", "中子扩散", "中子学", "中子在介质中的统计输运过程。", "IAEA Nuclear Energy Series"),
        new("macroscopic cross section", "宏观截面", "核数据", "单位路径长度发生指定相互作用的概率参数。", "IAEA Nuclear Energy Series"),
        new("microscopic cross section", "微观截面", "核数据", "单个靶核发生指定相互作用的概率表征。", "IAEA Nuclear Energy Series"),
        new("multiplication factor", "增殖因数", "临界理论", "一代中子数与前一代中子数之比。", "IAEA Safety Glossary"),
        new("effective multiplication factor", "有效增殖因数", "临界理论", "计及有限系统泄漏的增殖因数。", "IAEA Safety Glossary", ["k-effective", "k eff"]),
        new("criticality", "临界状态", "临界理论", "链式裂变反应可自持的状态。", "IAEA Safety Glossary"),
        new("subcritical", "次临界", "临界理论", "有效增殖因数小于一的状态。", "IAEA Safety Glossary"),
        new("supercritical", "超临界", "临界理论", "有效增殖因数大于一的状态。", "IAEA Safety Glossary"),
        new("reactivity", "反应性", "反应堆动力学", "反应堆偏离临界程度的量度。", "IAEA Safety Glossary"),
        new("reactivity coefficient", "反应性系数", "反应堆动力学", "某参数单位变化导致的反应性变化。", "IAEA Safety Glossary"),
        new("delayed neutron fraction", "缓发中子份额", "反应堆动力学", "缓发中子数占裂变中子总数的份额。", "IAEA Nuclear Energy Series"),
        new("effective delayed neutron fraction", "有效缓发中子份额", "反应堆动力学", "按中子重要性加权的缓发中子份额。", "IAEA Nuclear Energy Series"),
        new("neutron generation time", "中子代时间", "反应堆动力学", "相邻两代中子产生之间的平均时间。", "IAEA Nuclear Energy Series"),
        new("prompt critical", "瞬发临界", "反应堆动力学", "仅靠瞬发中子即可维持临界的状态。", "IAEA Safety Glossary"),
        new("control rod worth", "控制棒价值", "反应性控制", "控制棒位置变化所引起的反应性变化量。", "IAEA Nuclear Energy Series"),
        new("burnup", "燃耗", "燃料性能", "核燃料单位初始质量释放的能量或消耗程度。", "IAEA Safety Glossary"),
        new("power peaking factor", "功率峰因子", "堆芯物理", "局部峰值功率与相应平均功率的比值。", "IAEA Nuclear Energy Series")
    ];

    private sealed record BuiltInSeed(
        string English,
        string Chinese,
        string Category,
        string Explanation,
        string SourceReference,
        string[]? Aliases = null);
}
