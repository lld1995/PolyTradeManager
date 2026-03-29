using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace PolyTradeManager;

/// <summary>
/// SQLite-based local cache for Polymarket data.
/// Supports incremental updates by tracking last-sync timestamps and block numbers.
/// </summary>
sealed class CacheDb : IDisposable
{
    private readonly SqliteConnection _conn;

    public CacheDb(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        InitTables();
    }

    // ═══════════════════════════════════════════
    //  Schema
    // ═══════════════════════════════════════════
    private void InitTables()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
PRAGMA journal_mode=WAL;

CREATE TABLE IF NOT EXISTS trades (
    id              TEXT PRIMARY KEY,
    match_time      TEXT NOT NULL,
    side            TEXT NOT NULL,
    outcome         TEXT NOT NULL,
    quantity        REAL NOT NULL,
    price           REAL NOT NULL,
    status          TEXT NOT NULL,
    condition_id    TEXT,
    token_id        TEXT,
    transaction_hash TEXT
);
CREATE INDEX IF NOT EXISTS idx_trades_match_time ON trades(match_time);
CREATE INDEX IF NOT EXISTS idx_trades_condition_id ON trades(condition_id);

CREATE TABLE IF NOT EXISTS markets (
    condition_id    TEXT PRIMARY KEY,
    question        TEXT,
    closed          INTEGER NOT NULL DEFAULT 0,
    outcome_prices  TEXT,
    outcomes        TEXT,
    raw_json        TEXT,
    updated_at      TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS balances (
    token_id        TEXT PRIMARY KEY,
    balance         REAL NOT NULL,
    updated_at      TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS redeems (
    condition_id    TEXT PRIMARY KEY,
    source          TEXT NOT NULL,
    transaction_ids TEXT NOT NULL,
    updated_at      TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS usdc_transfers (
    tx_hash         TEXT NOT NULL,
    direction       TEXT NOT NULL,
    amount          REAL NOT NULL,
    block_number    INTEGER,
    PRIMARY KEY (tx_hash, direction)
);

CREATE TABLE IF NOT EXISTS sync_state (
    key             TEXT PRIMARY KEY,
    value           TEXT NOT NULL
);
";
        cmd.ExecuteNonQuery();
    }

    // ═══════════════════════════════════════════
    //  Sync State
    // ═══════════════════════════════════════════
    public string? GetSyncState(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM sync_state WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSyncState(string key, string value)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO sync_state (key, value) VALUES (@key, @value)";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.ExecuteNonQuery();
    }

    // ═══════════════════════════════════════════
    //  Trades
    // ═══════════════════════════════════════════
    public void UpsertTrade(string id, DateTime matchTime, string side, string outcome,
        decimal quantity, decimal price, string status, string? conditionId, string? tokenId, string? transactionHash)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT OR REPLACE INTO trades (id, match_time, side, outcome, quantity, price, status, condition_id, token_id, transaction_hash)
VALUES (@id, @mt, @side, @outcome, @qty, @price, @status, @cid, @tid, @txh)";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@mt", matchTime.ToString("o"));
        cmd.Parameters.AddWithValue("@side", side);
        cmd.Parameters.AddWithValue("@outcome", outcome);
        cmd.Parameters.AddWithValue("@qty", (double)quantity);
        cmd.Parameters.AddWithValue("@price", (double)price);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@cid", (object?)conditionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tid", (object?)tokenId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@txh", (object?)transactionHash ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void BulkUpsertTrades(List<CachedTrade> trades)
    {
        using var tx = _conn.BeginTransaction();
        foreach (var t in trades)
        {
            UpsertTrade(t.Id, t.MatchTime, t.Side, t.Outcome, t.Quantity, t.Price,
                t.Status, t.ConditionId, t.TokenId, t.TransactionHash);
        }
        tx.Commit();
    }

    public List<CachedTrade> GetAllTrades()
    {
        var list = new List<CachedTrade>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, match_time, side, outcome, quantity, price, status, condition_id, token_id, transaction_hash FROM trades ORDER BY match_time DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new CachedTrade
            {
                Id = reader.GetString(0),
                MatchTime = DateTime.Parse(reader.GetString(1)),
                Side = reader.GetString(2),
                Outcome = reader.GetString(3),
                Quantity = (decimal)reader.GetDouble(4),
                Price = (decimal)reader.GetDouble(5),
                Status = reader.GetString(6),
                ConditionId = reader.IsDBNull(7) ? null : reader.GetString(7),
                TokenId = reader.IsDBNull(8) ? null : reader.GetString(8),
                TransactionHash = reader.IsDBNull(9) ? null : reader.GetString(9),
            });
        }
        return list;
    }

    public DateTime? GetLatestTradeTime()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(match_time) FROM trades";
        var result = cmd.ExecuteScalar();
        if (result is string s && DateTime.TryParse(s, out var dt))
            return dt;
        return null;
    }

    public int GetTradeCount()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM trades";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ═══════════════════════════════════════════
    //  Markets
    // ═══════════════════════════════════════════
    public void UpsertMarket(string conditionId, string? question, bool closed,
        decimal[]? outcomePrices, string[]? outcomes, string? rawJson)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT OR REPLACE INTO markets (condition_id, question, closed, outcome_prices, outcomes, raw_json, updated_at)
