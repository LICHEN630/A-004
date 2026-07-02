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
        FirestoreDb db = await InitializeFirebaseAsync();
        if (db == null) return;

        string command = args.Length > 0 ? args[0].ToLower() : "all";
        if (command == "threads" || command == "all") await RunThreadsTask(db);

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
    // 任務 2：Playwright 自建免費爬蟲 (取代原本的 Apify)
    // ========================================================

    static async Task RunThreadsTask(FirestoreDb db)
    {
        Console.WriteLine("\n--- [任務 2] 開始執行 Playwright (多關鍵字循環 + 防錯點擊 + 網址除錯) ---");

        List<string> keywords = new List<string> {
            "可以買","閉眼買","閉眼入","會漲停","會有驚喜","我的建議",
            "今天的散戶","明天的散戶","台股","漲停","飆股","獲利","上車","散戶","報牌","因為我不缺錢",
            "買在起漲點","賣在高峰處"
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

            // 針對每個關鍵字執行搜尋
            foreach (var kw in keywords)
            {
                Console.WriteLine($"\n--- 正在搜尋關鍵字: {kw} ---");
                string searchUrl = $"https://www.threads.net/search?q={Uri.EscapeDataString(kw)}";
                await page.GotoAsync(searchUrl);

                await page.WaitForSelectorAsync("div[data-pressable-container='true']", new PageWaitForSelectorOptions { Timeout = 15000 });
                await Task.Delay(2000);

                // 【優化點擊邏輯】：嘗試切換至「最近」標籤
                Console.WriteLine("嘗試切換至「最近」標籤...");
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

                // 向下捲動抓取資料
                for (int i = 0; i < 40; i++)
                {
                    await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");
                    await Task.Delay(7000);
                }

                // 抓取當前頁面資料 (加回時間標籤的擷取)
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

            // 【篩選與儲存】
            int count = 0;
            // 去重處理
            var uniquePosts = allCollectedPosts.GroupBy(p => p.GetProperty("post_url").GetString()).Select(g => g.First());

            foreach (var post in uniquePosts)
            {
                string postUrl = post.GetProperty("post_url").GetString() ?? "";
                string content = post.GetProperty("text_content").GetString() ?? "";
                string timeText = post.GetProperty("time_text").GetString() ?? "";

                // 1. 資料庫已儲存過的貼文直接跳過，不加入計算
                if (string.IsNullOrEmpty(postUrl) || existingUrls.Contains(postUrl))
                {
                    continue;
                }

                // 2. 篩選 24 小時內的貼文
                // Threads 時間格式包含 s(秒), m(分), h(時)。若包含 d(天), w(週) 則代表超過 24 小時。
                bool isWithin24Hours = !string.IsNullOrEmpty(timeText) &&
                                       (timeText.Contains("s") || timeText.Contains("m") || timeText.Contains("h") ||
                                        timeText.Contains("秒") || timeText.Contains("分") || timeText.Contains("時")) &&
                                       !(timeText.Contains("d") || timeText.Contains("w") || timeText.Contains("y") ||
                                         timeText.Contains("天") || timeText.Contains("週") || timeText.Contains("周") || timeText.Contains("年"));

                if (!isWithin24Hours)
                {
                    // 若想讓畫面乾淨點，這行 Console.WriteLine 可以註解掉
                    Console.WriteLine($"[DEBUG] 非 24 小時內貼文，跳過 | 時間: {timeText}");
                    continue;
                }

                // 檢查是否包含關鍵字
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
                else
                {
                    //Console.WriteLine($"[DEBUG] 股票代號不足 (找到 {stocks.Count} 個)，跳過: {postUrl}");
                }
            }

            await context.CloseAsync();
            Console.WriteLine($"✅ [任務 2 完成] 本次共彙整並寫入 {count} 篇貼文。");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ [任務 2 失敗]: {ex.Message}");
        }
    }

    static async Task<FirestoreDb> InitializeFirebaseAsync()
    {
        try
        {
            Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", "firebase-key.json");
            return await FirestoreDb.CreateAsync("ai-001-d64e3");
        }
        catch { return null; }
    }
}