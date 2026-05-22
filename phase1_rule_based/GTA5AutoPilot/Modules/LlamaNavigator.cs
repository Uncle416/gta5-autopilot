using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GTA.Math;
using LLama;
using LLama.Common;
using Newtonsoft.Json.Linq;

namespace GTA5AutoPilot.Modules
{
    /// <summary>
    /// LLM-powered navigation parser using LlamaSharp + GGUF model.
    /// Runs inference on a background thread to avoid blocking the game.
    /// </summary>
    public class LlamaNavigator : IDisposable
    {
        private LLamaWeights _model;
        private LLamaContext _context;
        private InteractiveExecutor _executor;
        private bool _ready;
        private bool _disposed;

        private static readonly Dictionary<string, Vector3> Gazetteer =
            new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase)
        {
            // === 机场 / 交通枢纽 ===
            {"洛圣都国际机场", new Vector3(-1267, 2995, 15)}, {"国际机场", new Vector3(-1267, 2995, 15)}, {"机场", new Vector3(-1267, 2995, 15)},
            {"桑迪海岸机场", new Vector3(1740, 3294, 42)}, {"小机场", new Vector3(1740, 3294, 42)},
            {"洛圣都港", new Vector3(20, -2700, 5)}, {"港口", new Vector3(20, -2700, 5)}, {"码头", new Vector3(20, -2700, 5)},
            {"洛圣都火车站", new Vector3(-780, -1160, 26)}, {"火车站", new Vector3(-780, -1160, 26)},
            {"地铁站", new Vector3(-595, -110, 40)},
            // === 商业街 / 购物 ===
            {"罗克福德山", new Vector3(-800, 550, 120)}, {"罗克福德", new Vector3(-800, 550, 120)},
            {"好麦坞大道", new Vector3(-575, 260, 80)}, {"好麦坞", new Vector3(-575, 260, 80)},
            {"威斯普奇海滩", new Vector3(-1550, -890, 5)}, {"海滩", new Vector3(-1550, -890, 5)},
            {"洛圣都市中心", new Vector3(-595, -110, 40)}, {"市中心", new Vector3(-595, -110, 40)},
            {"小首尔", new Vector3(-600, -700, 32)},
            {"韩国城", new Vector3(-600, -700, 32)},
            {"中国城", new Vector3(-780, -880, 26)},
            {"艾尔塔", new Vector3(-280, -440, 45)},
            {"圆帽山", new Vector3(-1100, -100, 65)},
            {"斯卓贝利", new Vector3(180, -580, 42)},
            {"拉梅萨", new Vector3(800, -970, 40)},
            {"赛普勒斯", new Vector3(600, -500, 30)},
            {"穆列塔高地", new Vector3(-1120, 200, 65)},
            {"达维斯特", new Vector3(350, -1630, 30)},
            {"兰桥", new Vector3(255, -1670, 27)},
            {"凡洁莉", new Vector3(320, -1840, 27)},
            {"东洛圣都", new Vector3(770, -1270, 28)},
            {"洛圣都国际机场区", new Vector3(-1267, 2995, 15)},
            {"艾尔布罗高地", new Vector3(-450, -800, 52)},
            {"密申罗", new Vector3(-300, -1300, 42)},
            {"洛圣都河", new Vector3(-1500, -1700, 5)},
            // === 沙漠 / 乡村 ===
            {"赛诺拉沙漠", new Vector3(1780, 3370, 40)}, {"沙漠", new Vector3(1780, 3370, 40)},
            {"桑迪海岸", new Vector3(1750, 3300, 42)},
            {"葡萄籽", new Vector3(1690, 4900, 45)},
            {"阿拉莫海", new Vector3(950, 4370, 5)},
            {"帕勒托湾", new Vector3(-180, 6300, 30)},
            {"佩里托湾", new Vector3(220, 6460, 5)},
            {"奇丽耶德山", new Vector3(-350, 4020, 120)}, {"奇丽耶德", new Vector3(-350, 4020, 120)},
            {"大洋公路", new Vector3(-2910, 1700, 10)},
            {"班宁", new Vector3(-1800, 2600, 35)},
            // === 地标 / 景点 ===
            {"天文台", new Vector3(-500, 810, 340)}, {"伽利略天文台", new Vector3(-500, 810, 340)},
            {"Vinewood标志", new Vector3(370, 3870, 390)}, {"好麦坞标志", new Vector3(370, 3870, 390)},
            {"洛圣都码头", new Vector3(-1550, -960, 5)}, {"游乐园", new Vector3(-1550, -960, 5)},
            {"摩天轮", new Vector3(-1550, -960, 5)},
            {"Kortz中心", new Vector3(-1310, 94, 55)}, {"高尔夫球场", new Vector3(-1310, 94, 55)},
            {"洛圣都大学", new Vector3(-1630, -115, 55)}, {"大学", new Vector3(-1630, -115, 55)},
            {"竞技场", new Vector3(-250, -2010, 30)}, {"Maze Bank竞技场", new Vector3(-250, -2010, 30)},
            {"赛马场", new Vector3(490, -190, 80)}, {"马场", new Vector3(490, -190, 80)},
            {"洛圣都公墓", new Vector3(70, -1480, 32)},
            {"天文台公园", new Vector3(-500, 810, 340)},
            {"洛圣都动物园", new Vector3(-1420, 890, 90)},
            {"军事基地", new Vector3(-2100, 2970, 33)}, {"桑库多堡垒", new Vector3(-2100, 2970, 33)},
            {"洛圣都水库", new Vector3(440, 1400, 160)},
            {"风力发电场", new Vector3(2440, 1660, 40)},
            {"人道实验室", new Vector3(3500, 3600, 40)},
            {"采石场", new Vector3(2800, 2900, 40)},
            {"洛圣都国际赛道", new Vector3(-1370, -640, 35)},
            {"高尔夫俱乐部", new Vector3(-1310, 94, 55)},
            {"网球俱乐部", new Vector3(-325, 365, 88)},
            {"海滩游乐场", new Vector3(-1550, -960, 5)},
            {"洛圣都运河", new Vector3(790, -1920, 3)},
            // === 警察 / 医院 / 消防 ===
            {"洛圣都警局", new Vector3(-665, -920, 24)}, {"警察局", new Vector3(-665, -920, 24)},
            {"市中心警局", new Vector3(400, -730, 28)},
            {"好麦坞警局", new Vector3(-320, 330, 75)},
            {"威斯普奇警局", new Vector3(-1100, -860, 16)},
            {"洛圣都医院", new Vector3(-675, -450, 34)}, {"医院", new Vector3(-675, -450, 34)},
            {"市中心医院", new Vector3(-440, -280, 40)},
            {"桑迪海岸医院", new Vector3(1840, 3690, 35)},
            {"帕勒托医院", new Vector3(-260, 6310, 32)},
            {"消防局", new Vector3(-630, -1080, 22)},
            {"洛圣都消防局", new Vector3(-630, -1080, 22)},
            // === 改车厂 / 修车 ===
            {"洛圣都改车王", new Vector3(-860, -1280, 5)}, {"改车王", new Vector3(-860, -1280, 5)}, {"改车厂", new Vector3(-860, -1280, 5)},
            {"市中心改车王", new Vector3(480, -1085, 26)},
            {"拉梅萨改车王", new Vector3(750, -1155, 26)},
            {"好麦坞改车王", new Vector3(-215, -100, 56)},
            {"国际机场改车王", new Vector3(-1090, 2740, 2)},
            // === 武器店 ===
            {"武装国度", new Vector3(-660, -920, 23)}, {"武器店", new Vector3(-660, -920, 23)},
            {"市中心武器店", new Vector3(410, -730, 28)},
            {"好麦坞武器店", new Vector3(-370, 260, 82)},
            {"圆帽山武器店", new Vector3(-1060, 200, 60)},
            {"拉梅萨武器店", new Vector3(790, -1080, 26)},
            {"桑迪武器店", new Vector3(1690, 3310, 41)},
            {"帕勒托武器店", new Vector3(-350, 6130, 30)},
            // === 理发店 ===
            {"理发店", new Vector3(-660, -920, 23)},
            {"市中心理发店", new Vector3(-440, -360, 45)},
            {"好麦坞理发店", new Vector3(-200, -100, 56)},
            {"威斯普奇理发店", new Vector3(-1090, -840, 18)},
            {"桑迪理发店", new Vector3(1690, 3310, 41)},
            // === 服装店 ===
            {"服装店", new Vector3(-730, -920, 24)},
            {"Ponsonbys", new Vector3(-500, -350, 42)}, {"彭森比", new Vector3(-500, -350, 42)},
            {"Binco", new Vector3(530, -1285, 27)}, {"宾科", new Vector3(530, -1285, 27)},
            {"Suburban", new Vector3(280, -160, 75)}, {"郊区服饰", new Vector3(280, -160, 75)},
            {"Discount Store", new Vector3(170, -1320, 28)},
            // === 纹身店 ===
            {"纹身店", new Vector3(-450, -400, 40)},
            {"威斯普奇纹身", new Vector3(-1090, -840, 18)},
            {"艾尔塔纹身", new Vector3(-270, -440, 45)},
            // === 酒吧 / 夜店 ===
            {"香草独角兽", new Vector3(100, -1290, 28)}, {"脱衣舞俱乐部", new Vector3(100, -1290, 28)},
            {"Tequi-La-La", new Vector3(-560, 280, 80)},
            {"巴哈马妈妈", new Vector3(-770, -890, 20)},
            {"喜剧俱乐部", new Vector3(-420, 255, 88)},
            // === 赌场 ===
            {"钻石赌场", new Vector3(930, 50, 80)}, {"赌场", new Vector3(930, 50, 80)},
            // === 汽车经销商 ===
            {"豪华汽车", new Vector3(-400, -150, 55)},
            {"传奇车业", new Vector3(560, -170, 77)},
            {"南洛圣都汽车", new Vector3(55, -1365, 28)},
            // === 加油站 ===
            {"加油站", new Vector3(-360, -1400, 25)},
            {"小首尔加油站", new Vector3(-580, -710, 32)},
            {"桑迪加油站", new Vector3(1690, 3310, 45)},
            {"好麦坞加油站", new Vector3(280, 10, 87)},
            // === 便利店 / 超市 ===
            {"24小时商店", new Vector3(-700, -920, 24)}, {"便利店", new Vector3(-700, -920, 24)},
            {"LTD加油站", new Vector3(-360, -1400, 25)},
            {"Rob's Liquor", new Vector3(50, -1390, 28)},
            // === 银行 ===
            {"Maze Bank", new Vector3(-520, -108, 45)}, {"银行", new Vector3(-520, -108, 45)},
            {"Fleeca银行", new Vector3(-755, -850, 22)},
            // === 政府 / 公共建筑 ===
            {"洛圣都市政府", new Vector3(-550, -110, 45)}, {"市政府", new Vector3(-550, -110, 45)},
            {"FIB大楼", new Vector3(-800, -680, 106)}, {"FIB", new Vector3(-800, -680, 106)},
            {"IAA大楼", new Vector3(-790, -700, 106)},
            {"洛圣都法院", new Vector3(-500, -200, 45)}, {"法院", new Vector3(-500, -200, 45)},
            {"洛圣都监狱", new Vector3(1600, 2560, 45)}, {"监狱", new Vector3(1600, 2560, 45)},
            // === 角色住所 ===
            {"麦克家", new Vector3(-812, 176, 55)}, {"麦克的家", new Vector3(-812, 176, 55)},
            {"富兰克林家", new Vector3(-20, -1437, 30)}, {"富兰克林的家", new Vector3(-20, -1437, 30)}, {"小富家", new Vector3(-20, -1437, 30)},
            {"崔佛家", new Vector3(1397, 3614, 40)}, {"崔佛的家", new Vector3(1397, 3614, 40)}, {"老崔家", new Vector3(1397, 3614, 40)},
            // === 任务 / DLC 地点 ===
            {"洛圣都国际机场机库", new Vector3(-1267, 2995, 15)},
            {"机库", new Vector3(-1267, 2860, 18)},
            {"地堡", new Vector3(2500, 3300, 0)},
            {"设施", new Vector3(-500, 6000, 0)},
            {"会所", new Vector3(980, -147, 54)},
            {"办公室", new Vector3(-520, -108, 45)},
            {"夜店", new Vector3(-620, -20, 52)},
            {"游戏厅", new Vector3(60, -1390, 28)},
            {"改装铺", new Vector3(480, -1085, 26)},
            {"事务所", new Vector3(-380, -350, 45)},
            {"载具仓库", new Vector3(850, -1550, 28)},
            {"特种货物仓库", new Vector3(790, -1920, 18)},
            {"机动作战中心", new Vector3(2500, 3300, 0)},
            {"复仇者", new Vector3(1740, 3294, 42)},
        };

