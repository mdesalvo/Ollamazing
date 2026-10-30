using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Ollamazing.Web.Catalog;

/// <summary>Persists the ollama.com catalog in a SQLite database, so a restart does not depend on ollama.com being reachable.</summary>
/// <remarks>Every operation opens its own connection: SQLite serialises writers and WAL lets readers proceed meanwhile.</remarks>
public sealed class CatalogStore(IOptions<CatalogOptions> options)
{
    /// <summary>Bumped whenever the scraper starts extracting different data; a database with another version has its tables rebuilt.</summary>
    public const int SchemaVersion = 1;

    private const string DateOnlyFormat = "yyyy-MM-dd";

    /// <summary>Absolute path of the database file, resolved once from the configuration.</summary>
    public string DatabasePath { get; } = ResolveDatabasePath(options.Value.DatabasePath);

    /// <summary>Prepares the database and reads the persisted catalog; null when no refresh has been stored yet.</summary>
    public async Task<CatalogSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
        await PrepareSchemaAsync(connection, cancellationToken);

        // Without a state row no refresh ever completed, so there is nothing to restore.
        DateTimeOffset refreshedAt;
        string? pricingNote;
        await using (SqliteCommand stateCommand = connection.CreateCommand())
        {
            stateCommand.CommandText = "SELECT refreshed_at, pricing_note FROM catalog_state WHERE id = 1";
            await using SqliteDataReader stateReader = await stateCommand.ExecuteReaderAsync(cancellationToken);
            if (!await stateReader.ReadAsync(cancellationToken))
                return null;
            refreshedAt = ParseDateTimeOffset(stateReader.GetString(0));
            pricingNote = stateReader.IsDBNull(1) ? null : stateReader.GetString(1);
        }

        // Tags and prices are read in one pass each and grouped by model, instead of one query per model.
        Dictionary<string, List<CatalogTag>> tagsByModel = await ReadTagsAsync(connection, cancellationToken);
        Dictionary<string, List<CatalogPrice>> pricesByModel = await ReadPricesAsync(connection, cancellationToken);

        List<CatalogModel> models = [];
        await using SqliteCommand modelsCommand = connection.CreateCommand();
        modelsCommand.CommandText = """
            SELECT name, description, capabilities, parameter_sizes, offers_cloud, is_cloud_free, cloud_usage_level,
                   downloads, context_window, updated_on, popularity_rank, tags_loaded_at, first_seen_at
            FROM models ORDER BY popularity_rank
            """;
        await using SqliteDataReader modelsReader = await modelsCommand.ExecuteReaderAsync(cancellationToken);
        while (await modelsReader.ReadAsync(cancellationToken))
        {
            string name = modelsReader.GetString(0);
            models.Add(new CatalogModel
            {
                Name = name,
                Description = modelsReader.GetString(1),
                Capabilities = JsonSerializer.Deserialize<List<string>>(modelsReader.GetString(2)) ?? [],
                ParameterSizes = JsonSerializer.Deserialize<List<string>>(modelsReader.GetString(3)) ?? [],
                OffersCloud = modelsReader.GetInt64(4) != 0,
                IsCloudFree = modelsReader.GetInt64(5) != 0,
                CloudUsageLevel = modelsReader.GetInt32(6),
                Downloads = modelsReader.IsDBNull(7) ? null : modelsReader.GetInt64(7),
                ContextWindow = modelsReader.IsDBNull(8) ? null : modelsReader.GetString(8),
                UpdatedOn = modelsReader.IsDBNull(9) ? null : DateOnly.ParseExact(modelsReader.GetString(9), DateOnlyFormat, CultureInfo.InvariantCulture),
                PopularityRank = modelsReader.GetInt32(10),
                TagsLoadedAt = modelsReader.IsDBNull(11) ? null : ParseDateTimeOffset(modelsReader.GetString(11)),
                FirstSeenAt = modelsReader.IsDBNull(12) ? null : ParseDateTimeOffset(modelsReader.GetString(12)),
                Tags = tagsByModel.GetValueOrDefault(name) ?? [],
                Prices = pricesByModel.GetValueOrDefault(name) ?? []
            });
        }

