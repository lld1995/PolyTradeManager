using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using Nethereum.JsonRpc.Client;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using Polymarket.Net;
using Polymarket.Net.Clients;
using Polymarket.Net.Enums;
using Polymarket.Net.Objects.Models;
using System.Globalization;
using System.Numerics;
using System.Text;
using PolyTradeManager;

// ═══════════════════════════════════════════
//  Config - load secrets from poly_secrets.txt
// ═══════════════════════════════════════════
var secretsPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "poly_secrets.txt");
if (!File.Exists(secretsPath))
    secretsPath = Path.Combine(AppContext.BaseDirectory, "poly_secrets.txt");
if (!File.Exists(secretsPath))
{
    Console.WriteLine($"找不到 poly_secrets.txt，请将其放在项目根目录或输出目录。");
    return;
}
var secrets = File.ReadAllLines(secretsPath)
    .Where(l => !string.IsNullOrWhiteSpace(l) && l.Contains('='))
    .ToDictionary(
        l => l[..l.IndexOf('=')].Trim(),
        l => l[(l.IndexOf('=') + 1)..].Trim());

string GetSecret(string key)
{
    if (secrets.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val))
        return val;
    Console.WriteLine($"poly_secrets.txt 中缺少 {key}");
    Environment.Exit(1);
    return null!;
}

var PRIVATE_KEY = GetSecret("PRIVATE_KEY");
var ETHERSCAN_API_KEY = GetSecret("ETHERSCAN_API_KEY");
var PROXY_URL = GetSecret("PROXY_URL");
var POLYGON_RPC = GetSecret("POLYGON_RPC");

const string CTF_ADDRESS = "0x4D97DCd97eC945f40cF65F87097ACe5EA0476045";
const string NEG_RISK_ADAPTER = "0xd91E80cF2E7be2e162c6513ceD346b7C0F5b9F95";
const string USDC_E_ADDRESS = "0x2791Bca1f2de4661ED88A30C99A7a9449Aa84174";
const string REDEEM_ABI = "[{\"inputs\":[{\"name\":\"collateralToken\",\"type\":\"address\"},{\"name\":\"parentCollectionId\",\"type\":\"bytes32\"},{\"name\":\"conditionId\",\"type\":\"bytes32\"},{\"name\":\"indexSets\",\"type\":\"uint256[]\"}],\"name\":\"redeemPositions\",\"outputs\":[],\"stateMutability\":\"nonpayable\",\"type\":\"function\"}]";
const string BALANCE_OF_ABI = "[{\"inputs\":[{\"name\":\"account\",\"type\":\"address\"},{\"name\":\"id\",\"type\":\"uint256\"}],\"name\":\"balanceOf\",\"outputs\":[{\"name\":\"\",\"type\":\"uint256\"}],\"stateMutability\":\"view\",\"type\":\"function\"}]";

// ═══════════════════════════════════════════
//  Init client
// ═══════════════════════════════════════════
var account = new Account(PRIVATE_KEY, 137);
var walletAddress = account.Address;
var l1Cred = new PolymarketL1Credential(SignType.EOA, PRIVATE_KEY);
var client = new PolymarketRestClient(opts =>
{
    opts.ApiCredentials = new PolymarketCredentials(l1Cred);
    var proxyUri = new Uri(PROXY_URL);
    opts.Proxy = new ApiProxy(proxyUri.GetLeftPart(UriPartial.Authority).Replace($":{proxyUri.Port}", ""), proxyUri.Port);
});

Console.WriteLine("正在获取 L2 凭证...");
var l2Creds = await client.ClobApi.Account.GetOrCreateApiCredentialsAsync();
if (!l2Creds.Success)
{
    Console.WriteLine($"获取 L2 凭证失败: {l2Creds.Error?.Message}");
    return;
}
client.UpdateL2Credentials(l2Creds.Data);
Console.WriteLine("L2 凭证 OK.\n");

// ═══════════════════════════════════════════
//  Local cache DB
// ═══════════════════════════════════════════
var cacheDbPath = Path.Combine(AppContext.BaseDirectory, "poly_cache.db");
var db = new CacheDb(cacheDbPath);

// ═══════════════════════════════════════════
//  In-memory data (loaded from cache + API)
// ═══════════════════════════════════════════
List<CachedTrade> cachedTrades = new();
Dictionary<string, CachedMarket> cachedMarkets = new(StringComparer.OrdinalIgnoreCase);
Dictionary<string, decimal> cachedBalances = new(StringComparer.OrdinalIgnoreCase);
Dictionary<string, CachedRedeem> cachedRedeems = new(StringComparer.OrdinalIgnoreCase);
Dictionary<string, decimal> cachedReceiveAmounts = new(StringComparer.OrdinalIgnoreCase);
Dictionary<string, decimal> cachedBuyCosts = new(StringComparer.OrdinalIgnoreCase);
bool dataLoaded = false;