        private static readonly string[] CanonicalNames =
        {
            "洛圣都国际机场", "桑迪海岸机场", "洛圣都港", "洛圣都火车站", "地铁站",
            "罗克福德山", "好麦坞大道", "威斯普奇海滩", "洛圣都市中心", "小首尔",
            "中国城", "拉梅萨", "斯卓贝利", "兰桥", "凡洁莉", "圆帽山",
            "赛诺拉沙漠", "桑迪海岸", "葡萄籽", "阿拉莫海", "帕勒托湾", "佩里托湾", "奇丽耶德山",
            "天文台", "Vinewood标志", "洛圣都码头", "Kortz中心", "洛圣都大学",
            "竞技场", "赛马场", "洛圣都公墓", "军事基地", "人道实验室",
            "风力发电场", "洛圣都国际赛道", "海滩游乐场",
            "洛圣都警局", "市中心警局", "洛圣都医院", "消防局",
            "洛圣都改车王", "市中心改车王", "拉梅萨改车王",
            "武装国度", "药店",
            "理发店", "服装店", "纹身店",
            "香草独角兽", "巴哈马妈妈",
            "钻石赌场", "赌场",
            "豪华汽车", "传奇车业",
            "Maze Bank", "FIB大楼", "洛圣都市政府", "洛圣都法院", "洛圣都监狱",
            "麦克家", "富兰克林家", "崔佛家",
            "机库", "地堡", "会所", "夜店", "游戏厅", "改装铺", "事务所", "载具仓库",
        };