        // The snapshot hands the models back in popularity order as the query produced them.
        return new CatalogSnapshot(refreshedAt, models, pricingNote);
    }

    /// <summary>Replaces the whole stored catalog with the snapshot in a single transaction, so readers never see a half-written refresh.</summary>
    public async Task ReplaceCatalogAsync(CatalogSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        // Tags and prices of the old models go away through the foreign key cascade.
        await using (SqliteCommand deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM models";
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        // The catalog has a single state row, overwritten by every refresh.
        await using (SqliteCommand stateCommand = connection.CreateCommand())
        {
            stateCommand.Transaction = transaction;
            stateCommand.CommandText = """
                INSERT INTO catalog_state (id, refreshed_at, pricing_note) VALUES (1, $refreshedAt, $pricingNote)
                ON CONFLICT (id) DO UPDATE SET refreshed_at = excluded.refreshed_at, pricing_note = excluded.pricing_note
                """;
            stateCommand.Parameters.AddWithValue("$refreshedAt", FormatDateTimeOffset(snapshot.RefreshedAt));
            stateCommand.Parameters.AddWithValue("$pricingNote", ToDatabaseValue(snapshot.PricingNote));
            await stateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        // One command per table is built up front and only its parameter values change from row to row.
        await using SqliteCommand modelCommand = connection.CreateCommand();
        modelCommand.Transaction = transaction;
        modelCommand.CommandText = """
            INSERT INTO models (name, description, capabilities, parameter_sizes, offers_cloud, is_cloud_free, cloud_usage_level,
                                downloads, context_window, updated_on, popularity_rank, tags_loaded_at, first_seen_at)
            VALUES ($name, $description, $capabilities, $parameterSizes, $offersCloud, $isCloudFree, $cloudUsageLevel,
                    $downloads, $contextWindow, $updatedOn, $popularityRank, $tagsLoadedAt, $firstSeenAt)
            """;
        SqliteParameter name = modelCommand.Parameters.Add("$name", SqliteType.Text);
        SqliteParameter description = modelCommand.Parameters.Add("$description", SqliteType.Text);
        SqliteParameter capabilities = modelCommand.Parameters.Add("$capabilities", SqliteType.Text);
        SqliteParameter parameterSizes = modelCommand.Parameters.Add("$parameterSizes", SqliteType.Text);
        SqliteParameter offersCloud = modelCommand.Parameters.Add("$offersCloud", SqliteType.Integer);
        SqliteParameter isCloudFree = modelCommand.Parameters.Add("$isCloudFree", SqliteType.Integer);
        SqliteParameter cloudUsageLevel = modelCommand.Parameters.Add("$cloudUsageLevel", SqliteType.Integer);
        SqliteParameter downloads = modelCommand.Parameters.Add("$downloads", SqliteType.Integer);
        SqliteParameter contextWindow = modelCommand.Parameters.Add("$contextWindow", SqliteType.Text);
        SqliteParameter updatedOn = modelCommand.Parameters.Add("$updatedOn", SqliteType.Text);
        SqliteParameter popularityRank = modelCommand.Parameters.Add("$popularityRank", SqliteType.Integer);
        SqliteParameter tagsLoadedAt = modelCommand.Parameters.Add("$tagsLoadedAt", SqliteType.Text);
        SqliteParameter firstSeenAt = modelCommand.Parameters.Add("$firstSeenAt", SqliteType.Text);

        await using SqliteCommand tagCommand = CreateTagInsertCommand(connection, transaction, out SqliteParameter[] tagParameters);

        await using SqliteCommand priceCommand = connection.CreateCommand();
        priceCommand.Transaction = transaction;
        priceCommand.CommandText = """
            INSERT INTO prices (model_name, position, variant, is_off_peak, input, cached_input, output)
            VALUES ($modelName, $position, $variant, $isOffPeak, $input, $cachedInput, $output)
            """;
        SqliteParameter priceModelName = priceCommand.Parameters.Add("$modelName", SqliteType.Text);
        SqliteParameter pricePosition = priceCommand.Parameters.Add("$position", SqliteType.Integer);
        SqliteParameter variant = priceCommand.Parameters.Add("$variant", SqliteType.Text);
        SqliteParameter isOffPeak = priceCommand.Parameters.Add("$isOffPeak", SqliteType.Integer);
        SqliteParameter input = priceCommand.Parameters.Add("$input", SqliteType.Text);
        SqliteParameter cachedInput = priceCommand.Parameters.Add("$cachedInput", SqliteType.Text);
        SqliteParameter output = priceCommand.Parameters.Add("$output", SqliteType.Text);

        foreach (CatalogModel model in snapshot.Models)
        {
            // Each model row carries its list-valued fields as JSON and its optional fields as NULL.
            name.Value = model.Name;
            description.Value = model.Description;
            capabilities.Value = JsonSerializer.Serialize(model.Capabilities);
            parameterSizes.Value = JsonSerializer.Serialize(model.ParameterSizes);
            offersCloud.Value = model.OffersCloud ? 1 : 0;
            isCloudFree.Value = model.IsCloudFree ? 1 : 0;
            cloudUsageLevel.Value = model.CloudUsageLevel;
            downloads.Value = ToDatabaseValue(model.Downloads);
            contextWindow.Value = ToDatabaseValue(model.ContextWindow);
            updatedOn.Value = ToDatabaseValue(model.UpdatedOn?.ToString(DateOnlyFormat, CultureInfo.InvariantCulture));
            popularityRank.Value = model.PopularityRank;
            tagsLoadedAt.Value = ToDatabaseValue(model.TagsLoadedAt is { } loadedAt ? FormatDateTimeOffset(loadedAt) : null);
            firstSeenAt.Value = ToDatabaseValue(model.FirstSeenAt is { } seenAt ? FormatDateTimeOffset(seenAt) : null);
            await modelCommand.ExecuteNonQueryAsync(cancellationToken);

            // Tags go in after their model because they reference it.
            await InsertTagsAsync(tagCommand, tagParameters, model.Name, model.Tags, cancellationToken);

            // The list index is stored so the rates come back in the order the pricing table printed them.
            for (int position = 0; position < model.Prices.Count; position++)
            {
                CatalogPrice price = model.Prices[position];
                priceModelName.Value = model.Name;
                pricePosition.Value = position;
                variant.Value = ToDatabaseValue(price.Variant);
                isOffPeak.Value = price.IsOffPeak ? 1 : 0;
                input.Value = ToDatabaseValue(price.Input?.ToString(CultureInfo.InvariantCulture));
                cachedInput.Value = ToDatabaseValue(price.CachedInput?.ToString(CultureInfo.InvariantCulture));
                output.Value = ToDatabaseValue(price.Output?.ToString(CultureInfo.InvariantCulture));
                await priceCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Stores the tags just read for one model and stamps when they were read; does nothing when the model has left the catalog.</summary>
    public async Task SaveTagsAsync(string modelName, IReadOnlyList<CatalogTag> tags, DateTimeOffset loadedAt, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        // The stamp doubles as the existence check: a refresh may have dropped the model while its tags were loading.
        await using (SqliteCommand stampCommand = connection.CreateCommand())
        {
            stampCommand.Transaction = transaction;
            stampCommand.CommandText = "UPDATE models SET tags_loaded_at = $loadedAt WHERE name = $name";
            stampCommand.Parameters.AddWithValue("$loadedAt", FormatDateTimeOffset(loadedAt));
            stampCommand.Parameters.AddWithValue("$name", modelName);
            if (await stampCommand.ExecuteNonQueryAsync(cancellationToken) == 0)
                return;
        }

        // The previous tags of the model are replaced as a whole by the fresh list.
        await using (SqliteCommand deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM tags WHERE model_name = $name";
            deleteCommand.Parameters.AddWithValue("$name", modelName);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using SqliteCommand tagCommand = CreateTagInsertCommand(connection, transaction, out SqliteParameter[] tagParameters);
        await InsertTagsAsync(tagCommand, tagParameters, modelName, tags, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Resolves the configured database path, falling back to the per-user application data folder.</summary>
    private static string ResolveDatabasePath(string configuredPath)
        => string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ollamazing", "ollamazing.db")
            : Path.GetFullPath(configuredPath);

    /// <summary>Opens a connection to the database with foreign keys enforced, which the tag and price cascades rely on.</summary>
    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        // The data folder does not exist on the first run.
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

        SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);

            // SQLite leaves foreign keys off by default and the setting belongs to the connection.
            await using SqliteCommand pragmaCommand = connection.CreateCommand();
            pragmaCommand.CommandText = "PRAGMA foreign_keys = ON;";
            await pragmaCommand.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            // A connection that failed halfway is released here because the caller never receives it.
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Rebuilds the tables whenever the stored schema version differs from <see cref="SchemaVersion"/>; a new database counts as version 0.</summary>
    private static async Task PrepareSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // The version lives in the database header, so it survives with the file.
        await using SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        long storedVersion = (long)(await versionCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);

        // A matching version means the tables are already the ones this code reads.
        if (storedVersion == SchemaVersion)
            return;

        // Persisted data is only a cache of ollama.com, so a version change drops it instead of migrating it.
        await using SqliteCommand schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText = $"""
            DROP TABLE IF EXISTS prices;
            DROP TABLE IF EXISTS tags;
            DROP TABLE IF EXISTS models;
            DROP TABLE IF EXISTS catalog_state;

            CREATE TABLE catalog_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                refreshed_at TEXT NOT NULL,
                pricing_note TEXT);

            CREATE TABLE models (
                name TEXT PRIMARY KEY,
                description TEXT NOT NULL,
                capabilities TEXT NOT NULL,
                parameter_sizes TEXT NOT NULL,
                offers_cloud INTEGER NOT NULL,
                is_cloud_free INTEGER NOT NULL,
                cloud_usage_level INTEGER NOT NULL,
                downloads INTEGER,
                context_window TEXT,
                updated_on TEXT,
                popularity_rank INTEGER NOT NULL,
                tags_loaded_at TEXT,
                first_seen_at TEXT);

            CREATE TABLE tags (
                full_name TEXT PRIMARY KEY,
                model_name TEXT NOT NULL REFERENCES models(name) ON DELETE CASCADE,
                position INTEGER NOT NULL,
                size_bytes INTEGER,
                context_window TEXT,
                inputs TEXT,
                digest TEXT,
                is_latest INTEGER NOT NULL);

            CREATE INDEX tags_model ON tags(model_name);

            CREATE TABLE prices (
                model_name TEXT NOT NULL REFERENCES models(name) ON DELETE CASCADE,
                position INTEGER NOT NULL,
                variant TEXT,
                is_off_peak INTEGER NOT NULL,
                input TEXT,
                cached_input TEXT,
                output TEXT,
                PRIMARY KEY (model_name, position));

            PRAGMA user_version = {SchemaVersion};
            PRAGMA journal_mode = WAL;
            """;
        await schemaCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Reads every stored tag in the order of its model's list, grouped by model name.</summary>
    private static async Task<Dictionary<string, List<CatalogTag>>> ReadTagsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        Dictionary<string, List<CatalogTag>> tagsByModel = new(StringComparer.Ordinal);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT model_name, full_name, size_bytes, context_window, inputs, digest, is_latest FROM tags ORDER BY model_name, position";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // Rows arrive sorted by model and position, so appending preserves each model's tag order.
            string modelName = reader.GetString(0);
            if (!tagsByModel.TryGetValue(modelName, out List<CatalogTag>? modelTags))
                tagsByModel[modelName] = modelTags = [];
            modelTags.Add(new CatalogTag
            {
                FullName = reader.GetString(1),
                SizeBytes = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                ContextWindow = reader.IsDBNull(3) ? null : reader.GetString(3),
                Inputs = reader.IsDBNull(4) ? null : reader.GetString(4),
                Digest = reader.IsDBNull(5) ? null : reader.GetString(5),
                IsLatest = reader.GetInt64(6) != 0
            });
        }

        return tagsByModel;
    }

    /// <summary>Reads every stored rate in the order of its model's pricing rows, grouped by model name.</summary>
    private static async Task<Dictionary<string, List<CatalogPrice>>> ReadPricesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        Dictionary<string, List<CatalogPrice>> pricesByModel = new(StringComparer.Ordinal);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT model_name, variant, is_off_peak, input, cached_input, output FROM prices ORDER BY model_name, position";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // Rows arrive sorted by model and position, so appending preserves each model's rate order.
            string modelName = reader.GetString(0);
            if (!pricesByModel.TryGetValue(modelName, out List<CatalogPrice>? modelPrices))
                pricesByModel[modelName] = modelPrices = [];
            modelPrices.Add(new CatalogPrice
            {
                Variant = reader.IsDBNull(1) ? null : reader.GetString(1),
                IsOffPeak = reader.GetInt64(2) != 0,
                Input = ParseRate(reader, 3),
                CachedInput = ParseRate(reader, 4),
                Output = ParseRate(reader, 5)
            });
        }

        return pricesByModel;
    }

    /// <summary>Builds the tag insert command shared by a full replacement and a single-model tag save.</summary>
    /// <param name="tagParameters">Parameters of the command in the order <see cref="InsertTagsAsync"/> fills them.</param>
    private static SqliteCommand CreateTagInsertCommand(SqliteConnection connection, SqliteTransaction transaction, out SqliteParameter[] tagParameters)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tags (full_name, model_name, position, size_bytes, context_window, inputs, digest, is_latest)
            VALUES ($fullName, $modelName, $position, $sizeBytes, $contextWindow, $inputs, $digest, $isLatest)
            """;
        tagParameters =
        [
            command.Parameters.Add("$fullName", SqliteType.Text),
            command.Parameters.Add("$modelName", SqliteType.Text),
            command.Parameters.Add("$position", SqliteType.Integer),
            command.Parameters.Add("$sizeBytes", SqliteType.Integer),
            command.Parameters.Add("$contextWindow", SqliteType.Text),
            command.Parameters.Add("$inputs", SqliteType.Text),
            command.Parameters.Add("$digest", SqliteType.Text),
            command.Parameters.Add("$isLatest", SqliteType.Integer)
        ];
        return command;
    }

    /// <summary>Inserts the tags of one model, storing each one's list index so the order survives a restart.</summary>
    private static async Task InsertTagsAsync(
        SqliteCommand tagCommand,
        SqliteParameter[] tagParameters,
        string modelName,
        IReadOnlyList<CatalogTag> tags,
        CancellationToken cancellationToken)
    {
        for (int position = 0; position < tags.Count; position++)
        {
            // The parameter order is the one fixed by CreateTagInsertCommand.
            CatalogTag tag = tags[position];
            tagParameters[0].Value = tag.FullName;
            tagParameters[1].Value = modelName;
            tagParameters[2].Value = position;
            tagParameters[3].Value = ToDatabaseValue(tag.SizeBytes);
            tagParameters[4].Value = ToDatabaseValue(tag.ContextWindow);
            tagParameters[5].Value = ToDatabaseValue(tag.Inputs);
            tagParameters[6].Value = ToDatabaseValue(tag.Digest);
            tagParameters[7].Value = tag.IsLatest ? 1 : 0;
            await tagCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>Reads a rate column, where NULL means the pricing table printed a dash.</summary>
    private static decimal? ParseRate(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : decimal.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

    /// <summary>Writes a timestamp in the round-trip format that <see cref="ParseDateTimeOffset"/> reads back.</summary>
    private static string FormatDateTimeOffset(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Reads a timestamp written by <see cref="FormatDateTimeOffset"/>.</summary>
    private static DateTimeOffset ParseDateTimeOffset(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>Maps a C# null to SQL NULL, which a parameter value cannot express directly.</summary>
    private static object ToDatabaseValue(object? value) => value ?? DBNull.Value;
}