async Task LoadData(bool force = false)
{
    if (dataLoaded && !force) return;

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var existingTradeCount = db.GetTradeCount();
    var isFirstLoad = existingTradeCount == 0;

    if (isFirstLoad)
        Console.WriteLine("首次运行，正在拉取全部数据...");
    else
        Console.WriteLine($"缓存中有 {existingTradeCount} 笔交易，正在增量更新...");

    // ── Step 1: Trades (incremental, or full when force) ──
    Console.WriteLine("正在拉取交易记录...");
    var latestTime = force ? null : db.GetLatestTradeTime();
    var newTrades = await FetchTradesIncremental(latestTime);
    if (newTrades.Count > 0)
    {
        int seqIdx = 0;
        var validTrades = newTrades
            .Where(t => t.Status == TradeStatus.Matched || t.Status == TradeStatus.Mined || t.Status == TradeStatus.Confirmed)
            .Select(t => new CachedTrade
            {
                Id = $"{t.TransactionHash}_{t.MatchTime:yyyyMMddHHmmssfff}_{t.TokenId}_{seqIdx++}",
                MatchTime = t.MatchTime,
                Side = t.Side.ToString(),
                Outcome = t.Outcome,
                Quantity = t.Quantity,
                Price = t.Price,
                Status = t.Status.ToString(),
                ConditionId = NormalizeConditionId(t.ConditionId),
                TokenId = t.TokenId,
                TransactionHash = t.TransactionHash,
            }).ToList();
        db.BulkUpsertTrades(validTrades);
        Console.WriteLine($"  新增/更新 {validTrades.Count} 笔交易");
    }
    else
    {
        Console.WriteLine("  没有新交易");
    }

    // Load all trades from cache
    cachedTrades = db.GetAllTrades();

    if (cachedTrades.Count > 0)
    {
        // ── Step 2: Markets (only missing + open ones) ──
        Console.WriteLine("正在更新市场状态...");
        var missingCids = db.GetMissingMarketConditionIds();
        var openCids = db.GetOpenMarketConditionIds();
        var cidsToFetch = missingCids.Concat(openCids).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (cidsToFetch.Length > 0)
        {
            Console.WriteLine($"  需查询 {cidsToFetch.Length} 个市场 ({missingCids.Count} 新 + {openCids.Count} 未结算)");
            var freshMarkets = await FetchMarketsByConditionIds(cidsToFetch);
            foreach (var kv in freshMarkets)
            {
                db.UpsertMarket(kv.Key, kv.Value.Question, kv.Value.Closed,
                    kv.Value.OutcomePrices, kv.Value.Outcomes, null);
            }
            Console.WriteLine($"  已更新 {freshMarkets.Count} 个市场");
        }
        else
        {
            Console.WriteLine("  所有市场已是最新");
        }

        // Load all markets from cache
        cachedMarkets = db.GetAllMarkets();

        // ── Step 3: USDC spent (incremental by checking new buy tx hashes) ──
        Console.WriteLine("正在获取链上真实买入成本...");
        var existingBuyCosts = db.GetUsdcTransfers("out");
        var allBuyTxHashes = cachedTrades
            .Where(t => t.Side == "Buy" && !string.IsNullOrWhiteSpace(t.TransactionHash))
            .Select(t => t.TransactionHash!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingBuyTxHashes = allBuyTxHashes.Where(h => !existingBuyCosts.ContainsKey(h)).ToList();

        if (missingBuyTxHashes.Count > 0 || isFirstLoad || force)
        {
            var freshBuyCosts = await FetchUsdcSpentByTx();
            db.BulkUpsertUsdcTransfers(freshBuyCosts, "out");
            Console.WriteLine($"  共 {freshBuyCosts.Count} 条USDC转出记录");
        }
        else
        {
            Console.WriteLine($"  缓存中有 {existingBuyCosts.Count} 条USDC转出记录 (无新增)");
        }
        cachedBuyCosts = db.GetUsdcTransfers("out");

        // ── Step 4: On-chain balances (always refresh for open markets) ──
        Console.WriteLine("正在查询链上持仓余额...");
        var tokenIds = cachedTrades
            .Select(t => t.TokenId)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
        var freshBalances = await FetchTokenBalances(tokenIds);
        db.BulkUpsertBalances(freshBalances);
        cachedBalances = freshBalances;

        // ── Step 5: Redeems (only for closed markets without existing redeem records) ──
        var closedConditionIds = cachedMarkets.Values
            .Where(m => m.Closed && !string.IsNullOrWhiteSpace(m.ConditionId))
            .Select(m => m.ConditionId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (closedConditionIds.Length > 0)
        {
            var existingRedeems = db.GetAllRedeems();
            var cidsNeedRedeem = closedConditionIds
                .Where(c => !existingRedeems.ContainsKey(c))
                .ToArray();

            if (cidsNeedRedeem.Length > 0 || isFirstLoad || force)
            {
                var searchCids = (isFirstLoad || force) ? closedConditionIds : cidsNeedRedeem;
                Console.WriteLine($"正在查询赎回交易 ({searchCids.Length} 个市场)...");
                var freshRedeems = await FetchRedeemTransactions(searchCids);
                foreach (var kv in freshRedeems)
                    db.UpsertRedeem(kv.Key, kv.Value.Source, kv.Value.TransactionIds);
                Console.WriteLine($"  已定位 {freshRedeems.Count} 条赎回交易");
            }
            else
            {
                Console.WriteLine($"赎回记录已缓存 ({existingRedeems.Count} 条)");
            }

            cachedRedeems = db.GetAllRedeems();

            // ── Step 6: USDC received (incremental) ──
            if (cachedRedeems.Count > 0)
            {
                var existingUsdcIn = db.GetUsdcTransfers("in");
                var redeemTxHashes = cachedRedeems.Values
                    .SelectMany(r => r.TransactionIds)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missingUsdcIn = redeemTxHashes.Where(h => !existingUsdcIn.ContainsKey(h)).ToList();

                if (missingUsdcIn.Count > 0 || isFirstLoad || force)
                {
                    Console.WriteLine("正在批量获取USDC到账记录...");
                    var freshUsdcIn = await FetchUsdcTransfersToWallet();
                    db.BulkUpsertUsdcTransfers(freshUsdcIn, "in");
                    Console.WriteLine($"  共 {freshUsdcIn.Count} 条USDC转入记录");
                }
                else
                {
                    Console.WriteLine($"USDC到账记录已缓存 ({existingUsdcIn.Count} 条)");
                }

                var usdcByTxHash = db.GetUsdcTransfers("in");
                cachedReceiveAmounts.Clear();
                int resolvedCount = 0;
                foreach (var kv in cachedRedeems)
                {
                    if (kv.Value.TransactionIds.Count == 0) continue;
                    decimal totalUsdc = 0;
                    foreach (var txId in kv.Value.TransactionIds)
                    {
                        if (usdcByTxHash.TryGetValue(txId, out var amt))
                            totalUsdc += amt;
                    }
                    cachedReceiveAmounts[kv.Key] = totalUsdc;
                    resolvedCount++;
                }
                Console.WriteLine($"已解析 {resolvedCount} 条到账金额");
            }
        }
    }

    dataLoaded = true;
    sw.Stop();
    Console.WriteLine($"加载完成: {cachedTrades.Count} 笔交易, {cachedMarkets.Count} 个市场 (耗时 {sw.Elapsed.TotalSeconds:F1}s)\n");
}

// ═══════════════════════════════════════════
//  Main loop
// ═══════════════════════════════════════════
await LoadData();

// Command-line mode: --redeem-all
if (args.Length > 0 && args[0] == "--redeem-all")
{
    await RedeemAll();
    client.Dispose();
    db.Dispose();
    return;
}

while (true)
{
    Console.WriteLine("╔══════════════════════════════════════╗");
    Console.WriteLine("║   Polymarket 交易管理工具            ║");
    Console.WriteLine("╠══════════════════════════════════════╣");
    Console.WriteLine("║  1. 查看所有交易                     ║");
    Console.WriteLine("║  2. 持仓汇总 (按市场聚合+结算状态)   ║");
    Console.WriteLine("║  3. 赎回指定 conditionId             ║");
    Console.WriteLine("║  4. 赎回全部可赎回                   ║");
    Console.WriteLine("║  5. 查看余额                         ║");
    Console.WriteLine("║  6. 增量刷新数据                     ║");
    Console.WriteLine("║  7. 全量刷新 (清除缓存重新拉取)      ║");
    Console.WriteLine("║  8. 缓存统计                         ║");
    Console.WriteLine("║  9. 清除 pending 交易 (Cancel stuck)  ║");
    Console.WriteLine("║  0. 退出                             ║");
    Console.WriteLine("╚══════════════════════════════════════╝");
    Console.Write("请选择: ");
    var choice = Console.ReadLine()?.Trim();

    switch (choice)
    {
        case "1": ShowAllTrades(); break;
        case "2": ShowPositionSummary(); break;
        case "3": await RedeemSpecific(); break;
        case "4": await RedeemAll(); break;
        case "5": await ShowBalance(); break;
        case "6": await LoadData(force: true); break;
        case "7": db.ClearAll(); dataLoaded = false; await LoadData(force: true); break;
        case "8": ShowCacheStats(); break;
        case "9": await CancelPendingTransactions(); break;
        case "0": goto exit;
        default: Console.WriteLine("无效选择\n"); break;
    }
}
exit:
Console.WriteLine("再见!");
client.Dispose();
db.Dispose();
return;

// ═══════════════════════════════════════════
//  1. 查看所有交易 (分页)
// ═══════════════════════════════════════════
void ShowAllTrades()
{
    if (cachedTrades.Count == 0)
    {
        Console.WriteLine("\n没有交易记录。\n");
        return;
    }

    const int pageSize = 20;
    var sorted = cachedTrades.OrderByDescending(t => t.MatchTime).ToList();
    int totalPages = (sorted.Count + pageSize - 1) / pageSize;
    int currentPage = 1;

    while (true)
    {
        Console.Clear();
        Console.WriteLine($"\n共 {sorted.Count} 笔交易  |  第 {currentPage}/{totalPages} 页\n");
        Console.WriteLine($"{"序号",-5} {"时间",-20} {"方向",-5} {"Outcome",-8} {"数量",-10} {"价格",-8} {"成本",-10} {"结算",-12} {"TxHash",-18} {"ConditionId",-20}");
        Console.WriteLine(new string('─', 150));

        var pageItems = sorted.Skip((currentPage - 1) * pageSize).Take(pageSize).ToList();
        for (int i = 0; i < pageItems.Count; i++)
        {
            var t = pageItems[i];
            var idx = (currentPage - 1) * pageSize + i + 1;
            var cost = t.Price * t.Quantity;
            var conditionId = NormalizeConditionId(t.ConditionId);
            cachedMarkets.TryGetValue(conditionId ?? "", out var market);
            var (settlement, sColor) = GetSettlement(market, t.Outcome, t.Side);

            Console.Write($"{idx,-5} {t.MatchTime:yyyy-MM-dd HH:mm:ss} {t.Side,-5} {t.Outcome,-8} {t.Quantity,-10:F2} {t.Price,-8:F4} {cost,-10:F4} ");
            Console.ForegroundColor = sColor;
            Console.Write($"{settlement,-12}");
            Console.ResetColor();
            Console.Write($" {Shorten(t.TransactionHash),-18}");
            Console.WriteLine($" {Shorten(conditionId)}");
        }

        Console.WriteLine();
        Console.Write($"[n]下一页  [p]上一页  [q]返回  (第{currentPage}/{totalPages}页): ");
        var key = Console.ReadLine()?.Trim().ToLower();
        if (key == "n" && currentPage < totalPages) currentPage++;
        else if (key == "p" && currentPage > 1) currentPage--;
        else if (key == "q" || key == "") break;
    }
}

// ═══════════════════════════════════════════
//  2. 持仓汇总 (链路视图, 分页)
// ═══════════════════════════════════════════
void ShowPositionSummary()
{
    if (cachedTrades.Count == 0)
    {
        Console.WriteLine("\n没有交易记录。\n");
        return;
    }

    var groups = cachedTrades
        .Where(t => !string.IsNullOrWhiteSpace(t.ConditionId))
        .GroupBy(t => NormalizeConditionId(t.ConditionId)!)
        .ToList();

    // Pre-compute grand totals across all groups
    decimal grandTotalCost = 0, grandTotalPayout = 0, grandTotalSold = 0;
    foreach (var g in groups)
    {
        var trades = g.ToList();
        var buyTrades = trades.Where(t => t.Side == "Buy").ToList();
        var sellTrades = trades.Where(t => t.Side == "Sell").ToList();
        var clobCost = buyTrades.Sum(t => t.Price * t.Quantity);
        var buyTxHashes = buyTrades.Select(t => t.TransactionHash).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct().ToList();
        var onChainCost = buyTxHashes.Sum(h => cachedBuyCosts.TryGetValue(h, out var c) ? c : 0m);
        var totalCost = onChainCost > 0 ? onChainCost : clobCost;
        var totalSold = sellTrades.Sum(t => t.Price * t.Quantity);
        grandTotalCost += totalCost;
        grandTotalSold += totalSold;
        cachedMarkets.TryGetValue(g.Key, out var mkt);
        if (mkt != null && mkt.Closed)
        {
            var hasRcv = cachedReceiveAmounts.TryGetValue(g.Key, out var rcv);
            grandTotalPayout += hasRcv ? rcv : GetPayout(mkt, trades);
        }
    }

    const int pageSize = 5;
    int totalPages = (groups.Count + pageSize - 1) / pageSize;
    int currentPage = 1;

    while (true)
    {
        Console.Clear();
        Console.WriteLine($"\n共 {groups.Count} 个市场  |  第 {currentPage}/{totalPages} 页\n");

        var pageGroups = groups.Skip((currentPage - 1) * pageSize).Take(pageSize).ToList();
        foreach (var g in pageGroups)
        {
            var globalIdx = groups.IndexOf(g) + 1;
            var trades = g.ToList();
            var buyTrades = trades.Where(t => t.Side == "Buy").ToList();
            var sellTrades = trades.Where(t => t.Side == "Sell").ToList();
            var clobCost = buyTrades.Sum(t => t.Price * t.Quantity);
            var clobSold = sellTrades.Sum(t => t.Price * t.Quantity);

            var buyTxHashes = buyTrades.Select(t => t.TransactionHash).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct().ToList();
            var onChainCost = buyTxHashes.Sum(h => cachedBuyCosts.TryGetValue(h, out var c) ? c : 0m);
            var totalCost = onChainCost > 0 ? onChainCost : clobCost;
            var totalSold = clobSold;
            var totalBuyQty = buyTrades.Sum(t => t.Quantity);
            var totalSellQty = sellTrades.Sum(t => t.Quantity);
            var costSource = onChainCost > 0 ? "链上" : "CLOB";

            cachedMarkets.TryGetValue(g.Key, out var market);
            var (settlement, settlementColor) = GetSettlement(market, trades.First().Outcome, trades.First().Side);
            var question = market?.Question ?? "(未知市场)";
            if (question.Length > 70) question = question[..67] + "...";

            cachedRedeems.TryGetValue(g.Key, out var redeemInfoEarly);
            var hasReceiveEarly = cachedReceiveAmounts.TryGetValue(g.Key, out var receiveEarly);
            if (market != null && market.Closed && hasReceiveEarly && redeemInfoEarly != null && redeemInfoEarly.TransactionIds.Count > 0)
            {
                if (receiveEarly > 0 && !settlement.Contains("赢"))
                {
                    settlement = "✓ 赢";
                    settlementColor = ConsoleColor.Green;
                }
                else if (receiveEarly == 0 && settlement.Contains("赢"))
                {
                    settlement = "✗ 输";
                    settlementColor = ConsoleColor.Red;
                }
            }

            // ── 标题行 ──
            Console.ForegroundColor = settlementColor;
            Console.Write($"[{globalIdx}] ");
            Console.ResetColor();
            Console.WriteLine(question);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"    ConditionId: {g.Key}");
            Console.ResetColor();

            // ── 买入链路 ──
            Console.Write("    ┌─ ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("买入");
            Console.ResetColor();
            Console.Write($": {totalCost:F4} USDC ({costSource})");
            if (buyTrades.Count > 0)
            {
                var firstBuy = buyTrades.OrderBy(t => t.MatchTime).First();
                var lastBuy = buyTrades.OrderByDescending(t => t.MatchTime).First();
                Console.Write(buyTrades.Count == 1
                    ? $"  {firstBuy.MatchTime:MM-dd HH:mm}"
                    : $"  {firstBuy.MatchTime:MM-dd HH:mm}~{lastBuy.MatchTime:MM-dd HH:mm}");
            }
            Console.WriteLine();
            foreach (var txh in buyTxHashes)
                Console.WriteLine($"    │  Tx: {Shorten(txh)}");

            // ── 卖出链路 ──
            if (sellTrades.Count > 0)
            {
                var sellTxHashes = sellTrades.Select(t => t.TransactionHash).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct().ToList();
                Console.Write("    ├─ ");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write("卖出");
                Console.ResetColor();
                Console.WriteLine($": {totalSold:F4} USDC ({sellTrades.Count}笔, {totalSellQty:F2}份)");
                foreach (var txh in sellTxHashes)
                    Console.WriteLine($"    │  Tx: {Shorten(txh)}");
            }

            // ── 赎回 + 到账 ──
            var tokenIds = trades.Select(t => t.TokenId).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
            var totalOnChainBalance = tokenIds.Sum(tid => cachedBalances.TryGetValue(tid, out var bal) ? bal : 0m);
            cachedRedeems.TryGetValue(g.Key, out var redeemInfo);
            var hasReceiveAmount = cachedReceiveAmounts.TryGetValue(g.Key, out var receiveAmount);

            if (market != null && market.Closed)
            {
                var payout = GetPayout(market, trades);

                if (redeemInfo != null && redeemInfo.TransactionIds.Count > 0)
                {
                    Console.Write("    ├─ ");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write("赎回");
                    Console.ResetColor();
                    Console.WriteLine($": ({redeemInfo.Source}, {redeemInfo.TransactionIds.Count}笔)");
                    foreach (var rtx in redeemInfo.TransactionIds)
                        Console.WriteLine($"    │  Tx: {Shorten(rtx)}");
                }
                else if (totalOnChainBalance > 0)
                {
                    Console.Write("    ├─ ");
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write("赎回");
                    Console.ResetColor();
                    Console.WriteLine($": 待赎回 (链上余额: {totalOnChainBalance:F2})");
                }
                else
                {
                    Console.Write("    ├─ ");
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("赎回");
                    Console.ResetColor();
                    Console.WriteLine(": 已赎回");
                }

                if (hasReceiveAmount)
                {
                    Console.Write("    └─ ");
                    Console.ForegroundColor = receiveAmount > 0 ? ConsoleColor.Green : ConsoleColor.DarkGray;
                    Console.Write("到账");
                    Console.ResetColor();
                    Console.WriteLine($": {receiveAmount:F4} USDC  (链上实际)");
                }
                else if (totalOnChainBalance > 0)
                {
                    Console.Write("    └─ ");
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("到账");
                    Console.ResetColor();
                    Console.WriteLine($": 待赎回后到账 (预计 {payout:F4} USDC)");
                }
                else
                {
                    Console.Write("    └─ ");
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.Write("到账");
                    Console.ResetColor();
                    Console.WriteLine($": {payout:F4} USDC  (理论赔付)");
                }

                var actualPayout = hasReceiveAmount ? receiveAmount : payout;
                var pnl = actualPayout + totalSold - totalCost;
                Console.Write("    ");
                Console.ForegroundColor = settlementColor;
                Console.Write($"结算: {settlement}");
                Console.ResetColor();
                Console.Write("  |  盈亏: ");
                Console.ForegroundColor = pnl >= 0 ? ConsoleColor.Green : ConsoleColor.Red;
                Console.Write($"{pnl:+0.0000;-0.0000;0} USDC");
                Console.ResetColor();
                Console.WriteLine();
            }
            else
            {
                Console.Write("    └─ ");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write("状态");
                Console.ResetColor();
                Console.Write($": {settlement}");
                if (totalOnChainBalance > 0)
                    Console.Write($"  |  链上持仓: {totalOnChainBalance:F2}");
                Console.WriteLine();
            }
            Console.WriteLine();
        }

        // ── 汇总行 (始终显示全局汇总) ──
        Console.WriteLine(new string('═', 60));
        var grandPnl = grandTotalPayout + grandTotalSold - grandTotalCost;
        Console.Write($"  总买入: {grandTotalCost:F4} USDC");
        if (grandTotalSold > 0)
            Console.Write($"  |  总卖出: {grandTotalSold:F4} USDC");
        Console.Write($"  |  总到账: {grandTotalPayout:F4} USDC");
        Console.Write("  |  总盈亏: ");
        Console.ForegroundColor = grandPnl >= 0 ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write($"{grandPnl:+0.0000;-0.0000;0} USDC");
        Console.ResetColor();
        Console.WriteLine();

        Console.WriteLine();
        Console.Write($"[n]下一页  [p]上一页  [q]返回  (第{currentPage}/{totalPages}页): ");
        var key = Console.ReadLine()?.Trim().ToLower();
        if (key == "n" && currentPage < totalPages) currentPage++;
        else if (key == "p" && currentPage > 1) currentPage--;
        else if (key == "q" || key == "") break;
    }
}

// ═══════════════════════════════════════════
//  3. 赎回指定 conditionId
// ═══════════════════════════════════════════
async Task RedeemSpecific()
{
    Console.Write("\n请输入 conditionId: ");
    var conditionId = NormalizeConditionId(Console.ReadLine()?.Trim());
    if (string.IsNullOrWhiteSpace(conditionId))
    {
        Console.WriteLine("取消。\n");
        return;
    }

    // Check market status first
    var markets = await FetchMarketsByConditionIds(new[] { conditionId });
    if (markets.TryGetValue(conditionId, out var mkt))
    {
        if (!mkt.Closed)
        {
            Console.WriteLine("⚠ 该市场尚未结算，无法赎回。\n");
            return;
        }
        Console.WriteLine($"市场: {mkt.Question}");
        Console.WriteLine($"状态: 已结算 (Closed)");
    }

    Console.WriteLine("正在尝试赎回...");
    var result = await RedeemPositions(conditionId, isNegRisk: false);
    if (!result.success)
    {
        Console.WriteLine($"CTF 赎回失败: {result.message}，尝试 NegRisk adapter...");
        result = await RedeemPositions(conditionId, isNegRisk: true);
    }

    if (result.success)
    {
        Console.WriteLine($"✓ 赎回成功! TxHash: {result.txHash}");
    }
    else
        Console.WriteLine($"✗ 赎回失败: {result.message}");
    Console.WriteLine();
}

// ═══════════════════════════════════════════
//  4. 赎回全部可赎回
// ═══════════════════════════════════════════
async Task RedeemAll()
{
    var conditionIds = cachedTrades
        .Select(t => NormalizeConditionId(t.ConditionId))
        .Where(c => !string.IsNullOrWhiteSpace(c))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Cast<string>()
        .ToArray();

    if (conditionIds.Length == 0)
    {
        Console.WriteLine("\n没有交易记录。\n");
        return;
    }

    var redeemable = conditionIds.Where(cid =>
        cachedMarkets.TryGetValue(cid, out var m) && m.Closed).ToList();

    if (redeemable.Count == 0)
    {
        Console.WriteLine("没有已结算可赎回的市场。\n");
        return;
    }

    Console.WriteLine($"共 {redeemable.Count} 个已结算市场，开始赎回...\n");
    int ok = 0, fail = 0;
    foreach (var cid in redeemable)
    {
        var question = cachedMarkets.TryGetValue(cid, out var m) ? m.Question : cid;
        if (question.Length > 50) question = question[..47] + "...";
        Console.Write($"  赎回 [{question}] ... ");

        var result = await RedeemPositions(cid, isNegRisk: false);
        if (!result.success)
            result = await RedeemPositions(cid, isNegRisk: true);

        if (result.success)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ tx={result.txHash}");
            Console.ResetColor();
            ok++;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"✗ {result.message}");
            Console.ResetColor();
            fail++;
        }
    }
    Console.WriteLine($"\n赎回完成: {ok} 成功, {fail} 失败\n");
}