VALUES (@cid, @q, @closed, @op, @oc, @raw, @ua)";
        cmd.Parameters.AddWithValue("@cid", conditionId);
        cmd.Parameters.AddWithValue("@q", (object?)question ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@closed", closed ? 1 : 0);
        cmd.Parameters.AddWithValue("@op", outcomePrices != null ? JsonConvert.SerializeObject(outcomePrices) : DBNull.Value);
        cmd.Parameters.AddWithValue("@oc", outcomes != null ? JsonConvert.SerializeObject(outcomes) : DBNull.Value);
        cmd.Parameters.AddWithValue("@raw", (object?)rawJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ua", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, CachedMarket> GetAllMarkets()
    {
        var map = new Dictionary<string, CachedMarket>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT condition_id, question, closed, outcome_prices, outcomes FROM markets";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var cid = reader.GetString(0);
            map[cid] = new CachedMarket
            {
                ConditionId = cid,
                Question = reader.IsDBNull(1) ? null : reader.GetString(1),
                Closed = reader.GetInt32(2) == 1,
                OutcomePrices = reader.IsDBNull(3) ? null : JsonConvert.DeserializeObject<decimal[]>(reader.GetString(3)),
                Outcomes = reader.IsDBNull(4) ? null : JsonConvert.DeserializeObject<string[]>(reader.GetString(4)),
            };
        }
        return map;
    }

    /// <summary>Returns conditionIds of markets that are NOT yet closed (need re-check).</summary>
    public List<string> GetOpenMarketConditionIds()
    {
        var list = new List<string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT condition_id FROM markets WHERE closed = 0";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(reader.GetString(0));
        return list;
    }

    /// <summary>Returns conditionIds that have trades but no market record.</summary>
    public List<string> GetMissingMarketConditionIds()
    {
        var list = new List<string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
SELECT DISTINCT t.condition_id FROM trades t
LEFT JOIN markets m ON LOWER(t.condition_id) = LOWER(m.condition_id)
WHERE t.condition_id IS NOT NULL AND m.condition_id IS NULL";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(reader.GetString(0));
        return list;
    }

    // ═══════════════════════════════════════════
    //  Balances
    // ═══════════════════════════════════════════
    public void UpsertBalance(string tokenId, decimal balance)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT OR REPLACE INTO balances (token_id, balance, updated_at)
VALUES (@tid, @bal, @ua)";
        cmd.Parameters.AddWithValue("@tid", tokenId);
        cmd.Parameters.AddWithValue("@bal", (double)balance);
        cmd.Parameters.AddWithValue("@ua", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public void BulkUpsertBalances(Dictionary<string, decimal> balances)
    {
        using var tx = _conn.BeginTransaction();
        foreach (var kv in balances)
            UpsertBalance(kv.Key, kv.Value);
        tx.Commit();
    }

    public Dictionary<string, decimal> GetAllBalances()
    {
        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT token_id, balance FROM balances";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            map[reader.GetString(0)] = (decimal)reader.GetDouble(1);
        return map;
    }

    // ═══════════════════════════════════════════
    //  Redeems
    // ═══════════════════════════════════════════
    public void UpsertRedeem(string conditionId, string source, List<string> transactionIds)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT OR REPLACE INTO redeems (condition_id, source, transaction_ids, updated_at)
VALUES (@cid, @src, @txids, @ua)";
        cmd.Parameters.AddWithValue("@cid", conditionId);
        cmd.Parameters.AddWithValue("@src", source);
        cmd.Parameters.AddWithValue("@txids", JsonConvert.SerializeObject(transactionIds));
        cmd.Parameters.AddWithValue("@ua", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, CachedRedeem> GetAllRedeems()
    {
        var map = new Dictionary<string, CachedRedeem>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT condition_id, source, transaction_ids FROM redeems";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var cid = reader.GetString(0);
            map[cid] = new CachedRedeem
            {
                ConditionId = cid,
                Source = reader.GetString(1),
                TransactionIds = JsonConvert.DeserializeObject<List<string>>(reader.GetString(2)) ?? new(),
            };
        }
        return map;
    }

    // ═══════════════════════════════════════════
    //  USDC Transfers
    // ═══════════════════════════════════════════
    public void UpsertUsdcTransfer(string txHash, string direction, decimal amount, long? blockNumber)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT OR REPLACE INTO usdc_transfers (tx_hash, direction, amount, block_number)
VALUES (@txh, @dir, @amt, @bn)";
        cmd.Parameters.AddWithValue("@txh", txHash);
        cmd.Parameters.AddWithValue("@dir", direction);
        cmd.Parameters.AddWithValue("@amt", (double)amount);
        cmd.Parameters.AddWithValue("@bn", (object?)blockNumber ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void BulkUpsertUsdcTransfers(Dictionary<string, decimal> transfers, string direction)
    {
        using var tx = _conn.BeginTransaction();
        foreach (var kv in transfers)
            UpsertUsdcTransfer(kv.Key, direction, kv.Value, null);
        tx.Commit();
    }

    public Dictionary<string, decimal> GetUsdcTransfers(string direction)
    {
        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT tx_hash, amount FROM usdc_transfers WHERE direction = @dir";
        cmd.Parameters.AddWithValue("@dir", direction);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var txHash = reader.GetString(0);
            var amount = (decimal)reader.GetDouble(1);
            if (map.ContainsKey(txHash))
                map[txHash] += amount;
            else
                map[txHash] = amount;
        }
        return map;
    }

    // ═══════════════════════════════════════════
    //  Maintenance
    // ═══════════════════════════════════════════
    public void ClearAll()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
DELETE FROM trades;
DELETE FROM markets;
DELETE FROM balances;
DELETE FROM redeems;
DELETE FROM usdc_transfers;
DELETE FROM sync_state;
";
        cmd.ExecuteNonQuery();
    }

    public (int trades, int markets, int balances, int redeems, int usdcIn, int usdcOut) GetStats()
    {
        int Count(string table)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        int CountDir(string dir)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM usdc_transfers WHERE direction = @dir";
            cmd.Parameters.AddWithValue("@dir", dir);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        return (Count("trades"), Count("markets"), Count("balances"), Count("redeems"), CountDir("in"), CountDir("out"));
    }

    public void Dispose()
    {
        _conn?.Dispose();
    }
}

// ═══════════════════════════════════════════
//  Cache DTOs
// ═══════════════════════════════════════════
class CachedTrade
{
    public string Id { get; set; } = "";
    public DateTime MatchTime { get; set; }
    public string Side { get; set; } = "";
    public string Outcome { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public string Status { get; set; } = "";
    public string? ConditionId { get; set; }
    public string? TokenId { get; set; }
    public string? TransactionHash { get; set; }
}

class CachedMarket
{
    public string ConditionId { get; set; } = "";
    public string? Question { get; set; }
    public bool Closed { get; set; }
    public decimal[]? OutcomePrices { get; set; }
    public string[]? Outcomes { get; set; }
}

class CachedRedeem
{
    public string ConditionId { get; set; } = "";
    public List<string> TransactionIds { get; set; } = new();
    public string Source { get; set; } = "";
}