        public bool IsReady => _ready;

        public LlamaNavigator(string modelPath)
        {
            if (!File.Exists(modelPath))
            {
                GTA.UI.Screen.ShowSubtitle("LLM model not found at scripts/models/", 5000);
                return;
            }

            try
            {
                var parameters = new ModelParams(modelPath)
                {
                    ContextSize = 2048,
                    GpuLayerCount = 0,
                    BatchSize = 512,
                };
                _model = LLamaWeights.LoadFromFile(parameters);
                _context = _model.CreateContext(parameters);
                _executor = new InteractiveExecutor(_context);
                _ready = true;
                GTA.UI.Screen.ShowSubtitle("LLM navigator ready", 3000);
            }
            catch (Exception ex)
            {
                GTA.UI.Screen.ShowSubtitle("LLM load: " + ex.Message, 5000);
            }
        }

        /// <summary>
        /// Parse navigation text on a background thread.
        /// Returns (waypoints, responseMessage).
        /// </summary>
        public Task<(List<Waypoint>, string)> ParseNavigationAsync(string userText)
        {
            return Task.Run(() => ParseNavigation(userText));
        }

        public (List<Waypoint>, string) ParseNavigation(string userText)
        {
            if (!_ready || _disposed || string.IsNullOrWhiteSpace(userText))
                return (new List<Waypoint>(), "LLM not ready");

            try
            {
                string prompt = $@"你是GTA5导航助手。理解用户的导航意图，按时间顺序提取途经地点。

可用地点名称（只能从下面选）:
{string.Join("\n", CanonicalNames)}

示例:
用户: 先去机场接个人，但出发之前去趟麦克家
{{""places"":[""麦克家"",""洛圣都国际机场""]}}

用户: 我想去海滩，然后打高尔夫，顺便去趟改车王
{{""places"":[""威斯普奇海滩"",""高尔夫球场"",""洛圣都海关""]}}

用户: 从市中心出发，经过好麦坞，最后到天文台
{{""places"":[""洛圣都市中心"",""好麦坞"",""天文台""]}}

只输出JSON，不要其他文字。如果用户提到的地点不在上面列表里，略过它。";

                // Create new session per request (avoid message ordering issues)
                var history = new ChatHistory();
                history.AddMessage(AuthorRole.System, prompt);
                var session = new ChatSession(_executor, history);

                // Run inference synchronously on this background thread
                var responseBuilder = new StringBuilder();
                var task = Task.Run(async () =>
                {
                    var msg = new ChatHistory.Message(AuthorRole.User, userText);
                    await foreach (var text in session.ChatAsync(msg))
                    {
                        responseBuilder.Append(text);
                    }
                });
                task.GetAwaiter().GetResult();

                string raw = responseBuilder.ToString().Trim();
                return ParsePlaces(raw);
            }
            catch (Exception ex)
            {
                return (new List<Waypoint>(), $"LLM error: {ex.Message}");
            }
        }