// ═══════════════════════════════════════════
//  9. 清除 pending 交易 (发送相同 nonce 的 0 值自转账覆盖)
// ═══════════════════════════════════════════
async Task CancelPendingTransactions()
{
    Console.WriteLine("\n=== 清除 Pending 交易 ===");
    try
    {
        var web3 = CreateSignedWeb3();

        // 获取 pending nonce 和 confirmed nonce
        var pendingNonce = await web3.Eth.Transactions.GetTransactionCount.SendRequestAsync(
            walletAddress, Nethereum.RPC.Eth.DTOs.BlockParameter.CreatePending());
        var confirmedNonce = await web3.Eth.Transactions.GetTransactionCount.SendRequestAsync(
            walletAddress, Nethereum.RPC.Eth.DTOs.BlockParameter.CreateLatest());

        var pendingCount = (int)(pendingNonce.Value - confirmedNonce.Value);
        Console.WriteLine($"  钱包地址: {walletAddress}");
        Console.WriteLine($"  Confirmed nonce: {confirmedNonce.Value}");
        Console.WriteLine($"  Pending nonce:   {pendingNonce.Value}");
        Console.WriteLine($"  Pending 交易数:  {pendingCount}");

        if (pendingCount <= 0)
        {
            Console.WriteLine("  没有 pending 交易。\n");
            return;
        }

        Console.Write($"\n确认要取消 {pendingCount} 笔 pending 交易? (y/N): ");
        var confirm = Console.ReadLine()?.Trim().ToLower();
        if (confirm != "y")
        {
            Console.WriteLine("  取消。\n");
            return;
        }

        // 获取当前 gas price 和 base fee
        var baseGasPrice = await web3.Eth.GasPrice.SendRequestAsync();
        var defaultGwei = (decimal)baseGasPrice.Value / 1_000_000_000m;
        Console.WriteLine($"  当前网络 gas price: {defaultGwei:F2} Gwei");

        // 方式选择
        Console.WriteLine("  替换方式:");
        Console.WriteLine("    1. Legacy (GasPrice, 默认 10x)");
        Console.WriteLine("    2. EIP-1559 (MaxFeePerGas + MaxPriorityFeePerGas)");
        Console.Write("  选择 (1/2, 默认 1): ");
        var modeInput = Console.ReadLine()?.Trim();
        bool useEip1559 = modeInput == "2";

        Console.Write($"  输入 gas 倍数 (默认 10): ");
        var multInput = Console.ReadLine()?.Trim();
        decimal multiplier = string.IsNullOrEmpty(multInput) ? 10 : decimal.Parse(multInput);
        var boostWei = (BigInteger)((decimal)baseGasPrice.Value * multiplier);
        Console.WriteLine($"  使用 {multiplier}x = {(decimal)boostWei / 1_000_000_000m:F2} Gwei ({boostWei} wei)");
        Console.WriteLine($"  模式: {(useEip1559 ? "EIP-1559" : "Legacy")}");

        int ok = 0, fail = 0;
        for (var nonce = confirmedNonce.Value; nonce < pendingNonce.Value; nonce++)
        {
            try
            {
                Console.Write($"  取消 nonce={nonce} ... ");
                string txHash;
                if (useEip1559)
                {
                    var tx1559 = new Nethereum.RPC.Eth.DTOs.TransactionInput
                    {
                        From = walletAddress,
                        To = walletAddress,
                        Value = new HexBigInteger(0),
                        Nonce = new HexBigInteger(nonce),
                        Gas = new HexBigInteger(21000),
                        Type = new HexBigInteger(2),
                        MaxFeePerGas = new HexBigInteger(boostWei),
                        MaxPriorityFeePerGas = new HexBigInteger(boostWei),
                    };
                    txHash = await web3.Eth.TransactionManager.SendTransactionAsync(tx1559);
                }
                else
                {
                    txHash = await web3.Eth.TransactionManager.SendTransactionAsync(
                        new TransactionInput
                        {
                            From = walletAddress,
                            To = walletAddress,
                            Value = new HexBigInteger(0),
                            Nonce = new HexBigInteger(nonce),
                            Gas = new HexBigInteger(21000),
                            GasPrice = new HexBigInteger(boostWei),
                        });
                }
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✓ tx={txHash}");
                Console.ResetColor();
                ok++;

                // 等待确认
                Console.Write("    等待确认...");
                var receipt = await web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
                int waitCount = 0;
                while (receipt == null && waitCount < 30)
                {
                    await Task.Delay(2000);
                    receipt = await web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
                    waitCount++;
                }
                if (receipt != null)
                    Console.WriteLine($" 已确认 (block={receipt.BlockNumber.Value})");
                else
                    Console.WriteLine(" 超时，继续下一笔...");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"✗ {ex.Message}");
                Console.ResetColor();
                fail++;
            }
        }
        Console.WriteLine($"\n清除完成: {ok} 成功, {fail} 失败\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  错误: {ex.Message}\n");
    }
}

