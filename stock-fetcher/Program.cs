using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using Microsoft.Playwright; // 這是 Playwright 的引用

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("=== 系統啟動 ===");
        Console.WriteLine($"收到的參數數量: {args.Length}");
        foreach (var arg in args)
        {
            Console.WriteLine($"參數內容: {arg}");
        }
        FirestoreDb db = await InitializeFirebaseAsync();
        if (db == null) return;

        string command = args.Length > 0 ? args[0].ToLower() : "all";

        // 執行爬蟲抓取資料
        if (command == "threads" || command == "all")
            await RunThreadsTask(db);

        // 執行 30 天統計更新 (Materialized View)
        if (args.Contains("aggregate") || (args.Length > 0 && args[0] == "aggregate"))
        {
            await RunAggregationTask(db);
        }

        Console.WriteLine("\n=== 任務執行完畢 ===");
    }

    // ==========================================================
    // 任務 1：抓取 FinMind 歷史 K 線並寫入資料庫 (保持原樣)
    // ==========================================================
    static async Task RunKLineTask(FirestoreDb db)
    {
        Console.WriteLine("\n--- [任務 1] 開始抓取 FinMind K 線資料 ---");
        try
        {
            string stockNo = "2330";
            string startDate = DateTime.Now.AddDays(-30).ToString("yyyy-MM-dd");
            string apiUrl = $"https://api.finmindtrade.com/api/v4/data?dataset=TaiwanStockPrice&data_id={stockNo}&start_date={startDate}";

            using HttpClient client = new HttpClient();
            var response = await client.GetAsync(apiUrl);
            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(responseBody);
            JsonElement dataArray = doc.RootElement.GetProperty("data");

            CollectionReference collectionRef = db.Collection("stocks");
            foreach (JsonElement item in dataArray.EnumerateArray())
            {
                var data = new Dictionary<string, object>
                {
                    { "no", item.GetProperty("stock_id").GetString() },
                    { "update", item.GetProperty("date").GetString() },
                    { "open", item.GetProperty("open").GetDouble() },
                    { "high", item.GetProperty("max").GetDouble() },
                    { "low", item.GetProperty("min").GetDouble() },
                    { "close", item.GetProperty("close").GetDouble() }
                };
                string docId = $"{data["no"]}_{data["update"]}";
                await collectionRef.Document(docId).SetAsync(data, SetOptions.Overwrite);
            }
            Console.WriteLine($"✅ [任務 1 成功] 完成 K 線資料寫入。");
        }
        catch (Exception ex) { Console.WriteLine($"❌ [任務 1 失敗]: {ex.Message}"); }
    }

    // ========================================================
    // 任務 2：Playwright 自建爬蟲 
    // ========================================================

    static async Task RunThreadsTask(FirestoreDb db)
    {
        Console.WriteLine("\n--- [任務 1] 開始執行 Playwright 爬蟲 ---");

        List<string> keywords = new List<string> {
            "獲利","漲停","飆股","散戶","甜甜價","可以買",
            "報牌","上車","不要再買了","閉眼入","今天的散戶","落袋",
            "閉眼買","會漲停","會有驚喜","我的建議","明天的散戶",
            "買在無人問津處","賣在人聲鼎沸時","因為我不缺錢","買在起漲點",
            "賣在高峰處"
        };
        int minStockCount = 2;

        try
        {
            CollectionReference collectionRef = db.Collection("threads_tips");
            var existingDocs = await collectionRef.GetSnapshotAsync();
            HashSet<string> existingUrls = new HashSet<string>(existingDocs.Documents.Select(d => d.GetValue<string>("url")));

            using var playwright = await Playwright.CreateAsync();
            string userDataDir = Path.Combine(Directory.GetCurrentDirectory(), "user-data");
            var context = await playwright.Chromium.LaunchPersistentContextAsync(userDataDir, new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = true,
                Channel = "chrome"
            });

            var page = await context.NewPageAsync();
            var allCollectedPosts = new List<JsonElement>();

            foreach (var kw in keywords)
            {
                Console.WriteLine($"\n--- 正在搜尋關鍵字: {kw} ---");
                string searchUrl = $"https://www.threads.net/search?q={Uri.EscapeDataString(kw)}";
                await page.GotoAsync(searchUrl);
                await page.WaitForSelectorAsync("div[data-pressable-container='true']", new PageWaitForSelectorOptions { Timeout = 15000 });
                await Task.Delay(2000);

                try
                {
                    var latestTab = page.GetByText("最近", new() { Exact = true }).First;
                    await latestTab.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5000 });
                    await latestTab.EvaluateAsync("el => el.click()");
                    await Task.Delay(5000);
                    Console.WriteLine("✅ 已切換至「最近」頁面。");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ 無法切換至最近標籤 (已略過): {ex.Message}");
                }

                for (int i = 0; i < 20; i++)
                {
                    await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");
                    await Task.Delay(5000);
                }

                var posts = await page.EvaluateAsync<JsonElement>(@"() => {
                    const results = [];
                    document.querySelectorAll('div[data-pressable-container=""true""]').forEach(art => {
                        const linkEl = art.querySelector('a[href*=""/post/""]');
                        const timeEl = art.querySelector('time'); 
                        if (linkEl) {
                            results.push({ 
                                post_url: linkEl.href, 
                                text_content: art.innerText || '',
                                time_text: timeEl ? timeEl.innerText : ''
                            });
                        }
                    });
                    return results;
                }");

                foreach (var item in posts.EnumerateArray()) allCollectedPosts.Add(item);
            }

            int count = 0;
            var uniquePosts = allCollectedPosts.GroupBy(p => p.GetProperty("post_url").GetString()).Select(g => g.First());

            foreach (var post in uniquePosts)
            {
                string postUrl = post.GetProperty("post_url").GetString() ?? "";
                string content = post.GetProperty("text_content").GetString() ?? "";
                string timeText = post.GetProperty("time_text").GetString() ?? "";

                if (string.IsNullOrEmpty(postUrl) || existingUrls.Contains(postUrl)) continue;

                bool isWithin24Hours = !string.IsNullOrEmpty(timeText) &&
                                       (timeText.Contains("s") || timeText.Contains("m") || timeText.Contains("h") ||
                                        timeText.Contains("秒") || timeText.Contains("分") || timeText.Contains("時")) &&
                                       !(timeText.Contains("d") || timeText.Contains("w") || timeText.Contains("y") ||
                                         timeText.Contains("天") || timeText.Contains("週") || timeText.Contains("周") || timeText.Contains("年"));

                if (!isWithin24Hours) continue;

                bool hasKeyword = keywords.Any(k => content.Contains(k));
                if (!hasKeyword) continue;

                var matches = Regex.Matches(content, @"\b\d{4}\b");
                var stocks = new HashSet<string>(matches.Select(m => m.Value).Where(s => s != "2026"));

                if (stocks.Count >= minStockCount)
                {
                    await collectionRef.Document(Guid.NewGuid().ToString()).SetAsync(new Dictionary<string, object>
                    {
                        { "author", Regex.Match(postUrl, @"(@[^/]+)").Value },
                        { "content", content },
                        { "url", postUrl },
                        { "mentioned_stocks", stocks.ToList() },
                        { "crawl_time", Timestamp.GetCurrentTimestamp() }
                    });
                    existingUrls.Add(postUrl);
                    count++;
                    Console.WriteLine($"✅ 成功存入新貼文: {postUrl}");
                }
            }

            await context.CloseAsync();
            Console.WriteLine($"✅ [任務 1 完成] 本次共寫入 {count} 篇新貼文。");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ [任務 1 失敗]: {ex.Message}");
        }
    }

    // ==========================================
    // 新增：任務 3 - 30 天數據統整 (Materialized View)
    // ==========================================
    static async Task RunAggregationTask(FirestoreDb db)
    {
        Console.WriteLine("\n--- [任務 2] 開始統整最近 30 天股票數據 ---");

        // 演算法參數
        int PROXIMITY_LIMIT = 25;
        int MAX_DAYS_TO_KEEP = 30;
        int TIME_DECAY_THRESHOLD_DAYS = 3;
        double DECAY_WEIGHT = 0.2;
        int MIN_MENTIONS_TO_SHOW = 2;
        int MIN_MENTIONS_FOR_FOMO = 3;

        string[] hypeWords = { "上車", "買爆", "閉眼買", "會漲停", "會大漲", "噴出", "發財", "買必賺", "可以買" };
        string[] bearWords = { "下車", "快逃", "割韭菜", "出清", "跌停", "先別碰", "不要碰", "不要賣" };

        DateTime nowUtc = DateTime.UtcNow;
        DateTime cutoffDate = nowUtc.AddDays(-MAX_DAYS_TO_KEEP);

        try
        {
            // 1. 取得股票名稱對照表
            var stockNameMap = await FetchStockNamesAsync();

            // 2. 從資料庫撈取 30 天內的貼文
            Console.WriteLine($"正在讀取 {cutoffDate.ToLocalTime():yyyy-MM-dd} 之後的資料...");
            Query query = db.Collection("threads_tips").WhereGreaterThanOrEqualTo("crawl_time", Timestamp.FromDateTime(cutoffDate));
            QuerySnapshot snapshot = await query.GetSnapshotAsync();
            Console.WriteLine($"共讀取到 {snapshot.Documents.Count} 篇貼文，準備開始計算...");

            // 3. 在記憶體中進行加權與分組計算
            var stats = new Dictionary<string, StockStatData>();

            foreach (var doc in snapshot.Documents)
            {
                if (!doc.TryGetValue("mentioned_stocks", out List<string> stocks)) continue;
                string content = doc.ContainsField("content") ? doc.GetValue<string>("content") : "";
                Timestamp crawlTimestamp = doc.GetValue<Timestamp>("crawl_time");
                DateTime postDate = crawlTimestamp.ToDateTime();

                // 轉成台北時間字串，準備給標籤使用
                string formattedPostTime = postDate.AddHours(8).ToString("yyyy/MM/dd HH:mm:ss");

                double daysOld = (nowUtc - postDate).TotalDays;
                double weight = daysOld <= TIME_DECAY_THRESHOLD_DAYS ? 1.0 : DECAY_WEIGHT;

                foreach (var stock in stocks)
                {
                    if (!stats.ContainsKey(stock))
                    {
                        stats[stock] = new StockStatData
                        {
                            FirstTime = postDate,
                            LastTime = postDate,
                            Tags = new Dictionary<string, string>()
                        };
                    }

                    var stat = stats[stock];
                    stat.RawCount += 1;
                    stat.WeightedCount += weight;

                    // 更新整體股票的時間
                    if (postDate < stat.FirstTime) stat.FirstTime = postDate;
                    if (postDate > stat.LastTime) stat.LastTime = postDate;

                    // 鄰近定位演算法 (尋找關鍵字)
                    string stockName = stockNameMap.ContainsKey(stock) ? stockNameMap[stock] : "";
                    List<int> stockIndices = GetAllIndices(content, stock);
                    if (!string.IsNullOrEmpty(stockName))
                    {
                        stockIndices.AddRange(GetAllIndices(content, stockName));
                    }

                    bool CheckProximity(string word)
                    {
                        List<int> wordIndices = GetAllIndices(content, word);
                        foreach (int sIdx in stockIndices)
                        {
                            foreach (int wIdx in wordIndices)
                            {
                                if (Math.Abs(sIdx - wIdx) <= PROXIMITY_LIMIT) return true;
                            }
                        }
                        return false;
                    }

                    // 檢查 Hype 關鍵字
                    foreach (var word in hypeWords)
                    {
                        if (CheckProximity(word))
                        {
                            stat.WeightedHypeScore += weight;
                            // 記錄或覆寫該話術的最後出現時間
                            stat.Tags[word] = formattedPostTime;
                        }
                    }

                    // 檢查 Bear 關鍵字
                    foreach (var word in bearWords)
                    {
                        if (CheckProximity(word))
                        {
                            // 記錄或覆寫該話術的最後出現時間
                            stat.Tags[word] = formattedPostTime;
                        }
                    }
                }
            }

            // 4. 寫入 Stocks_statistics
            Console.WriteLine("計算完成，準備更新資料庫...");

            var oldStatsSnapshot = await db.Collection("Stocks_statistics").GetSnapshotAsync();
            WriteBatch batch = db.StartBatch();
            int opCount = 0;

            foreach (var doc in oldStatsSnapshot.Documents)
            {
                batch.Delete(doc.Reference);
                opCount++;
                if (opCount == 490) { await batch.CommitAsync(); batch = db.StartBatch(); opCount = 0; }
            }

            int newRecordsCount = 0;
            foreach (var kvp in stats)
            {
                string stockId = kvp.Key;
                var stat = kvp.Value;

                if (stat.RawCount >= MIN_MENTIONS_TO_SHOW && stockNameMap.ContainsKey(stockId))
                {
                    double fomoRatio = 0;
                    if (stat.RawCount >= MIN_MENTIONS_FOR_FOMO && stat.WeightedCount > 0)
                    {
                        fomoRatio = stat.WeightedHypeScore / stat.WeightedCount;
                    }

                    // 這裡直接把 Dictionary 型態的 stat.Tags 傳進去，Firebase 就會自動建立 Map 結構！
                    var docData = new Dictionary<string, object>
                {
                    { "stock_id", stockId },
                    { "count", stat.RawCount },
                    { "fomo_index", Math.Round(fomoRatio, 2) },
                    { "tags", stat.Tags }, // 這裡傳入 Dictionary<string, string>
                    { "first_time", stat.FirstTime.AddHours(8).ToString("yyyy/MM/dd HH:mm:ss") },
                    { "last_time", stat.LastTime.AddHours(8).ToString("yyyy/MM/dd HH:mm:ss") }
                };

                    batch.Set(db.Collection("Stocks_statistics").Document(stockId), docData);
                    opCount++;
                    newRecordsCount++;

                    if (opCount == 490) { await batch.CommitAsync(); batch = db.StartBatch(); opCount = 0; }
                }
            }

            if (opCount > 0) await batch.CommitAsync();

            Console.WriteLine($"✅ [任務 2 完成] 成功更新 {newRecordsCount} 筆熱門股票統計（帶有 Map 格式 Tags）至 Stocks_statistics！");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ [任務 2 失敗]: {ex.Message}\n{ex.StackTrace}");
        }
    }

    static async Task<FirestoreDb> InitializeFirebaseAsync()
    {
        try
        {
            // 取得當前執行的絕對路徑，並拼出 firebase-key.json 的絕對路徑
            string keyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "firebase-key.json");

            // 如果執行目錄找不到，試著用工作目錄
            if (!File.Exists(keyPath))
            {
                keyPath = Path.Combine(Directory.GetCurrentDirectory(), "firebase-key.json");
            }

            Console.WriteLine($"[Firebase] 使用的金鑰路徑: {keyPath}");

            Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", keyPath); 
            return await FirestoreDb.CreateAsync("ai-001-d64e3");
        }
        catch (Exception ex)
        {
            // 加上這行，萬一初始化失敗，你可以直接在 GitHub 日誌看到詳細錯誤，而不是默默回傳 null
            Console.WriteLine($"[Firebase 初始化錯誤] {ex.Message}");
            return null;
        }
    }

    // ==========================================
    // 輔助函式區
    // ==========================================

    // 取得字串中所有子字串的位置
    static List<int> GetAllIndices(string source, string matchString)
    {
        List<int> indices = new List<int>();
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(matchString)) return indices;

        int index = source.IndexOf(matchString);
        while (index != -1)
        {
            indices.Add(index);
            index = source.IndexOf(matchString, index + matchString.Length);
        }
        return indices;
    }

    // 呼叫 FinMind 取得股票名稱對照表
    static async Task<Dictionary<string, string>> FetchStockNamesAsync()
    {
        var map = new Dictionary<string, string>();
        try
        {
            using HttpClient client = new HttpClient();
            string response = await client.GetStringAsync("https://api.finmindtrade.com/api/v4/data?dataset=TaiwanStockInfo");
            using JsonDocument doc = JsonDocument.Parse(response);

            if (doc.RootElement.GetProperty("msg").GetString() == "success")
            {
                var data = doc.RootElement.GetProperty("data");
                foreach (var item in data.EnumerateArray())
                {
                    string id = item.GetProperty("stock_id").GetString();
                    string name = item.GetProperty("stock_name").GetString();
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                    {
                        map[id] = name;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[警告] 無法取得股票代碼表: {ex.Message}");
        }
        return map;
    }

    // 內部資料結構，用於統計暫存
    public class StockStatData
    {
        public int RawCount { get; set; } = 0;
        public double WeightedCount { get; set; } = 0;
        public double WeightedHypeScore { get; set; } = 0;
        public DateTime FirstTime { get; set; }
        public DateTime LastTime { get; set; }
        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();
    }
}