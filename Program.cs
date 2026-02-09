using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autostand;

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
                    PrintStand(DoUp(client, cfg.StandId, cfg.OpTimeoutSec));
                    break;

                case "down":
                    PrintStand(DoDown(client, cfg.StandId, cfg.OpTimeoutSec));
                    break;

                case "status":
                    PrintStand(DoStatus(client, cfg.StandId, cfg.OpTimeoutSec));
                    break;

                case "battery":
                    PrintBattery(DoBattery(client, cfg.StandId, cfg.OpTimeoutSec));
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
                            PrintStand(DoUp(client, cfg.StandId, cfg.OpTimeoutSec));
                            break;

                        case "down":
                            PrintStand(DoDown(client, cfg.StandId, cfg.OpTimeoutSec));
                            break;

                        case "status":
                            PrintStand(DoStatus(client, cfg.StandId, cfg.OpTimeoutSec));
                            break;

                        case "battery":
                            PrintBattery(DoBattery(client, cfg.StandId, cfg.OpTimeoutSec));
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

        private static StandInfo DoUp(AutostandClient client, int standId, double timeoutSec)
        {
            // Support multiple Autostand.dll versions:
            // - Newer: Up/Down/CheckStatus/CheckBattery (sync wrappers)
            // - Newer: UpAsync/DownAsync/CheckStatusAsync/CheckBatteryAsync
            // - Older: Open/Close/GetStatus/GetBattery
            object res;

            // Prefer "Up" / "UpAsync"
            if (TryInvokeWithArgOptions(client, "Up", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return CoerceStandInfo(client, AwaitIfTask(res), "up", standId, timeoutSec);

            if (TryInvokeWithArgOptions(client, "UpAsync", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return CoerceStandInfo(client, AwaitIfTask(res), "up", standId, timeoutSec);

            // Fallback: "Open" / "OpenAsync"
            if (TryInvokeWithArgOptions(client, "Open", out res,
                    new object[] { standId, true, timeoutSec },
                    new object[] { standId, true },
                    new object[] { standId }))
                return CoerceStandInfo(client, AwaitIfTask(res), "up", standId, timeoutSec);

            if (TryInvokeWithArgOptions(client, "OpenAsync", out res,
                    new object[] { standId, true, timeoutSec },
                    new object[] { standId, true },
                    new object[] { standId }))
                return CoerceStandInfo(client, AwaitIfTask(res), "up", standId, timeoutSec);

            throw new MissingMethodException("AutostandClient", "Up/UpAsync/Open/OpenAsync");
        }

        private static StandInfo DoDown(AutostandClient client, int standId, double timeoutSec)
        {
            object res;

            if (TryInvokeWithArgOptions(client, "Down", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return CoerceStandInfo(client, AwaitIfTask(res), "down", standId, timeoutSec);

            if (TryInvokeWithArgOptions(client, "DownAsync", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return CoerceStandInfo(client, AwaitIfTask(res), "down", standId, timeoutSec);

            if (TryInvokeWithArgOptions(client, "Close", out res,
                    new object[] { standId, true, timeoutSec },
                    new object[] { standId, true },
                    new object[] { standId }))
                return CoerceStandInfo(client, AwaitIfTask(res), "down", standId, timeoutSec);

            if (TryInvokeWithArgOptions(client, "CloseAsync", out res,
                    new object[] { standId, true, timeoutSec },
                    new object[] { standId, true },
                    new object[] { standId }))
                return CoerceStandInfo(client, AwaitIfTask(res), "down", standId, timeoutSec);

            throw new MissingMethodException("AutostandClient", "Down/DownAsync/Close/CloseAsync");
        }

        private static StandInfo DoStatus(AutostandClient client, int standId, double timeoutSec)
        {
            object res;

            if (TryInvokeWithArgOptions(client, "CheckStatus", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return CoerceStandInfo(client, AwaitIfTask(res), "status", standId, timeoutSec);

            if (TryInvokeWithArgOptions(client, "CheckStatusAsync", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return CoerceStandInfo(client, AwaitIfTask(res), "status", standId, timeoutSec);

            if (TryInvokeWithArgOptions(client, "GetStatus", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return CoerceStandInfo(client, AwaitIfTask(res), "status", standId, timeoutSec);

            if (TryInvokeWithArgOptions(client, "GetStatusAsync", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return CoerceStandInfo(client, AwaitIfTask(res), "status", standId, timeoutSec);

            // As a last resort, if we cannot call a direct status method but can do "RequestStatusAsync" + "WaitTransactionAsync"
            if (TryInvokeWithArgOptions(client, "RequestStatusAsync", out res,
                    new object[] { standId },
                    new object[] { (object)standId }))
            {
                var receiptObj = AwaitIfTask(res);
                var txCode = GetStringProperty(receiptObj, "TransactionCode");
                if (!string.IsNullOrEmpty(txCode))
                {
                    var s = ResolveStandFromTransaction(client, txCode, timeoutSec);
                    if (s != null) return s;
                }
            }

            throw new MissingMethodException("AutostandClient", "CheckStatus/CheckStatusAsync/GetStatus/GetStatusAsync/RequestStatusAsync");
        }

        private static BatteryLevel? DoBattery(AutostandClient client, int standId, double timeoutSec)
        {
            object res;

            if (TryInvokeWithArgOptions(client, "CheckBattery", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return CoerceBatteryLevel(AwaitIfTask(res), client, standId, timeoutSec);

            if (TryInvokeWithArgOptions(client, "CheckBatteryAsync", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return CoerceBatteryLevel(AwaitIfTask(res), client, standId, timeoutSec);

            if (TryInvokeWithArgOptions(client, "GetBattery", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return CoerceBatteryLevel(AwaitIfTask(res), client, standId, timeoutSec);

            if (TryInvokeWithArgOptions(client, "GetBatteryAsync", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return CoerceBatteryLevel(AwaitIfTask(res), client, standId, timeoutSec);

            // Fallback: use status then extract battery
            var st = DoStatus(client, standId, timeoutSec);
            return st.Battery;
        }

        private static BatteryLevel? CoerceBatteryLevel(object obj, AutostandClient client, int standId, double timeoutSec)
        {
            if (obj == null) return null;

            if (obj is BatteryLevel b)
                return b;

            // Some variants return StandInfo (battery embedded)
            if (obj is StandInfo s)
                return s.Battery;

            var standProp = obj.GetType().GetProperty("Stand", BindingFlags.Instance | BindingFlags.Public);
            if (standProp != null)
            {
                var standObj = standProp.GetValue(obj);
                if (standObj is StandInfo s2)
                    return s2.Battery;
            }

            // If we get a transaction code, resolve and extract
            if (obj is string tx)
            {
                var s3 = ResolveStandFromTransaction(client, tx, timeoutSec);
                if (s3 != null) return s3.Battery;
            }

            throw new InvalidOperationException($"battery: unexpected return type: {obj.GetType().FullName}");
        }

        private static StandInfo CoerceStandInfo(AutostandClient client, object obj, string opName, int standId, double timeoutSec)
        {
            if (obj == null)
                throw new InvalidOperationException($"{opName}: operation returned null");

            if (obj is StandInfo s)
                return s;

            // Some APIs return TransactionResult-like objects with a Stand property.
            var standProp = obj.GetType().GetProperty("Stand", BindingFlags.Instance | BindingFlags.Public);
            if (standProp != null)
            {
                var standObj = standProp.GetValue(obj);
                if (standObj is StandInfo s2)
                    return s2;
            }

            // Some APIs return a transaction code string.
            if (obj is string tx)
            {
                var s3 = ResolveStandFromTransaction(client, tx, timeoutSec);
                if (s3 != null)
                    return s3;

                // If we cannot wait transaction, fall back to polling status.
                var s4 = PollStatusUntilTimeout(client, standId, timeoutSec);
                if (s4 != null)
                    return s4;

                throw new InvalidOperationException($"{opName}: returned transaction code but could not resolve stand info: {tx}");
            }

            throw new InvalidOperationException($"{opName}: unexpected return type: {obj.GetType().FullName}");
        }

        private static StandInfo ResolveStandFromTransaction(AutostandClient client, string transactionCode, double timeoutSec)
        {
            if (string.IsNullOrEmpty(transactionCode)) return null;

            object res;
            if (TryInvokeWithArgOptions(client, "WaitTransactionAsync", out res,
                    new object[] { transactionCode, timeoutSec },
                    new object[] { transactionCode }))
            {
                var txObj = AwaitIfTask(res);
                return ExtractStandFromObject(txObj);
            }

            if (TryInvokeWithArgOptions(client, "WaitTransaction", out res,
                    new object[] { transactionCode, timeoutSec },
                    new object[] { transactionCode }))
            {
                return ExtractStandFromObject(res);
            }

            // Try GetTransaction polling if WaitTransaction isn't available.
            var start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start).TotalSeconds < timeoutSec)
            {
                if (TryInvokeWithArgOptions(client, "GetTransactionAsync", out res,
                        new object[] { transactionCode }))
                {
                    var txObj = AwaitIfTask(res);
                    var s = ExtractStandFromObject(txObj);
                    if (s != null) return s;
                }
                else if (TryInvokeWithArgOptions(client, "GetTransaction", out res,
                        new object[] { transactionCode }))
                {
                    var s = ExtractStandFromObject(res);
                    if (s != null) return s;
                }
                else
                {
                    break;
                }

                System.Threading.Thread.Sleep(300);
            }

            return null;
        }

        private static StandInfo PollStatusUntilTimeout(AutostandClient client, int standId, double timeoutSec)
        {
            // Best-effort fallback: repeatedly call DoStatus() for up to timeoutSec.
            // If status API is also missing, this will throw and be handled by caller.
            var start = DateTime.UtcNow;
            StandInfo last = null;

            while ((DateTime.UtcNow - start).TotalSeconds < timeoutSec)
            {
                try
                {
                    last = DoStatus(client, standId, Math.Max(3.0, Math.Min(10.0, timeoutSec)));
                }
                catch
                {
                    // If status itself cannot be executed, abort.
                    return null;
                }

                // If we can read operate / standState, treat it as "good enough".
                if (last != null)
                    return last;

                System.Threading.Thread.Sleep(300);
            }

            return last;
        }

        private static StandInfo ExtractStandFromObject(object obj)
        {
            if (obj == null) return null;
            if (obj is StandInfo s) return s;

            var standProp = obj.GetType().GetProperty("Stand", BindingFlags.Instance | BindingFlags.Public);
            if (standProp == null) return null;

            var standObj = standProp.GetValue(obj);
            return standObj as StandInfo;
        }

        private static string GetStringProperty(object obj, string propertyName)
        {
            if (obj == null) return null;
            var p = obj.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (p == null) return null;
            return p.GetValue(obj) as string;
        }

        private static object AwaitIfTask(object maybeTask)
        {
            if (maybeTask == null) return null;

            if (maybeTask is Task t)
            {
                t.GetAwaiter().GetResult();

                var taskType = maybeTask.GetType();
                if (taskType.IsGenericType)
                {
                    var resultProp = taskType.GetProperty("Result", BindingFlags.Instance | BindingFlags.Public);
                    if (resultProp != null)
                        return resultProp.GetValue(maybeTask);
                }

                return null;
            }

            return maybeTask;
        }

        private static bool TryInvokeWithArgOptions(object target, string methodName, out object result, params object[][] argOptions)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrEmpty(methodName)) throw new ArgumentException("methodName is required");

            var t = target.GetType();
            var methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
                .ToArray();

            foreach (var args in argOptions)
            {
                foreach (var m in methods)
                {
                    var ps = m.GetParameters();
                    if (ps.Length != args.Length) continue;

                    if (!TryConvertArgs(args, ps, out var converted))
                        continue;

                    try
                    {
                        result = m.Invoke(target, converted);
                        return true;
                    }
                    catch (TargetInvocationException tie)
                    {
                        // Bubble up the real exception if the method was found and invoked.
                        throw tie.InnerException ?? tie;
                    }
                }
            }

            result = null;
            return false;
        }

        private static bool TryConvertArgs(object[] args, ParameterInfo[] ps, out object[] converted)
        {
            converted = new object[args.Length];

            for (int i = 0; i < args.Length; i++)
            {
                if (!TryConvertArg(args[i], ps[i].ParameterType, out var cv))
                {
                    converted = null;
                    return false;
                }
                converted[i] = cv;
            }

            return true;
        }

        private static bool TryConvertArg(object arg, Type paramType, out object converted)
        {
            converted = null;

            if (paramType == typeof(object))
            {
                converted = arg;
                return true;
            }

            var underlying = Nullable.GetUnderlyingType(paramType) ?? paramType;

            if (arg == null)
            {
                // null is OK for reference types or Nullable<T>
                if (!underlying.IsValueType || Nullable.GetUnderlyingType(paramType) != null)
                {
                    converted = null;
                    return true;
                }
                return false;
            }

            if (underlying.IsInstanceOfType(arg))
            {
                converted = arg;
                return true;
            }

            try
            {
                if (underlying.IsEnum)
                {
                    if (arg is string s)
                    {
                        converted = Enum.Parse(underlying, s, ignoreCase: true);
                        return true;
                    }

                    var enumUnderlying = Enum.GetUnderlyingType(underlying);
                    var num = Convert.ChangeType(arg, enumUnderlying, CultureInfo.InvariantCulture);
                    converted = Enum.ToObject(underlying, num);
                    return true;
                }

                // Convert between common primitives (int <-> long <-> double, string -> number, etc.)
                converted = Convert.ChangeType(arg, underlying, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
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
}