// ═══════════════════════════════════════════
//  5. 查看余额
// ═══════════════════════════════════════════
async Task ShowBalance()
{
    Console.WriteLine("\n=== 余额信息 ===");
    var balResp = await client.ClobApi.Account.GetBalanceAllowanceAsync(AssetType.Collateral);
    if (balResp.Success && balResp.Data != null)
    {
        foreach (var prop in balResp.Data.GetType().GetProperties())
            Console.WriteLine($"  {prop.Name} = {prop.GetValue(balResp.Data)}");
    }
    else
        Console.WriteLine($"  查询失败: {balResp.Error?.Message}");
    Console.WriteLine();
}

// ═══════════════════════════════════════════
//  Helper: fetch trades incrementally (only new ones after lastTime)
//  Always fetches at least minPages full pages to avoid missing trades
//  that the API returns in non-strict chronological order.
// ═══════════════════════════════════════════
async Task<List<PolymarketTrade>> FetchTradesIncremental(DateTime? lastTime)
{
    var all = new List<PolymarketTrade>();
    string? cursor = null;
    int page = 0;
    const int minPages = 2; // always fetch at least this many pages for safety
    bool reachedOld = false;
    while (true)
    {
        page++;
        var resp = await client.ClobApi.Trading.GetUserTradesAsync(cursor: cursor);
        if (!resp.Success || resp.Data?.Data == null)
        {
            if (page == 1)
                Console.WriteLine($"拉取交易失败: {resp.Error?.Message}");
            break;
        }

        all.AddRange(resp.Data.Data);

        // After minPages, check if we've gone past cached range
        if (lastTime.HasValue && page >= minPages)
        {
            var oldest = resp.Data.Data.LastOrDefault();
            if (oldest != null && oldest.MatchTime < lastTime.Value)
            {
                reachedOld = true;
                break;
            }
        }

        var next = resp.Data.NextPageCursor;
        if (string.IsNullOrEmpty(next) || next == "END")
            break;
        cursor = next;
    }
    return all;
}

