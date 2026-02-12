using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
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

        // HTTP response dump (text)
        private const string ENV_HTTP_LOG = "AUTOSTAND_HTTP_LOG";
        private const string ENV_HTTP_LOG_PATH = "AUTOSTAND_HTTP_LOG_PATH";
        private const string ENV_HTTP_LOG_ALL = "AUTOSTAND_HTTP_LOG_ALL";

        private const string ENV_WRANGLER_TAIL = "AUTOSTAND_WRANGLER_TAIL";
        private const string ENV_WRANGLER_TAIL_APP = "AUTOSTAND_WRANGLER_TAIL_APP";
        private const string ENV_WRANGLER_TAIL_TIMEOUT_SEC = "AUTOSTAND_WRANGLER_TAIL_TIMEOUT_SEC";

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

                var envTail = Environment.GetEnvironmentVariable(ENV_WRANGLER_TAIL);
                var useWranglerTail = DetermineUseWranglerTail(envTail, baseUrl);

                var tailApp = FirstNonEmpty(Environment.GetEnvironmentVariable(ENV_WRANGLER_TAIL_APP), "autostand-webhook");

                double tailTimeoutSec = opTimeoutSec;
                var envTailTimeout = Environment.GetEnvironmentVariable(ENV_WRANGLER_TAIL_TIMEOUT_SEC);
                if (!string.IsNullOrEmpty(envTailTimeout))
                {
                    tailTimeoutSec = ParsePositiveDoubleOrThrow(envTailTimeout, "wrangler-tail-timeout-sec");
                }

                var cfg = new RuntimeConfig
                {
                    ApiKey = apiKey,
                    StandId = standId,
                    BaseUrl = baseUrl,
                    OpTimeoutSec = opTimeoutSec,
                    UseWranglerTail = useWranglerTail,
                    WranglerTailApp = tailApp,
                    WranglerTailTimeoutSec = tailTimeoutSec,
                    WranglerTailUrlFilter = BuildWranglerTailUrlFilter(baseUrl)
                };

                // HTTP response logging (default: enabled)
                var envHttpLog = Environment.GetEnvironmentVariable(ENV_HTTP_LOG);
                bool httpLogEnabled = true;
                if (!string.IsNullOrEmpty(envHttpLog) && TryParseBool(envHttpLog, out var bHttpLog))
                    httpLogEnabled = bHttpLog;

                var httpLogPathOpt = opt.HttpLogPath;
                if (!string.IsNullOrEmpty(httpLogPathOpt))
                {
                    var v = httpLogPathOpt.Trim();
                    if (string.Equals(v, "off", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(v, "none", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(v, "disable", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(v, "disabled", StringComparison.OrdinalIgnoreCase))
                    {
                        httpLogEnabled = false;
                    }
                }

                var httpLogPath = FirstNonEmpty(
                    (httpLogEnabled ? httpLogPathOpt : null),
                    Environment.GetEnvironmentVariable(ENV_HTTP_LOG_PATH),
                    "autostand_http_responses.txt");

                var envHttpLogAll = Environment.GetEnvironmentVariable(ENV_HTTP_LOG_ALL);
                bool httpLogAll = false;
                if (opt.HttpLogAll.HasValue)
                {
                    httpLogAll = opt.HttpLogAll.Value;
                }
                else if (!string.IsNullOrEmpty(envHttpLogAll) && TryParseBool(envHttpLogAll, out var bAll))
                {
                    httpLogAll = bAll;
                }

                cfg.HttpLogEnabled = httpLogEnabled;
                cfg.HttpLogPath = httpLogPath;
                cfg.HttpLogAll = httpLogAll;

                var httpTimeoutSec = Math.Max(15.0, Math.Min(120.0, cfg.OpTimeoutSec + 5.0));

                // Build HttpClient (optionally wrapped with a logging handler).
                HttpMessageHandler innerHandler = new HttpClientHandler();

                // NOTE: AutomaticDecompression is nice-to-have, but not available on every runtime.
                try
                {
                    var h = innerHandler as HttpClientHandler;
                    if (h != null)
                        h.AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate;
                }
                catch
                {
                    // ignore
                }

                if (cfg.HttpLogEnabled)
                {
                    innerHandler = new HttpResponseLogHandler(
                        innerHandler,
                        cfg.HttpLogPath,
                        () => HttpLogScope.CurrentOperationName,
                        () => cfg.HttpLogAll);
                }

                using (var http = new HttpClient(innerHandler))
                using (var client = new AutostandClient(
                    apiKey: cfg.ApiKey,
                    baseUrl: cfg.BaseUrl,
                    timeoutSec: httpTimeoutSec,
                    maxRetries: 0,
                    client: http))
                {
                    // AutostandClient sets User-Agent only when it creates HttpClient internally.
                    // When we inject HttpClient, we should keep the same behavior.
                    try
                    {
                        http.Timeout = TimeSpan.FromSeconds(httpTimeoutSec);
                        http.DefaultRequestHeaders.UserAgent.ParseAdd("autostand-client-csharp/1.0");
                    }
                    catch
                    {
                        // ignore
                    }

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
                    ExecuteUpDownOneShot(client, cfg, isUp: true);
                    break;

                case "down":
                    ExecuteUpDownOneShot(client, cfg, isUp: false);
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
            Console.WriteLine();

            if (cfg.HttpLogEnabled)
            {
                try
                {
                    Console.WriteLine($"HTTP response log: {System.IO.Path.GetFullPath(cfg.HttpLogPath)}");
                    Console.WriteLine("(scope: up/down/status; set env AUTOSTAND_HTTP_LOG_ALL=1 to log all requests)");
                    Console.WriteLine();
                }
                catch
                {
                    // ignore
                }
            }

            WranglerTailWatcher tail = null;
            if (cfg.UseWranglerTail)
            {
                tail = WranglerTailWatcher.TryStart(cfg.WranglerTailApp);
                if (tail == null)
                {
                    Console.WriteLine("wrangler tail: start failed (npx/wrangler not found?). Confirmation will be skipped.");
                    Console.WriteLine();
                }
                else
                {
                    Console.WriteLine($"wrangler tail: watching '{cfg.WranglerTailApp}' (filter: {cfg.WranglerTailUrlFilter ?? "none"})");
                    Console.WriteLine();
                }
            }

            using (tail)
            {
                while (true)
                {
                    PrintMenu();

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
                            PrintInteractiveHelp();
                            continue;
                        }

                        switch (cmd)
                        {
                            case "up":
                                ExecuteUpDownInteractive(client, cfg, tail, isUp: true);
                                break;

                            case "down":
                                ExecuteUpDownInteractive(client, cfg, tail, isUp: false);
                                break;

                            case "status":
                                ExecuteStatusInteractive(client, cfg);
                                break;

                            case "battery":
                                ExecuteBatteryInteractive(client, cfg);
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
        }

        private static void PrintMenu()
        {
            Console.WriteLine("Select: 1)up  2)down  3)status  4)battery   (help/h/? , quit/q)");
        }

        private static void PrintInteractiveHelp()
        {
            Console.WriteLine("Commands:");
            Console.WriteLine("  1 / up       : UP");
            Console.WriteLine("  2 / down     : DOWN");
            Console.WriteLine("  3 / status   : 状態確認");
            Console.WriteLine("  4 / battery  : バッテリー確認");
            Console.WriteLine("  quit         : 終了");
            Console.WriteLine();
        }

        private static void ExecuteUpDownInteractive(AutostandClient client, RuntimeConfig cfg, WranglerTailWatcher tail, bool isUp)
        {
            var op = isUp ? "up" : "down";

            var t0 = DateTime.Now;

            Console.WriteLine($"[{op}] sending...");

            object raw = null;
            Exception sendError = null;

            using (HttpLogScope.Begin(op))
            {
                try
                {
                    raw = isUp
                        ? SendUpNoWaitRaw(client, cfg.StandId, cfg.OpTimeoutSec)
                        : SendDownNoWaitRaw(client, cfg.StandId, cfg.OpTimeoutSec);

                    PrintRawStandResultOrAck(op, raw);
                }
                catch (Exception ex)
                {
                    sendError = ex;
                    Console.WriteLine($"[{op}] local error while sending: {ex.GetType().Name}: {ex.Message}");
                    Console.WriteLine($"[{op}] Will try to confirm via wrangler tail (if available).");
                }
            }

            if (tail != null)
            {
                Console.WriteLine($"[{op}] waiting for wrangler tail confirmation... (timeout {cfg.WranglerTailTimeoutSec:0.###}s)");

                var ev = tail.WaitForNextResultSince(t0, cfg.WranglerTailTimeoutSec, cfg.WranglerTailUrlFilter);
                PrintTailConfirmation(op, ev, cfg.WranglerTailTimeoutSec);

                // If we saw Ok on the tail, treat as success even if the local request timed out/canceled.
                if (ev != null && ev.IsOk)
                {
                    Console.WriteLine($"[{op}] confirmed by webhook log. (no retry)");
                    Console.WriteLine();
                    return;
                }
            }

            if (sendError != null)
                throw sendError;

            Console.WriteLine();
        }


        private static void ExecuteStatusInteractive(AutostandClient client, RuntimeConfig cfg)
        {
            // For status, users typically expect to see the latest device state printed.
            // Some Autostand.dll versions only provide a non-blocking status request
            // (returning a transaction code). DoStatus() handles both:
            // - direct status methods (CheckStatus/GetStatus)
            // - RequestStatus + transaction resolution
            using (HttpLogScope.Begin("status"))
            {
                PrintStand(DoStatus(client, cfg.StandId, cfg.OpTimeoutSec));
            }
        }

        private static void ExecuteBatteryInteractive(AutostandClient client, RuntimeConfig cfg)
        {
            var raw = SendBatteryRaw(client, cfg.StandId, cfg.OpTimeoutSec);
            PrintRawBatteryResultOrAck(raw);
        }



        private static void ExecuteUpDownOneShot(AutostandClient client, RuntimeConfig cfg, bool isUp)
        {
            var op = isUp ? "up" : "down";

            if (!cfg.UseWranglerTail)
            {
                // Original behavior: try to return StandInfo by resolving transaction/status.
                using (HttpLogScope.Begin(op))
                {
                    PrintStand(isUp
                        ? DoUp(client, cfg.StandId, cfg.OpTimeoutSec)
                        : DoDown(client, cfg.StandId, cfg.OpTimeoutSec));
                }
                return;
            }

            using (var tail = WranglerTailWatcher.TryStart(cfg.WranglerTailApp))
            {
                var t0 = DateTime.Now;

                object raw = null;
                Exception sendError = null;

                using (HttpLogScope.Begin(op))
                {
                    try
                    {
                        raw = isUp
                            ? SendUpNoWaitRaw(client, cfg.StandId, cfg.OpTimeoutSec)
                            : SendDownNoWaitRaw(client, cfg.StandId, cfg.OpTimeoutSec);

                        PrintRawStandResultOrAck(op, raw);
                    }
                    catch (Exception ex)
                    {
                        sendError = ex;
                        Console.WriteLine($"[{op}] local error while sending: {ex.GetType().Name}: {ex.Message}");
                        Console.WriteLine($"[{op}] Will try to confirm via wrangler tail (if available).");
                    }
                }

                if (tail == null)
                {
                    // If tail is unavailable, do NOT retry here (to avoid duplicate operations).
                    if (sendError != null)
                        throw sendError;
                    return;
                }

                Console.WriteLine($"[{op}] waiting for wrangler tail confirmation... (timeout {cfg.WranglerTailTimeoutSec:0.###}s)");
                var ev = tail.WaitForNextResultSince(t0, cfg.WranglerTailTimeoutSec, cfg.WranglerTailUrlFilter);
                PrintTailConfirmation(op, ev, cfg.WranglerTailTimeoutSec);

                if (ev != null && ev.IsOk)
                    return;

                if (sendError != null)
                    throw sendError;
            }
        }


        private static void PrintTailConfirmation(string op, WranglerTailEvent ev, double timeoutSec)
        {
            if (ev == null)
            {
                Console.WriteLine($"[{op}] wrangler tail: timeout (no confirmation within {timeoutSec:0.###}s)");
                return;
            }

            var t = ev.LoggedAt ?? ev.ReceivedAt;

            if (ev.IsOk)
            {
                Console.WriteLine($"[{op}] wrangler tail: Ok @ {t.ToString("yyyy/M/d HH:mm:ss", CultureInfo.InvariantCulture)}");
                return;
            }

            if (ev.IsError)
            {
                Console.WriteLine($"[{op}] wrangler tail: Error @ {t.ToString("yyyy/M/d HH:mm:ss", CultureInfo.InvariantCulture)}");
                Console.WriteLine(ev.RawLine);
                return;
            }

            Console.WriteLine($"[{op}] wrangler tail: {ev.StatusText}");
            Console.WriteLine(ev.RawLine);
        }

        private static void PrintRawStandResultOrAck(string opName, object raw)
        {
            if (raw == null)
            {
                Console.WriteLine($"[{opName}] request sent. (no response body)");
                return;
            }

            if (raw is StandInfo s)
            {
                PrintStand(s);
                return;
            }

            // Some APIs return a TransactionResult-like object with Stand property.
            var s2 = ExtractStandFromObject(raw);
            if (s2 != null)
            {
                PrintStand(s2);
                return;
            }

            // Some APIs return a transaction code string.
            if (raw is string tx)
            {
                Console.WriteLine($"[{opName}] request sent. (transaction: {tx})");
                return;
            }

            Console.WriteLine($"[{opName}] request sent. (response type: {raw.GetType().FullName})");
        }

        private static void PrintRawBatteryResultOrAck(object raw)
        {
            if (raw == null)
            {
                Console.WriteLine("battery: request sent. (no response body)");
                return;
            }

            if (raw is BatteryLevel b)
            {
                PrintBattery(b);
                return;
            }

            if (raw is StandInfo s)
            {
                PrintBattery(s.Battery);
                return;
            }

            var s2 = ExtractStandFromObject(raw);
            if (s2 != null)
            {
                PrintBattery(s2.Battery);
                return;
            }

            if (raw is string tx)
            {
                Console.WriteLine($"battery: request sent. (transaction: {tx})");
                return;
            }

            Console.WriteLine($"battery: request sent. (response type: {raw.GetType().FullName})");
        }

        private static object SendUpRaw(AutostandClient client, int standId, double timeoutSec)
        {
            object res;

            if (TryInvokeWithArgOptions(client, "Up", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return AwaitIfTask(res);

            if (TryInvokeWithArgOptions(client, "UpAsync", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return AwaitIfTask(res);

            if (TryInvokeWithArgOptions(client, "Open", out res,
                    new object[] { standId, true, timeoutSec },
                    new object[] { standId, true },
                    new object[] { standId }))
                return AwaitIfTask(res);

            if (TryInvokeWithArgOptions(client, "OpenAsync", out res,
                    new object[] { standId, true, timeoutSec },
                    new object[] { standId, true },
                    new object[] { standId }))
                return AwaitIfTask(res);

            throw new MissingMethodException("AutostandClient", "Up/UpAsync/Open/OpenAsync");
        }

        private static object SendDownRaw(AutostandClient client, int standId, double timeoutSec)
        {
            object res;

            if (TryInvokeWithArgOptions(client, "Down", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return AwaitIfTask(res);

            if (TryInvokeWithArgOptions(client, "DownAsync", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return AwaitIfTask(res);

            if (TryInvokeWithArgOptions(client, "Close", out res,
                    new object[] { standId, true, timeoutSec },
                    new object[] { standId, true },
                    new object[] { standId }))
                return AwaitIfTask(res);

            if (TryInvokeWithArgOptions(client, "CloseAsync", out res,
                    new object[] { standId, true, timeoutSec },
                    new object[] { standId, true },
                    new object[] { standId }))
                return AwaitIfTask(res);

            throw new MissingMethodException("AutostandClient", "Down/DownAsync/Close/CloseAsync");
        }


        private static object SendUpNoWaitRaw(AutostandClient client, int standId, double timeoutSec)
        {
            object res;

            // Prefer no-wait variants to avoid blocking and to reduce timeout/cancel issues.
            if (TryInvokeWithArgOptions(client, "Open", out res,
                    new object[] { standId, false, timeoutSec },
                    new object[] { standId, false },
                    new object[] { standId }))
                return AwaitIfTask(res);

            if (TryInvokeWithArgOptions(client, "OpenAsync", out res,
                    new object[] { standId, false, timeoutSec },
                    new object[] { standId, false },
                    new object[] { standId }))
                return AwaitIfTask(res);

            object opRes;
            if (TryRequestOperationNoWait(client, standId, isUp: true, out opRes))
                return opRes;

            // Fallback to the waiting call (may block on some library versions).
            return SendUpRaw(client, standId, timeoutSec);
        }

        private static object SendDownNoWaitRaw(AutostandClient client, int standId, double timeoutSec)
        {
            object res;

            if (TryInvokeWithArgOptions(client, "Close", out res,
                    new object[] { standId, false, timeoutSec },
                    new object[] { standId, false },
                    new object[] { standId }))
                return AwaitIfTask(res);

            if (TryInvokeWithArgOptions(client, "CloseAsync", out res,
                    new object[] { standId, false, timeoutSec },
                    new object[] { standId, false },
                    new object[] { standId }))
                return AwaitIfTask(res);

            object opRes;
            if (TryRequestOperationNoWait(client, standId, isUp: false, out opRes))
                return opRes;

            return SendDownRaw(client, standId, timeoutSec);
        }

        private static bool TryRequestOperationNoWait(AutostandClient client, int standId, bool isUp, out object result)
        {
            result = null;
            object res;

            // Newer Autostand.dll exposes RequestOperationAsync(standId, Direction).
            var dirType = client.GetType().Assembly.GetType("Autostand.Direction");
            if (dirType == null || !dirType.IsEnum)
                return false;

            object dirValue;
            try
            {
                dirValue = Enum.Parse(dirType, isUp ? "UP" : "DOWN", ignoreCase: true);
            }
            catch
            {
                return false;
            }

            if (TryInvokeWithArgOptions(client, "RequestOperationAsync", out res,
                    new object[] { standId, dirValue },
                    new object[] { (object)standId, dirValue }))
            {
                var receiptObj = AwaitIfTask(res);
                var txCode = GetStringProperty(receiptObj, "TransactionCode");
                if (!string.IsNullOrEmpty(txCode))
                {
                    result = txCode;
                    return true;
                }

                result = receiptObj;
                return true;
            }

            if (TryInvokeWithArgOptions(client, "RequestOperation", out res,
                    new object[] { standId, dirValue },
                    new object[] { (object)standId, dirValue }))
            {
                var receiptObj = res;
                var txCode = GetStringProperty(receiptObj, "TransactionCode");
                if (!string.IsNullOrEmpty(txCode))
                {
                    result = txCode;
                    return true;
                }

                result = receiptObj;
                return true;
            }

            return false;
        }

        private static object SendStatusRaw(AutostandClient client, int standId, double timeoutSec)
        {
            object res;

            if (TryInvokeWithArgOptions(client, "CheckStatus", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return AwaitIfTask(res);

            if (TryInvokeWithArgOptions(client, "CheckStatusAsync", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return AwaitIfTask(res);

            if (TryInvokeWithArgOptions(client, "GetStatus", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return AwaitIfTask(res);

            if (TryInvokeWithArgOptions(client, "GetStatusAsync", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return AwaitIfTask(res);

            if (TryInvokeWithArgOptions(client, "RequestStatusAsync", out res,
                    new object[] { standId },
                    new object[] { (object)standId }))
            {
                var receiptObj = AwaitIfTask(res);
                var txCode = GetStringProperty(receiptObj, "TransactionCode");
                if (!string.IsNullOrEmpty(txCode))
                    return txCode;
                return receiptObj;
            }

            throw new MissingMethodException("AutostandClient", "CheckStatus/CheckStatusAsync/GetStatus/GetStatusAsync/RequestStatusAsync");
        }

        private static object SendBatteryRaw(AutostandClient client, int standId, double timeoutSec)
        {
            object res;

            if (TryInvokeWithArgOptions(client, "CheckBattery", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return AwaitIfTask(res);

            if (TryInvokeWithArgOptions(client, "CheckBatteryAsync", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return AwaitIfTask(res);

            if (TryInvokeWithArgOptions(client, "GetBattery", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return AwaitIfTask(res);

            if (TryInvokeWithArgOptions(client, "GetBatteryAsync", out res,
                    new object[] { standId, timeoutSec },
                    new object[] { standId }))
                return AwaitIfTask(res);

            // Fallback: status result may contain battery info.
            return SendStatusRaw(client, standId, timeoutSec);
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

            if (c == "1") return "up";
            if (c == "2") return "down";
            if (c == "3") return "status";
            if (c == "4") return "battery";

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


        private static bool DetermineUseWranglerTail(string envValue, string baseUrl)
        {
            if (string.IsNullOrEmpty(envValue))
                return IsWorkersDevUrl(baseUrl);

            var v = envValue.Trim();

            if (string.Equals(v, "auto", StringComparison.OrdinalIgnoreCase))
                return IsWorkersDevUrl(baseUrl);

            if (TryParseBool(v, out var b))
                return b;

            // Unknown -> fallback to auto
            return IsWorkersDevUrl(baseUrl);
        }

        private static bool TryParseBool(string s, out bool value)
        {
            value = false;
            if (s == null) return false;

            var v = s.Trim().ToLowerInvariant();

            if (v == "1" || v == "true" || v == "yes" || v == "y" || v == "on" || v == "enable" || v == "enabled")
            {
                value = true;
                return true;
            }

            if (v == "0" || v == "false" || v == "no" || v == "n" || v == "off" || v == "disable" || v == "disabled")
            {
                value = false;
                return true;
            }

            return false;
        }

        private static bool IsWorkersDevUrl(string baseUrl)
        {
            if (string.IsNullOrEmpty(baseUrl)) return false;

            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var u))
            {
                if (!string.IsNullOrEmpty(u.Host))
                {
                    if (u.Host.EndsWith(".workers.dev", StringComparison.OrdinalIgnoreCase))
                        return true;

                    if (u.Host.IndexOf("workers.dev", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }

            return baseUrl.IndexOf("workers.dev", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string BuildWranglerTailUrlFilter(string baseUrl)
        {
            if (string.IsNullOrEmpty(baseUrl)) return null;

            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var u))
            {
                if (!string.IsNullOrEmpty(u.Host))
                    return u.Host;
            }

            // fallback: best-effort extract "host[:port]" part
            var idx = baseUrl.IndexOf("://", StringComparison.Ordinal);
            var s = idx >= 0 ? baseUrl.Substring(idx + 3) : baseUrl;
            var slash = s.IndexOf('/');
            if (slash >= 0) s = s.Substring(0, slash);
            if (s.Length == 0) return null;

            return s;
        }

        private sealed class WranglerTailEvent
        {
            public string Method;
            public string Url;
            public string StatusText;
            public bool IsOk;
            public bool IsError;
            public DateTime ReceivedAt;
            public DateTime? LoggedAt;
            public string RawLine;
        }

        private sealed class WranglerTailWatcher : IDisposable
        {
            private readonly Process _proc;
            private readonly object _gate = new object();

            private TaskCompletionSource<WranglerTailEvent> _waiter;
            private DateTime _waiterSince;
            private string _waiterUrlFilter;

            private WranglerTailWatcher(Process proc)
            {
                _proc = proc;
            }

            public static WranglerTailWatcher TryStart(string appName)
            {
                if (string.IsNullOrEmpty(appName))
                    appName = "autostand-webhook";

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "npx",
                        Arguments = $"wrangler tail {EscapeArg(appName)}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                    if (!p.Start())
                        return null;

                    var w = new WranglerTailWatcher(p);

                    p.OutputDataReceived += w.OnOutput;
                    p.ErrorDataReceived += w.OnError;

                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    return w;
                }
                catch
                {
                    return null;
                }
            }

            public WranglerTailEvent WaitForNextResultSince(DateTime since, double timeoutSec, string urlContains)
            {
                if (_proc == null || _proc.HasExited)
                    return null;

                if (timeoutSec <= 0)
                    timeoutSec = 30.0;

                var tcs = new TaskCompletionSource<WranglerTailEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

                lock (_gate)
                {
                    _waiter = tcs;
                    _waiterSince = since;
                    _waiterUrlFilter = urlContains;
                }

                try
                {
                    if (tcs.Task.Wait(TimeSpan.FromSeconds(timeoutSec)))
                        return tcs.Task.Result;

                    return null;
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_waiter, tcs))
                        {
                            _waiter = null;
                            _waiterUrlFilter = null;
                        }
                    }
                }
            }

            private void OnOutput(object sender, DataReceivedEventArgs e)
            {
                if (e == null || e.Data == null)
                    return;

                if (!TryParseTailLine(e.Data, out var ev))
                    return;

                TaskCompletionSource<WranglerTailEvent> waiter;
                DateTime since;
                string urlFilter;

                lock (_gate)
                {
                    waiter = _waiter;
                    since = _waiterSince;
                    urlFilter = _waiterUrlFilter;
                }

                if (waiter == null)
                    return;

                // Filter by "since" using receive time (most robust across wrangler versions).
                if (ev.ReceivedAt < since)
                    return;

                if (!string.IsNullOrEmpty(urlFilter))
                {
                    if (string.IsNullOrEmpty(ev.Url) || ev.Url.IndexOf(urlFilter, StringComparison.OrdinalIgnoreCase) < 0)
                        return;
                }

                if (!ev.IsOk && !ev.IsError)
                    return;

                lock (_gate)
                {
                    if (_waiter == waiter)
                    {
                        _waiter = null;
                        _waiterUrlFilter = null;
                    }
                }

                waiter.TrySetResult(ev);
            }

            private void OnError(object sender, DataReceivedEventArgs e)
            {
                // Keep silent by default. Errors (e.g., wrangler not found) are handled via TryStart().
            }

            public void Dispose()
            {
                try
                {
                    if (_proc != null && !_proc.HasExited)
                    {
                        try
                        {
                            _proc.Kill();
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }
                catch
                {
                    // ignore
                }

                try
                {
                    _proc?.Dispose();
                }
                catch
                {
                    // ignore
                }

                lock (_gate)
                {
                    _waiter = null;
                    _waiterUrlFilter = null;
                }
            }

            private static string EscapeArg(string s)
            {
                if (string.IsNullOrEmpty(s)) return s;

                if (s.IndexOf(' ') < 0 && s.IndexOf('"') < 0)
                    return s;

                return "\"" + s.Replace("\"", "\\\"") + "\"";
            }

            private static bool TryParseTailLine(string line, out WranglerTailEvent ev)
            {
                ev = null;
                if (string.IsNullOrEmpty(line)) return false;

                // Example:
                //   POST https://autostand-webhook.example.workers.dev/api/autostand-webhook - Ok @ 2026/2/9 14:10:39
                // We focus on extracting "method", "url", and "- Ok/- Error" + "@ timestamp" if present.

                var firstSpace = line.IndexOf(' ');
                if (firstSpace <= 0) return false;

                var method = line.Substring(0, firstSpace).Trim();
                if (method.Length == 0) return false;

                var rest = line.Substring(firstSpace + 1).Trim();
                if (rest.Length == 0) return false;

                var secondSpace = rest.IndexOf(' ');
                if (secondSpace <= 0) return false;

                var url = rest.Substring(0, secondSpace).Trim();
                if (url.Length == 0) return false;

                var afterUrl = rest.Substring(secondSpace + 1);

                var dashIdx = afterUrl.IndexOf('-');
                if (dashIdx < 0) return false;

                var statusPart = afterUrl.Substring(dashIdx + 1).Trim();

                string statusText;
                string tsText = null;

                var atIdx = statusPart.LastIndexOf('@');
                if (atIdx >= 0)
                {
                    statusText = statusPart.Substring(0, atIdx).Trim();
                    tsText = statusPart.Substring(atIdx + 1).Trim();
                }
                else
                {
                    statusText = statusPart.Trim();
                }

                var isOk = statusText.StartsWith("Ok", StringComparison.OrdinalIgnoreCase) ||
                           statusText.StartsWith("OK", StringComparison.OrdinalIgnoreCase);

                var isErr = statusText.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ||
                            statusText.StartsWith("Err", StringComparison.OrdinalIgnoreCase) ||
                            statusText.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0;

                DateTime? loggedAt = null;

                if (!string.IsNullOrEmpty(tsText))
                {
                    var patterns = new[]
                    {
                        "yyyy/M/d H:mm:ss",
                        "yyyy/M/d HH:mm:ss",
                        "yyyy/M/d H:mm:ss.FFF",
                        "yyyy/M/d HH:mm:ss.FFF",
                        "yyyy-MM-dd H:mm:ss",
                        "yyyy-MM-dd HH:mm:ss",
                        "yyyy-MM-dd H:mm:ss.FFF",
                        "yyyy-MM-dd HH:mm:ss.FFF"
                    };

                    if (DateTime.TryParseExact(tsText, patterns, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
                    {
                        loggedAt = dt;
                    }
                    else if (DateTime.TryParse(tsText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt))
                    {
                        loggedAt = dt;
                    }
                }

                ev = new WranglerTailEvent
                {
                    Method = method,
                    Url = url,
                    StatusText = statusText,
                    IsOk = isOk,
                    IsError = isErr,
                    LoggedAt = loggedAt,
                    ReceivedAt = DateTime.Now,
                    RawLine = line
                };

                return true;
            }
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
            Console.WriteLine("  --http-log <path|off>  (or env AUTOSTAND_HTTP_LOG_PATH, default: autostand_http_responses.txt)");
            Console.WriteLine("                        (disable with --http-log off or env AUTOSTAND_HTTP_LOG=0)");
            Console.WriteLine("  --http-log-all         (or env AUTOSTAND_HTTP_LOG_ALL=1)");
            Console.WriteLine("  --help");
            Console.WriteLine();
            Console.WriteLine("Wrangler tail confirmation (optional):");
            Console.WriteLine("  env AUTOSTAND_WRANGLER_TAIL=1|0|auto   (default: auto when base-url contains workers.dev)");
            Console.WriteLine("  env AUTOSTAND_WRANGLER_TAIL_APP=<name> (default: autostand-webhook)");
            Console.WriteLine("  env AUTOSTAND_WRANGLER_TAIL_TIMEOUT_SEC=<sec> (default: op-timeout-sec)");
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

            public bool HttpLogEnabled;
            public string HttpLogPath;
            public bool HttpLogAll;

            public bool UseWranglerTail;
            public string WranglerTailApp;
            public double WranglerTailTimeoutSec;
            public string WranglerTailUrlFilter;
        }

        private sealed class Options
        {
            public bool ShowHelp;
            public string Command;
            public string ApiKey;
            public string StandId;
            public string BaseUrl;
            public string OpTimeoutSec;
            public string HttpLogPath;
            public bool? HttpLogAll;

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

                    if (a == "--http-log")
                    {
                        o.HttpLogPath = NeedValue(list, ref i, "--http-log");
                        continue;
                    }

                    if (a == "--http-log-all")
                    {
                        o.HttpLogAll = true;
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
