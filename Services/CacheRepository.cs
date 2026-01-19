using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Hotkey_Translator.Services;

public sealed class CacheRepository : IDisposable
{
    private readonly ConcurrentDictionary<string, string> _memory = new(StringComparer.Ordinal);
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CacheRepository(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        _connection.Open();
        EnsureSchema();
    }

    public async Task<string?> TryGetAsync(string key, CancellationToken cancellationToken)
    {
        if (_memory.TryGetValue(key, out var value))
        {
            return value;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT translated_text FROM translations WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            value = reader.GetString(0);
            _memory[key] = value;

            await using var update = _connection.CreateCommand();
            update.CommandText = "UPDATE translations SET hit_count = hit_count + 1 WHERE key = $key";
            update.Parameters.AddWithValue("$key", key);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return value;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(string key, string translatedText, CancellationToken cancellationToken)
    {
        _memory[key] = translatedText;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = @"
INSERT INTO translations (key, translated_text, created_at, hit_count)
VALUES ($key, $translated, $created, 1)
ON CONFLICT(key) DO UPDATE SET
    translated_text = excluded.translated_text,
    hit_count = translations.hit_count + 1";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$translated", translatedText);
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _connection.Dispose();
    }

    private void EnsureSchema()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS translations (
    key TEXT PRIMARY KEY,
    translated_text TEXT NOT NULL,
    created_at TEXT NOT NULL,
    hit_count INTEGER NOT NULL
)";
        command.ExecuteNonQuery();
    }
}