// ═══════════════════════════════════════════
//  8. 缓存统计
// ═══════════════════════════════════════════
void ShowCacheStats()
{
    var (trades, markets, balances, redeems, usdcIn, usdcOut) = db.GetStats();
    var fi = new FileInfo(cacheDbPath);
    Console.WriteLine();
    Console.WriteLine("╔══════════════════════════════════════╗");
    Console.WriteLine("║          缓存统计信息                ║");
    Console.WriteLine("╠══════════════════════════════════════╣");
    Console.WriteLine($"║  缓存文件: {fi.Name,-25} ║");
    Console.WriteLine($"║  文件大小: {(fi.Exists ? $"{fi.Length / 1024.0:F1} KB" : "不存在"),-25} ║");
    Console.WriteLine($"║  交易记录: {trades,-25} ║");
    Console.WriteLine($"║  市场数据: {markets,-25} ║");
    Console.WriteLine($"║  持仓余额: {balances,-25} ║");
    Console.WriteLine($"║  赎回记录: {redeems,-25} ║");
    Console.WriteLine($"║  USDC转入: {usdcIn,-25} ║");
    Console.WriteLine($"║  USDC转出: {usdcOut,-25} ║");
    Console.WriteLine("╚══════════════════════════════════════╝");
    Console.WriteLine();
}

