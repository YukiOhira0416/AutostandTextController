using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AutostandTextController
{
    internal static class Program
    {
        private const string ENV_API_KEY = "AUTOSTAND_API_KEY";
        private const string ENV_STAND_ID = "AUTOSTAND_STAND_ID";
        private const string ENV_BASE_URL = "AUTOSTAND_BASE_URL";
        private const string ENV_OP_TIMEOUT_SEC = "AUTOSTAND_OP_TIMEOUT_SEC";

        private static int Main(string[] args)
        {
            try
            {
                var opt = Options.Parse(args);

                if (opt.ShowHelp)
                {
                    PrintHelp();
                    return 0;
                }

                var apiKey = FirstNonEmpty(opt.ApiKey, Environment.GetEnvironmentVariable(ENV_API_KEY));
                if (string.IsNullOrEmpty(apiKey))
                {
                    Console.Write("API Key (AUTOSTAND_API_KEY): ");
                    apiKey = ReadSecret();
                    Console.WriteLine();
                }

                var standIdStr = FirstNonEmpty(opt.StandId, Environment.GetEnvironmentVariable(ENV_STAND_ID));
                int standId = 0;
                if (!string.IsNullOrEmpty(standIdStr))
                {
                    standId = ParsePositiveIntOrThrow(standIdStr, "stand-id");
                }
                else
                {
                    Console.Write("Stand ID (AUTOSTAND_STAND_ID): ");
                    standId = ParsePositiveIntOrThrow(Console.ReadLine() ?? "", "stand-id");
                }

                var baseUrl = FirstNonEmpty(opt.BaseUrl, Environment.GetEnvironmentVariable(ENV_BASE_URL), AutostandClient.DefaultBaseUrl);

                double opTimeoutSec = 30.0;
                if (!string.IsNullOrEmpty(opt.OpTimeoutSec))
                {
                    opTimeoutSec = ParsePositiveDoubleOrThrow(opt.OpTimeoutSec, "op-timeout-sec");
                }
                else
                {
                    var envOpTimeout = Environment.GetEnvironmentVariable(ENV_OP_TIMEOUT_SEC);
                    if (!string.IsNullOrEmpty(envOpTimeout))
                    {
                        opTimeoutSec = ParsePositiveDoubleOrThrow(envOpTimeout, "op-timeout-sec");
                    }
                }

                var cfg = new RuntimeConfig
                {
                    ApiKey = apiKey,
                    StandId = standId,
                    BaseUrl = baseUrl,
                    OpTimeoutSec = opTimeoutSec
                };

                using (var client = new AutostandClient(apiKey: cfg.ApiKey, baseUrl: cfg.BaseUrl, timeoutSec: 10.0))
                {
                    if (string.IsNullOrEmpty(opt.Command))
                    {
                        InteractiveLoop(client, cfg);
                        return 0;
                    }

                    RunOne(client, cfg, opt.Command);
                    return 0;
                }
            }
            catch (Exception ex)
            {
                PrintException(ex);
                return 1;
            }
        }

        private static void RunOne(AutostandClient client, RuntimeConfig cfg, string cmdRaw)
        {
            var cmd = NormalizeCommand(cmdRaw);

            switch (cmd)
            {
                case "up":
                    PrintStand(client.Up(cfg.StandId, cfg.OpTimeoutSec));
                    break;

                case "down":
                    PrintStand(client.Down(cfg.StandId, cfg.OpTimeoutSec));
                    break;

                case "status":
                    PrintStand(client.CheckStatus(cfg.StandId, cfg.OpTimeoutSec));
                    break;

                case "battery":
                    PrintBattery(client.CheckBattery(cfg.StandId, cfg.OpTimeoutSec));
                    break;

                default:
                    Console.WriteLine($"Unknown command: {cmdRaw}");
                    PrintHelp();
                    break;
            }
        }

        private static void InteractiveLoop(AutostandClient client, RuntimeConfig cfg)
        {
            Console.WriteLine("AUTO STAND Text Controller");
            Console.WriteLine("Commands: up, down, status, battery, help, quit");
            Console.WriteLine();

            while (true)
            {
                Console.Write($"stand={cfg.StandId}> ");
                var line = Console.ReadLine();
                if (line == null) return;

                line = line.Trim();
                if (line.Length == 0) continue;

                var parts = SplitArgs(line);
                if (parts.Length == 0) continue;

                var cmd = NormalizeCommand(parts[0]);

                try
                {
                    if (cmd == "quit" || cmd == "exit" || cmd == "q")
                        return;

                    if (cmd == "help" || cmd == "h" || cmd == "?")
                    {
                        Console.WriteLine("Commands:");
                        Console.WriteLine("  up       : UP");
                        Console.WriteLine("  down     : DOWN");
                        Console.WriteLine("  status   : 状態確認");
                        Console.WriteLine("  battery  : バッテリー確認");
                        Console.WriteLine("  quit     : 終了");
                        Console.WriteLine();
                        continue;
                    }

                    switch (cmd)
                    {
                        case "up":
                            PrintStand(client.Up(cfg.StandId, cfg.OpTimeoutSec));
                            break;

                        case "down":
                            PrintStand(client.Down(cfg.StandId, cfg.OpTimeoutSec));
                            break;

                        case "status":
                            PrintStand(client.CheckStatus(cfg.StandId, cfg.OpTimeoutSec));
                            break;

                        case "battery":
                            PrintBattery(client.CheckBattery(cfg.StandId, cfg.OpTimeoutSec));
                            break;

                        default:
                            Console.WriteLine($"Unknown command: {parts[0]}");
                            Console.WriteLine("Type 'help' for commands.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    PrintException(ex);
                }
            }
        }

        private static void PrintStand(StandInfo s)
        {
            Console.WriteLine("----- Stand Info -----");
            Console.WriteLine($"id         : {s.Id}");
            Console.WriteLine($"operate    : {ToNA(s.Operate)}");
            Console.WriteLine($"armState   : {ToNA(s.ArmState)}");
            Console.WriteLine($"standState : {ToNA(s.StandState)}");
            Console.WriteLine($"battery    : {BatteryToString(s.Battery)}");
            Console.WriteLine($"ultrasonic : {UltrasonicToString(s.UltrasonicDetected)}");
            Console.WriteLine("----------------------");
            Console.WriteLine();
        }

        private static void PrintBattery(BatteryLevel? b)
        {
            Console.WriteLine("----- Battery -----");
            Console.WriteLine(BatteryToString(b));
            Console.WriteLine("-------------------");
            Console.WriteLine();
        }

        private static void PrintException(Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("===== ERROR =====");
            Console.WriteLine(ex.Message);
            Console.WriteLine($"Type: {ex.GetType().FullName}");

            if (ex is AutoStandApiError api)
            {
                Console.WriteLine($"API Code : {api.Code}");
                Console.WriteLine($"API Title: {api.Title}");
                Console.WriteLine($"API Desc : {api.Description}");
                Console.WriteLine($"API Date : {api.ResponseDate}");
            }

            if (ex is AutoStandHttpError http)
            {
                Console.WriteLine($"HTTP Status: {http.StatusCode}");
                if (http.Body != null)
                {
                    Console.WriteLine("HTTP Body:");
                    Console.WriteLine(http.Body.ToString());
                }
            }

            Console.WriteLine("=================");
            Console.WriteLine();
        }

        private static string NormalizeCommand(string raw)
        {
            var c = (raw ?? "").Trim().ToLowerInvariant();

            if (c == "open") return "up";
            if (c == "close") return "down";
            if (c == "u" || c == "up") return "up";
            if (c == "d" || c == "down") return "down";
            if (c == "s" || c == "status" || c == "state") return "status";
            if (c == "b" || c == "bat" || c == "battery") return "battery";

            return c;
        }

        private static string[] SplitArgs(string commandLine)
        {
            // simple split with quotes: set baseurl "https://.../v1/"
            var res = new List<string>();
            var cur = "";
            bool inQuotes = false;

            foreach (var ch in commandLine)
            {
                if (ch == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (!inQuotes && char.IsWhiteSpace(ch))
                {
                    if (cur.Length > 0)
                    {
                        res.Add(cur);
                        cur = "";
                    }
                    continue;
                }

                cur += ch;
            }

            if (cur.Length > 0) res.Add(cur);
            return res.ToArray();
        }

        private static string BatteryToString(BatteryLevel? b)
        {
            if (!b.HasValue) return "N/A";
            return $"{(int)b.Value}%";
        }

        private static string UltrasonicToString(bool? detected)
        {
            if (!detected.HasValue) return "N/A";
            return detected.Value ? "detected" : "not detected";
        }

        private static string ToNA(string s)
        {
            return string.IsNullOrEmpty(s) ? "N/A" : s;
        }

        private static int ParsePositiveIntOrThrow(string s, string name)
        {
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0)
                return v;
            throw new ArgumentException($"Invalid {name}: {s}");
        }

        private static double ParsePositiveDoubleOrThrow(string s, string name)
        {
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
                return v;
            throw new ArgumentException($"Invalid {name}: {s}");
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return null;
        }

        private static string ReadSecret()
        {
            // read without echo
            var chars = new List<char>();

            while (true)
            {
                var key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Enter)
                    break;

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (chars.Count > 0)
                    {
                        chars.RemoveAt(chars.Count - 1);
                        Console.Write("\b \b");
                    }
                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    chars.Add(key.KeyChar);
                    Console.Write("*");
                }
            }

            return new string(chars.ToArray());
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  AutostandTextController.exe [command] [options]");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  up       : UP");
            Console.WriteLine("  down     : DOWN");
            Console.WriteLine("  status   : 状態確認");
            Console.WriteLine("  battery  : バッテリー確認");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --api-key <key>        (or env AUTOSTAND_API_KEY)");
            Console.WriteLine("  --stand-id <id>        (or env AUTOSTAND_STAND_ID)");
            Console.WriteLine("  --base-url <url>       (or env AUTOSTAND_BASE_URL)");
            Console.WriteLine("  --op-timeout-sec <sec> (or env AUTOSTAND_OP_TIMEOUT_SEC, default 30)");
            Console.WriteLine("  --help");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  AutostandTextController.exe up --stand-id 401 --api-key xxxxx");
            Console.WriteLine("  AutostandTextController.exe status");
            Console.WriteLine("  (no args) -> interactive");
        }

        private sealed class RuntimeConfig
        {
            public string ApiKey;
            public int StandId;
            public string BaseUrl;
            public double OpTimeoutSec;
        }

        private sealed class Options
        {
            public bool ShowHelp;
            public string Command;
            public string ApiKey;
            public string StandId;
            public string BaseUrl;
            public string OpTimeoutSec;

            public static Options Parse(string[] args)
            {
                var o = new Options();
                var list = (args ?? Array.Empty<string>()).ToList();

                if (list.Count > 0 && !list[0].StartsWith("-", StringComparison.Ordinal))
                {
                    o.Command = list[0];
                    list.RemoveAt(0);
                }

                for (int i = 0; i < list.Count; i++)
                {
                    var a = list[i];

                    if (a == "--help" || a == "-h" || a == "/?")
                    {
                        o.ShowHelp = true;
                        continue;
                    }

                    if (a == "--api-key")
                    {
                        o.ApiKey = NeedValue(list, ref i, "--api-key");
                        continue;
                    }

                    if (a == "--stand-id")
                    {
                        o.StandId = NeedValue(list, ref i, "--stand-id");
                        continue;
                    }

                    if (a == "--base-url")
                    {
                        o.BaseUrl = NeedValue(list, ref i, "--base-url");
                        continue;
                    }

                    if (a == "--op-timeout-sec")
                    {
                        o.OpTimeoutSec = NeedValue(list, ref i, "--op-timeout-sec");
                        continue;
                    }

                    throw new ArgumentException($"Unknown option: {a}");
                }

                return o;
            }

            private static string NeedValue(List<string> list, ref int i, string name)
            {
                if (i + 1 >= list.Count)
                    throw new ArgumentException($"Missing value for {name}");
                i++;
                return list[i];
            }
        }
    }

    public class AutostandClient : IDisposable
    {
        public static string DefaultBaseUrl { get; } = "https://api.autostand.example.com/v1/";

        public AutostandClient(string apiKey, string baseUrl, double timeoutSec)
        {
        }

        public StandInfo Up(int standId, double timeoutSec)
        {
            throw new NotImplementedException("AutostandClient.Up is not implemented");
        }

        public StandInfo Down(int standId, double timeoutSec)
        {
            throw new NotImplementedException("AutostandClient.Down is not implemented");
        }

        public StandInfo CheckStatus(int standId, double timeoutSec)
        {
            throw new NotImplementedException("AutostandClient.CheckStatus is not implemented");
        }

        public BatteryLevel? CheckBattery(int standId, double timeoutSec)
        {
            throw new NotImplementedException("AutostandClient.CheckBattery is not implemented");
        }

        public void Dispose()
        {
        }
    }

    public class StandInfo
    {
        public int Id { get; set; }
        public string Operate { get; set; }
        public string ArmState { get; set; }
        public string StandState { get; set; }
        public BatteryLevel? Battery { get; set; }
        public bool? UltrasonicDetected { get; set; }
    }

    public enum BatteryLevel
    {
        Level0 = 0,
        Level10 = 10,
        Level20 = 20,
        Level30 = 30,
        Level40 = 40,
        Level50 = 50,
        Level60 = 60,
        Level70 = 70,
        Level80 = 80,
        Level90 = 90,
        Level100 = 100
    }

    public class AutoStandApiError : Exception
    {
        public string Code { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public DateTime? ResponseDate { get; set; }

        public AutoStandApiError(string message) : base(message)
        {
        }
    }

    public class AutoStandHttpError : Exception
    {
        public int StatusCode { get; set; }
        public object Body { get; set; }

        public AutoStandHttpError(string message) : base(message)
        {
        }
    }
}