        private (List<Waypoint>, string) ParsePlaces(string llmOutput)
        {
            var jsonMatch = Regex.Match(llmOutput, @"\{[\s\S]*\}");
            if (!jsonMatch.Success)
                return (new List<Waypoint>(), "No JSON found: " + llmOutput.Substring(0, System.Math.Min(100, llmOutput.Length)));

            try
            {
                var obj = JObject.Parse(jsonMatch.Value);
                var places = obj["places"]?.ToObject<List<string>>();
                if (places == null || places.Count == 0)
                    return (new List<Waypoint>(), "No places found");

                var waypoints = new List<Waypoint>();
                var unknowns = new List<string>();
                foreach (var name in places)
                {
                    var match = FuzzyMatch(name.Trim());
                    if (match != null)
                        waypoints.Add(new Waypoint(match.Value.name, match.Value.pos));
                    else
                        unknowns.Add(name);
                }

                string msg = $"Parsed {waypoints.Count} waypoints";
                foreach (var wp in waypoints)
                    msg += $"\n  -> {wp.Name}";
                if (unknowns.Count > 0)
                    msg += $"\nUnknown: {string.Join(", ", unknowns)}";
                return (waypoints, msg);
            }
            catch (Exception ex)
            {
                return (new List<Waypoint>(), $"Parse err: {ex.Message}");
            }
        }

        private (string name, Vector3 pos)? FuzzyMatch(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            if (Gazetteer.TryGetValue(input, out var exact))
                return (input, exact);

            string best = null;
            float bestScore = 0f;
            foreach (var kv in Gazetteer)
            {
                float s = Similarity(input, kv.Key);
                if (s > bestScore) { bestScore = s; best = kv.Key; }
            }
            if (bestScore > 0.4f && best != null)
                return (best, Gazetteer[best]);
            return null;
        }

        private static float Similarity(string a, string b)
        {
            a = a.ToLowerInvariant();
            b = b.ToLowerInvariant();
            if (a == b) return 1f;
            if (a.Contains(b) || b.Contains(a))
            {
                float r = (float)System.Math.Min(a.Length, b.Length) / System.Math.Max(a.Length, b.Length);
                return 0.7f + r * 0.3f;
            }
            int dist = Levenshtein(a, b);
            int max = System.Math.Max(a.Length, b.Length);
            return 1f - (float)dist / max;
        }

        private static int Levenshtein(string a, string b)
        {
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = System.Math.Min(System.Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            return d[a.Length, b.Length];
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _model?.Dispose();
            _context?.Dispose();
            _ready = false;
        }
    }
}