// ═══════════════════════════════════════════
//  Helper: fetch on-chain token balances (ERC1155 balanceOf)
// ═══════════════════════════════════════════
async Task<Dictionary<string, decimal>> FetchTokenBalances(string[] tokenIds)
{
    var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    try
    {
        var web3 = CreateReadOnlyWeb3();
        var contract = web3.Eth.GetContract(BALANCE_OF_ABI, CTF_ADDRESS);
        var balanceOfFunc = contract.GetFunction("balanceOf");

        foreach (var tokenId in tokenIds)
        {
            try
            {
                var tokenIdBig = BigInteger.Parse(tokenId, CultureInfo.InvariantCulture);
                var balance = await balanceOfFunc.CallAsync<BigInteger>(walletAddress, tokenIdBig);
                // CTF tokens have 6 decimals (same as USDC)
                var balDecimal = (decimal)balance / 1_000_000m;
                map[tokenId] = balDecimal;
            }
            catch
            {
                map[tokenId] = 0;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  链上余额查询失败: {ex.Message}");
    }
    return map;
}

// ═══════════════════════════════════════════
//  Helper: fetch markets by conditionIds via Gamma API
// ═══════════════════════════════════════════
async Task<Dictionary<string, CachedMarket>> FetchMarketsByConditionIds(string[] conditionIds)
{
    var map = new Dictionary<string, CachedMarket>(StringComparer.OrdinalIgnoreCase);
    // Gamma API has URL length limits, use small chunks
    foreach (var chunk in conditionIds.Chunk(5))
    {
        var resp = await client.GammaApi.GetMarketsAsync(conditionIds: chunk);
        if (resp.Success && resp.Data != null)
        {
            foreach (var m in resp.Data)
            {
                var normalized = NormalizeConditionId(m.ConditionId);
                if (normalized != null)
                    map[normalized] = new CachedMarket
                    {
                        ConditionId = normalized,
                        Question = m.Question,
                        Closed = m.Closed,
                        OutcomePrices = m.OutcomePrices,
                        Outcomes = m.Outcomes,
                    };
            }
        }
    }
    // Fallback: query individually for any missing conditionIds
    var missing = conditionIds.Where(c => !map.ContainsKey(c)).ToArray();
    foreach (var cid in missing)
    {
        var resp = await client.GammaApi.GetMarketsAsync(conditionIds: new[] { cid });
        if (resp.Success && resp.Data != null)
        {
            foreach (var m in resp.Data)
            {
                var normalized = NormalizeConditionId(m.ConditionId);
                if (normalized != null)
                    map[normalized] = new CachedMarket
                    {
                        ConditionId = normalized,
                        Question = m.Question,
                        Closed = m.Closed,
                        OutcomePrices = m.OutcomePrices,
                        Outcomes = m.Outcomes,
                    };
            }
        }
    }
    return map;
}

// ═══════════════════════════════════════════
//  Helper: determine settlement status
// ═══════════════════════════════════════════
(string text, ConsoleColor color) GetSettlement(CachedMarket? market, string tradeOutcome, string tradeSide)
{
    if (market == null)
        return ("未知", ConsoleColor.Gray);

    if (!market.Closed)
        return ("⏳ 未结算", ConsoleColor.Yellow);

    if (market.OutcomePrices == null || market.OutcomePrices.Length == 0)
        return ("已关闭(无价格数据)", ConsoleColor.Gray);

    int outcomeIdx = FindOutcomeIndex(market, tradeOutcome);
    if (outcomeIdx >= 0 && outcomeIdx < market.OutcomePrices.Length)
    {
        var price = market.OutcomePrices[outcomeIdx];
        // Buy 方向: 结算价=1 赢, =0 输; Sell 方向相反
        bool isBuy = tradeSide.Equals("Buy", StringComparison.OrdinalIgnoreCase);
        bool isWin = isBuy ? price >= 0.99m : price <= 0.01m;
        bool isLoss = isBuy ? price <= 0.01m : price >= 0.99m;
        if (isWin)
            return ("✓ 赢", ConsoleColor.Green);
        else if (isLoss)
            return ("✗ 输", ConsoleColor.Red);
    }

    return ("已关闭(待确认)", ConsoleColor.DarkYellow);
}

// ═══════════════════════════════════════════
//  Helper: calculate payout for closed market
// ═══════════════════════════════════════════
decimal GetPayout(CachedMarket market, List<CachedTrade> trades)
{
    if (market.OutcomePrices == null || market.OutcomePrices.Length == 0)
        return 0;

    decimal payout = 0;
    foreach (var t in trades)
    {
        int idx = FindOutcomeIndex(market, t.Outcome);
        if (idx < 0 || idx >= market.OutcomePrices.Length) continue;

        var settlementPrice = market.OutcomePrices[idx];
        var qty = t.Side.Equals("Buy", StringComparison.OrdinalIgnoreCase) ? t.Quantity : -t.Quantity;
        payout += qty * settlementPrice;
    }
    return payout;
}

// ═══════════════════════════════════════════
//  Helper: find outcome index by matching trade outcome to market outcomes
// ═══════════════════════════════════════════
int FindOutcomeIndex(CachedMarket market, string tradeOutcome)
{
    // Trade outcome 可能是 "Yes"/"No" 或 "Up"/"Down" 等
    // 先尝试在 market.Outcomes 数组中精确匹配
    if (market.Outcomes != null)
    {
        for (int i = 0; i < market.Outcomes.Length; i++)
        {
            if (market.Outcomes[i].Equals(tradeOutcome, StringComparison.OrdinalIgnoreCase))
                return i;
        }
    }
    // 回退: Yes/No 映射到 index 0/1
    if (tradeOutcome.Equals("Yes", StringComparison.OrdinalIgnoreCase)) return 0;
    if (tradeOutcome.Equals("No", StringComparison.OrdinalIgnoreCase)) return 1;
    return -1;
}

// ═══════════════════════════════════════════
//  Helper: compute winning index sets based on outcome prices and on-chain balances
// ═══════════════════════════════════════════
BigInteger[] GetWinningIndexSets(string conditionId)
{
    if (!cachedMarkets.TryGetValue(conditionId, out var market))
        return new BigInteger[] { 1, 2 }; // fallback: try both sides

    if (market.OutcomePrices == null || market.OutcomePrices.Length == 0)
        return new BigInteger[] { 1, 2 };

    // Find tokenIds for each outcome from trades
    var tradesByOutcomeIdx = cachedTrades
        .Where(t => NormalizeConditionId(t.ConditionId) == conditionId && !string.IsNullOrWhiteSpace(t.TokenId))
        .GroupBy(t => FindOutcomeIndex(market, t.Outcome))
        .ToDictionary(g => g.Key, g => g.Select(t => t.TokenId!).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

    var winningIndexSets = new List<BigInteger>();
    for (int i = 0; i < market.OutcomePrices.Length; i++)
    {
        // outcome price > 0 means this side won
        if (market.OutcomePrices[i] <= 0) continue;

        // check if user holds tokens for this outcome
        if (tradesByOutcomeIdx.TryGetValue(i, out var tokenIds))
        {
            var hasBalance = tokenIds.Any(tid => cachedBalances.TryGetValue(tid, out var bal) && bal > 0);
            if (hasBalance)
                winningIndexSets.Add(new BigInteger(1 << i));
        }
    }

    return winningIndexSets.Count > 0 ? winningIndexSets.ToArray() : new BigInteger[] { 1, 2 };
}

// ═══════════════════════════════════════════
//  Helper: redeem via direct on-chain call (EOA → CTF/NegRisk)
// ═══════════════════════════════════════════
async Task<(bool success, string? txHash, string source, string? message)> RedeemPositions(string conditionId, bool isNegRisk)
{
    var sourceName = isNegRisk ? "NegRisk" : "CTF";
    try
    {
        var contractAddress = isNegRisk ? NEG_RISK_ADAPTER : CTF_ADDRESS;
        var normalizedCid = NormalizeConditionId(conditionId) ?? conditionId;
        var conditionIdHex = normalizedCid[2..];
        var conditionIdBytes = Convert.FromHexString(conditionIdHex);

        // Step 1: Check if condition is resolved on-chain
        var preCheckError = await PreCheckConditionResolved(conditionIdHex);
        if (preCheckError != null)
            return (false, null, sourceName, preCheckError);
        Console.Write("[已解析] ");

        // Step 2: Send direct on-chain transaction from EOA
        var web3 = CreateSignedWeb3();
        var contract = web3.Eth.GetContract(REDEEM_ABI, contractAddress);
        var redeemFunc = contract.GetFunction("redeemPositions");

        var parentCollectionId = new byte[32];
        var indexSets = GetWinningIndexSets(normalizedCid);
        Console.Write($"[indexSets={string.Join(",", indexSets)}] ");

        var txHash = await redeemFunc.SendTransactionAsync(
            walletAddress,
            new Nethereum.Hex.HexTypes.HexBigInteger(300000), // gas limit
            null, // value
            USDC_E_ADDRESS,
            parentCollectionId,
            conditionIdBytes,
            indexSets);

        Console.Write($"[tx={txHash[..10]}...] ");

        // Step 3: Wait for receipt
        var receipt = await web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
        int waitCount = 0;
        while (receipt == null && waitCount < 20)
        {
            await Task.Delay(2000);
            receipt = await web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
            waitCount++;
        }

        if (receipt == null)
            return (false, txHash, sourceName, "已提交但未确认(超时)");

        if (receipt.Status.Value == 1)
            return (true, txHash, sourceName, null);
        else
            return (false, txHash, sourceName, "交易链上 revert");
    }
    catch (Exception ex)
    {
        return (false, null, sourceName, ex.Message);
    }
}

// Pre-check: query CTF.payoutDenominator(conditionId) to verify condition is resolved on-chain.
// If payoutDenominator == 0, oracle hasn't reported results yet → skip redemption.
// Uses DIRECT connection (no proxy) since publicnode.com is directly accessible.
async Task<string?> PreCheckConditionResolved(string conditionIdHex)
{
    using var httpClient = new System.Net.Http.HttpClient();
    httpClient.Timeout = TimeSpan.FromSeconds(10);

    // payoutDenominator(bytes32) selector = 0xdd34de67
    var calldata = "0xdd34de67" + conditionIdHex.ToLowerInvariant();
    var rpcBody = Newtonsoft.Json.JsonConvert.SerializeObject(new
    {
        jsonrpc = "2.0",
        method = "eth_call",
        @params = new object[] {
            new { to = CTF_ADDRESS, data = calldata, gas = "0x30D40" },
            "latest"
        },
        id = 1
    });

    for (int attempt = 0; attempt < 2; attempt++)
    {
        try
        {
            var resp = await httpClient.PostAsync(POLYGON_RPC,
                new System.Net.Http.StringContent(rpcBody, Encoding.UTF8, "application/json"));
            var json = await resp.Content.ReadAsStringAsync();
            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);

            if (obj["error"] != null)
                return "预检RPC错误";

            var result = obj["result"]?.ToString() ?? "0x";
            Console.Write($"[pD={result[^6..]}] ");
            // payoutDenominator returns uint256; if 0 → condition not resolved
            var cleaned = result.StartsWith("0x") ? result[2..] : result;
            cleaned = cleaned.TrimStart('0');
            if (string.IsNullOrEmpty(cleaned) || cleaned == "0")
                return "oracle 尚未报告结果(payoutDenominator=0)";

            return null; // condition is resolved, can redeem
        }
        catch
        {
            if (attempt == 0) { await Task.Delay(1000); continue; }
            return "预检网络错误(跳过以保护nonce)";
        }
    }
    return "预检网络错误(跳过以保护nonce)";
}

// ═══════════════════════════════════════════
//  Helper: shorten hex string for display
// ═══════════════════════════════════════════
Web3 CreateSignedWeb3(string? rpcUrl = null)
{
    var handler = new System.Net.Http.HttpClientHandler
    {
        Proxy = new System.Net.WebProxy(PROXY_URL),
        UseProxy = true
    };
    var httpClient = new System.Net.Http.HttpClient(handler);
    var rpcClient = new RpcClient(new Uri(rpcUrl ?? POLYGON_RPC), httpClient);
    return new Web3(account, rpcClient);
}

Web3 CreateReadOnlyWeb3()
{
    var handler = new System.Net.Http.HttpClientHandler
    {
        Proxy = new System.Net.WebProxy(PROXY_URL),
        UseProxy = true
    };
    var httpClient = new System.Net.Http.HttpClient(handler);
    var rpcClient = new RpcClient(new Uri(POLYGON_RPC), httpClient);
    return new Web3(rpcClient);
}

string? NormalizeConditionId(string? conditionId)
{
    if (string.IsNullOrWhiteSpace(conditionId))
        return null;

    var value = conditionId.Trim();
    if (!value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        value = "0x" + value;

    return value.ToLowerInvariant();
}

string Shorten(string? hex)
{
    if (string.IsNullOrEmpty(hex)) return "";
    return hex.Length > 16 ? hex[..8] + "..." + hex[^6..] : hex;
}

// ═══════════════════════════════════════════
//  Helper: fetch redeem txs via Etherscan V2 API
// ═══════════════════════════════════════════
async Task<Dictionary<string, CachedRedeem>> FetchRedeemTransactions(string[] conditionIds)
{
    var result = new Dictionary<string, CachedRedeem>(StringComparer.OrdinalIgnoreCase);
    if (conditionIds.Length == 0) return result;

    var wantedSet = new HashSet<string>(conditionIds, StringComparer.OrdinalIgnoreCase);
    const string redeemMethodId = "01b7037c"; // redeemPositions selector (actual on-chain)

    var handler = new System.Net.Http.HttpClientHandler
    {
        Proxy = new System.Net.WebProxy(PROXY_URL),
        UseProxy = true
    };
    using var httpClient = new System.Net.Http.HttpClient(handler);
    httpClient.Timeout = TimeSpan.FromSeconds(30);

    try
    {
        foreach (var contractAddr in new[] { CTF_ADDRESS, NEG_RISK_ADAPTER })
        {
            var contractName = string.Equals(contractAddr, NEG_RISK_ADAPTER, StringComparison.OrdinalIgnoreCase) ? "NegRisk" : "CTF";
            int redeemCount = 0;
            int page = 1;
            const int pageSize = 1000;

            while (true)
            {
                var url = $"https://api.etherscan.io/v2/api?chainid=137&module=account&action=txlist&address={walletAddress}&sort=desc&page={page}&offset={pageSize}&apikey={ETHERSCAN_API_KEY}";
                var json = await httpClient.GetStringAsync(url);
                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);

                if (obj["status"]?.ToString() != "1")
                {
                    if (page == 1)
                        Console.WriteLine($"  Etherscan {contractName}: {obj["message"]}");
                    break;
                }

                var txs = obj["result"] as Newtonsoft.Json.Linq.JArray;
                if (txs == null || txs.Count == 0) break;

                foreach (var tx in txs)
                {
                    var to = tx["to"]?.ToString();
                    if (!string.Equals(to, contractAddr, StringComparison.OrdinalIgnoreCase)) continue;

                    var input = tx["input"]?.ToString();
                    if (string.IsNullOrWhiteSpace(input)) continue;
                    var inputHex = input.StartsWith("0x") ? input[2..] : input;
                    if (!inputHex.StartsWith(redeemMethodId, StringComparison.OrdinalIgnoreCase)) continue;

                    var isError = tx["isError"]?.ToString();
                    if (isError == "1") continue;

                    redeemCount++;

                    // Extract conditionId from redeemPositions input:
                    // selector(4) + collateralToken(32) + parentCollectionId(32) + conditionId(32) + ...
                    if (inputHex.Length < 8 + 64 * 3) continue;
                    var conditionIdHex = "0x" + inputHex.Substring(8 + 64 * 2, 64).ToLowerInvariant();
                    if (!wantedSet.Contains(conditionIdHex)) continue;

                    var txHash = tx["hash"]?.ToString();
                    if (string.IsNullOrWhiteSpace(txHash)) continue;

                    if (!result.TryGetValue(conditionIdHex, out var existing))
                    {
                        existing = new CachedRedeem
                        {
                            ConditionId = conditionIdHex,
                            Source = contractName
                        };
                        result[conditionIdHex] = existing;
                    }
                    if (!existing.TransactionIds.Contains(txHash, StringComparer.OrdinalIgnoreCase))
                        existing.TransactionIds.Add(txHash);
                }

                if (txs.Count < pageSize) break;
                page++;
                await Task.Delay(250); // rate limit
            }

            Console.WriteLine($"  {contractName}: 找到 {redeemCount} 笔赎回交易");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  赎回交易查询失败: {ex.Message}");
    }

    return result;
}

// ═══════════════════════════════════════════
//  Helper: batch fetch all USDC transfers TO wallet via Etherscan getLogs API
//  Uses indexed log data (not RPC), so not affected by node pruning
// ═══════════════════════════════════════════
async Task<Dictionary<string, decimal>> FetchUsdcTransfersToWallet()
{
    var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

    const string transferTopic = "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";
    var walletTopic = "0x" + walletAddress[2..].PadLeft(64, '0').ToLowerInvariant();

    var handler = new System.Net.Http.HttpClientHandler
    {
        Proxy = new System.Net.WebProxy(PROXY_URL),
        UseProxy = true
    };
    using var httpClient = new System.Net.Http.HttpClient(handler);
    httpClient.Timeout = TimeSpan.FromSeconds(30);

    try
    {
        int page = 1;
        const int pageSize = 1000;

        while (true)
        {
            // getLogs: USDC contract, Transfer event, topic2(to)=wallet
            var url = $"https://api.etherscan.io/v2/api?chainid=137&module=logs&action=getLogs" +
                      $"&address={USDC_E_ADDRESS}" +
                      $"&topic0={transferTopic}&topic2={walletTopic}&topic0_2_opr=and" +
                      $"&fromBlock=0&toBlock=latest" +
                      $"&page={page}&offset={pageSize}" +
                      $"&apikey={ETHERSCAN_API_KEY}";
            var json = await httpClient.GetStringAsync(url);
            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);

            if (obj["status"]?.ToString() != "1") break;

            var logs = obj["result"] as Newtonsoft.Json.Linq.JArray;
            if (logs == null || logs.Count == 0) break;

            foreach (var log in logs)
            {
                var txHash = log["transactionHash"]?.ToString();
                if (string.IsNullOrWhiteSpace(txHash)) continue;

                var dataHex = log["data"]?.ToString();
                if (string.IsNullOrWhiteSpace(dataHex)) continue;
                var hex = dataHex.StartsWith("0x") ? dataHex[2..] : dataHex;
                if (hex.Length == 0) continue;

                var value = BigInteger.Parse("0" + hex, System.Globalization.NumberStyles.HexNumber);
                var usdc = (decimal)value / 1_000_000m;

                if (result.ContainsKey(txHash))
                    result[txHash] += usdc;
                else
                    result[txHash] = usdc;
            }

            if (logs.Count < pageSize) break;
            page++;
            await Task.Delay(250);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  USDC转入查询失败: {ex.Message}");
    }

    return result;
}

// ═══════════════════════════════════════════
//  Helper: batch fetch all USDC transfers FROM wallet via Etherscan getLogs API
//  Returns txHash -> total USDC spent in that tx
// ═══════════════════════════════════════════
async Task<Dictionary<string, decimal>> FetchUsdcSpentByTx()
{
    var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

    const string transferTopic = "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";
    var walletTopic = "0x" + walletAddress[2..].PadLeft(64, '0').ToLowerInvariant();

    var handler = new System.Net.Http.HttpClientHandler
    {
        Proxy = new System.Net.WebProxy(PROXY_URL),
        UseProxy = true
    };
    using var httpClient = new System.Net.Http.HttpClient(handler);
    httpClient.Timeout = TimeSpan.FromSeconds(30);

    try
    {
        int page = 1;
        const int pageSize = 1000;

        while (true)
        {
            // getLogs: USDC contract, Transfer event, topic1(from)=wallet
            var url = $"https://api.etherscan.io/v2/api?chainid=137&module=logs&action=getLogs" +
                      $"&address={USDC_E_ADDRESS}" +
                      $"&topic0={transferTopic}&topic1={walletTopic}&topic0_1_opr=and" +
                      $"&fromBlock=0&toBlock=latest" +
                      $"&page={page}&offset={pageSize}" +
                      $"&apikey={ETHERSCAN_API_KEY}";
            var json = await httpClient.GetStringAsync(url);
            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);

            if (obj["status"]?.ToString() != "1") break;

            var logs = obj["result"] as Newtonsoft.Json.Linq.JArray;
            if (logs == null || logs.Count == 0) break;

            foreach (var log in logs)
            {
                var txHash = log["transactionHash"]?.ToString();
                if (string.IsNullOrWhiteSpace(txHash)) continue;

                var dataHex = log["data"]?.ToString();
                if (string.IsNullOrWhiteSpace(dataHex)) continue;
                var hex = dataHex.StartsWith("0x") ? dataHex[2..] : dataHex;
                if (hex.Length == 0) continue;

                var value = BigInteger.Parse("0" + hex, System.Globalization.NumberStyles.HexNumber);
                var usdc = (decimal)value / 1_000_000m;

                if (result.ContainsKey(txHash))
                    result[txHash] += usdc;
                else
                    result[txHash] = usdc;
            }

            if (logs.Count < pageSize) break;
            page++;
            await Task.Delay(250);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  USDC转出查询失败: {ex.Message}");
    }

    return result;
}

