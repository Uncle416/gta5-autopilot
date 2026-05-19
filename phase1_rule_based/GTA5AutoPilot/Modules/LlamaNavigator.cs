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
            {"洛圣都国际机场", new Vector3(-1267, 2995, 15)},
            {"国际机场", new Vector3(-1267, 2995, 15)},
            {"机场", new Vector3(-1267, 2995, 15)},
            {"威斯普奇海滩", new Vector3(-1550, -890, 5)},
            {"海滩", new Vector3(-1550, -890, 5)},
            {"麦克家", new Vector3(-812, 176, 55)},
            {"麦克的家", new Vector3(-812, 176, 55)},
            {"富兰克林家", new Vector3(-20, -1437, 30)},
            {"富兰克林的家", new Vector3(-20, -1437, 30)},
            {"崔佛家", new Vector3(1397, 3614, 40)},
            {"崔佛的家", new Vector3(1397, 3614, 40)},
            {"洛圣都市中心", new Vector3(-595, -110, 40)},
            {"市中心", new Vector3(-595, -110, 40)},
            {"好麦坞", new Vector3(-575, 260, 80)},
            {"好麦坞山", new Vector3(-575, 260, 80)},
            {"天文台", new Vector3(-500, 810, 340)},
            {"伽利略天文台", new Vector3(-500, 810, 340)},
            {"洛圣都港", new Vector3(20, -2700, 5)},
            {"港口", new Vector3(20, -2700, 5)},
            {"赛诺拉沙漠", new Vector3(1780, 3370, 40)},
            {"沙漠", new Vector3(1780, 3370, 40)},
            {"帕勒托湾", new Vector3(-180, 6300, 30)},
            {"洛圣都海关", new Vector3(-860, -1280, 5)},
            {"改车王", new Vector3(-860, -1280, 5)},
            {"凡洁莉", new Vector3(320, -1840, 27)},
            {"洛圣都大学", new Vector3(-1630, -115, 55)},
            {"高尔夫球场", new Vector3(-1310, 94, 55)},
            {"竞技场", new Vector3(-250, -2010, 30)},
            {"佩里托湾", new Vector3(220, 6460, 5)},
            {"军事基地", new Vector3(-2100, 2970, 33)},
            {"桑迪海岸", new Vector3(1750, 3300, 42)},
            {"洛圣都河", new Vector3(-1500, -1700, 5)},
            {"阿拉莫海", new Vector3(950, 4370, 5)},
        };

        private static readonly string[] CanonicalNames =
        {
            "洛圣都国际机场", "威斯普奇海滩", "麦克家", "富兰克林家", "崔佛家",
            "洛圣都市中心", "好麦坞", "天文台", "洛圣都港", "赛诺拉沙漠",
            "帕勒托湾", "洛圣都海关", "凡洁莉", "洛圣都大学", "高尔夫球场",
            "竞技场", "佩里托湾", "军事基地", "桑迪海岸", "洛圣都河", "阿拉莫海",
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
