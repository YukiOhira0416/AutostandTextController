using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AutostandTextController
{
    /// <summary>
    /// HttpClient DelegatingHandler that writes HTTP(S) response bodies to a text file.
    ///
    /// IMPORTANT:
    /// - We deliberately do NOT log request headers, because they may contain the API key.
    /// - Reading response content consumes the stream. We therefore buffer it and replace
    ///   response.Content so downstream code can still read it.
    /// </summary>
    internal sealed class HttpResponseLogHandler : DelegatingHandler
    {
        private static readonly object FileLock = new object();
        private readonly string _logPath;
        private readonly Func<string> _getOperationName;
        private readonly Func<bool> _getLogAll;
        private readonly int _maxLoggedBytes;

        public HttpResponseLogHandler(
            HttpMessageHandler innerHandler,
            string logPath,
            Func<string> getOperationName,
            Func<bool> getLogAll,
            int maxLoggedBytes = 1024 * 1024)
            : base(innerHandler)
        {
            if (string.IsNullOrWhiteSpace(logPath))
                throw new ArgumentException("logPath is required", nameof(logPath));

            _logPath = logPath;
            _getOperationName = getOperationName;
            _getLogAll = getLogAll;
            _maxLoggedBytes = Math.Max(1, maxLoggedBytes);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var op = _getOperationName != null ? _getOperationName() : null;
            var logAll = _getLogAll != null && _getLogAll();

            // Only log when an operation scope is active, unless configured to log everything.
            if (string.IsNullOrEmpty(op) && !logAll)
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            var startedAt = DateTime.Now;
            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                await LogResponseAsync(op, startedAt, request, response).ConfigureAwait(false);
                return response;
            }
            catch (Exception ex)
            {
                TryLogException(op, startedAt, request, ex);
                throw;
            }
        }

        private async Task LogResponseAsync(string op, DateTime startedAt, HttpRequestMessage request, HttpResponseMessage response)
        {
            byte[] bodyBytes = null;
            string bodyText = null;
            bool truncated = false;

            if (response != null && response.Content != null)
            {
                // Buffer body bytes so we can both log and keep response readable.
                var originalContent = response.Content;
                var allBytes = await originalContent.ReadAsByteArrayAsync().ConfigureAwait(false);

                if (allBytes != null && allBytes.Length > _maxLoggedBytes)
                {
                    truncated = true;
                    bodyBytes = new byte[_maxLoggedBytes];
                    Buffer.BlockCopy(allBytes, 0, bodyBytes, 0, _maxLoggedBytes);
                }
                else
                {
                    bodyBytes = allBytes;
                }

                bodyText = DecodeToText(bodyBytes, originalContent.Headers);

                // Replace response.Content so downstream code can read it again.
                var newContent = new ByteArrayContent(allBytes ?? Array.Empty<byte>());
                CopyContentHeaders(originalContent.Headers, newContent.Headers);
                response.Content = newContent;
            }

            var endedAt = DateTime.Now;
            var entry = BuildLogEntry(op, startedAt, endedAt, request, response, bodyText, bodyBytes != null ? bodyBytes.Length : 0, truncated);
            TryAppend(entry);
        }

        private static string BuildLogEntry(
            string op,
            DateTime startedAt,
            DateTime endedAt,
            HttpRequestMessage request,
            HttpResponseMessage response,
            string bodyText,
            int loggedBytes,
            bool truncated)
        {
            var sb = new StringBuilder();
            sb.AppendLine("============================================================");
            sb.AppendLine($"Time: {endedAt.ToString(\"yyyy/MM/dd HH:mm:ss.fff\", CultureInfo.InvariantCulture)}");
            if (!string.IsNullOrEmpty(op))
                sb.AppendLine($"Op: {op}");

            var ms = (endedAt - startedAt).TotalMilliseconds;
            sb.AppendLine($"Elapsed: {ms.ToString(\"0.###\", CultureInfo.InvariantCulture)} ms");

            if (request != null)
            {
                var url = request.RequestUri != null ? request.RequestUri.ToString() : "(null-uri)";
                sb.AppendLine($"> {request.Method} {url}");
            }
            else
            {
                sb.AppendLine("> (null-request)");
            }

            if (response != null)
            {
                sb.AppendLine($"< {(int)response.StatusCode} {response.ReasonPhrase}");
                if (response.Content != null)
                {
                    var ct = response.Content.Headers != null ? response.Content.Headers.ContentType : null;
                    if (ct != null)
                        sb.AppendLine($"Content-Type: {ct}");
                }

                // Response headers (safe, does not include API key).
                if (response.Headers != null && response.Headers.Any())
                {
                    sb.AppendLine("Response-Headers:");
                    foreach (var h in response.Headers)
                    {
                        sb.AppendLine($"  {h.Key}: {string.Join(", ", h.Value)}");
                    }
                }

                if (response.Content != null && response.Content.Headers != null && response.Content.Headers.Any())
                {
                    sb.AppendLine("Content-Headers:");
                    foreach (var h in response.Content.Headers)
                    {
                        sb.AppendLine($"  {h.Key}: {string.Join(", ", h.Value)}");
                    }
                }
            }
            else
            {
                sb.AppendLine("< (no response)");
            }

            sb.AppendLine("Body:");
            if (bodyText == null)
            {
                sb.AppendLine("(no body)");
            }
            else
            {
                if (truncated)
                    sb.AppendLine($"(truncated to {loggedBytes} bytes)");
                sb.AppendLine(bodyText);
            }

            sb.AppendLine();
            return sb.ToString();
        }

        private static string DecodeToText(byte[] bytes, HttpContentHeaders headers)
        {
            if (bytes == null || bytes.Length == 0)
                return string.Empty;

            Encoding enc = null;
            try
            {
                if (headers != null && headers.ContentType != null && !string.IsNullOrEmpty(headers.ContentType.CharSet))
                {
                    enc = Encoding.GetEncoding(headers.ContentType.CharSet);
                }
            }
            catch
            {
                enc = null;
            }

            if (enc == null)
                enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

            try
            {
                return enc.GetString(bytes);
            }
            catch
            {
                // Fallback: show as base64 when text decode fails unexpectedly.
                return Convert.ToBase64String(bytes);
            }
        }

        private static void CopyContentHeaders(HttpContentHeaders from, HttpContentHeaders to)
        {
            if (from == null || to == null) return;

            foreach (var h in from)
            {
                // Some headers are restricted; ignore failures.
                try
                {
                    to.TryAddWithoutValidation(h.Key, h.Value);
                }
                catch
                {
                    // ignore
                }
            }
        }

        private void TryAppend(string text)
        {
            try
            {
                if (text == null) return;

                var dir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                lock (FileLock)
                {
                    using (var fs = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                    {
                        sw.Write(text);
                        sw.Flush();
                    }
                }
            }
            catch
            {
                // Logging must never break control flow.
            }
        }

        private void TryLogException(string op, DateTime startedAt, HttpRequestMessage request, Exception ex)
        {
            try
            {
                var endedAt = DateTime.Now;
                var sb = new StringBuilder();
                sb.AppendLine("============================================================");
                sb.AppendLine($"Time: {endedAt.ToString(\"yyyy/MM/dd HH:mm:ss.fff\", CultureInfo.InvariantCulture)}");
                if (!string.IsNullOrEmpty(op))
                    sb.AppendLine($"Op: {op}");

                var ms = (endedAt - startedAt).TotalMilliseconds;
                sb.AppendLine($"Elapsed: {ms.ToString(\"0.###\", CultureInfo.InvariantCulture)} ms");

                if (request != null)
                {
                    var url = request.RequestUri != null ? request.RequestUri.ToString() : "(null-uri)";
                    sb.AppendLine($"> {request.Method} {url}");
                }

                sb.AppendLine("< EXCEPTION");
                sb.AppendLine($"{ex.GetType().FullName}: {ex.Message}");
                sb.AppendLine(ex.StackTrace ?? string.Empty);
                sb.AppendLine();

                TryAppend(sb.ToString());
            }
            catch
            {
                // ignore
            }
        }
    }
}
