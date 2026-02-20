using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AutostandTextController
{
    /// <summary>
    /// Simple INI file reader for configuration settings.
    /// </summary>
    internal class IniFileReader
    {
        private readonly Dictionary<string, Dictionary<string, string>> _sections;

        public IniFileReader()
        {
            _sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Load INI file from the specified path.
        /// Returns true if the file was loaded successfully.
        /// </summary>
        public bool Load(string filePath)
        {
            if (!File.Exists(filePath))
                return false;

            try
            {
                var lines = File.ReadAllLines(filePath, Encoding.UTF8);
                string currentSection = "";

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();

                    // Skip empty lines and comments
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(";") || trimmed.StartsWith("#"))
                        continue;

                    // Section header
                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        currentSection = trimmed.Substring(1, trimmed.Length - 2).Trim();
                        if (!_sections.ContainsKey(currentSection))
                        {
                            _sections[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        }
                        continue;
                    }

                    // Key=Value pair
                    var separatorIndex = trimmed.IndexOf('=');
                    if (separatorIndex > 0)
                    {
                        var key = trimmed.Substring(0, separatorIndex).Trim();
                        var value = trimmed.Substring(separatorIndex + 1).Trim();

                        // Remove quotes if present
                        if (value.Length >= 2 && value.StartsWith("\"") && value.EndsWith("\""))
                        {
                            value = value.Substring(1, value.Length - 2);
                        }

                        if (!_sections.ContainsKey(currentSection))
                        {
                            _sections[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        }

                        _sections[currentSection][key] = value;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Get a value from the INI file.
        /// Returns null if the section or key doesn't exist.
        /// </summary>
        public string GetValue(string section, string key)
        {
            if (_sections.TryGetValue(section, out var sectionData))
            {
                if (sectionData.TryGetValue(key, out var value))
                {
                    return value;
                }
            }
            return null;
        }

        /// <summary>
        /// Check if a section exists.
        /// </summary>
        public bool HasSection(string section)
        {
            return _sections.ContainsKey(section);
        }

        /// <summary>
        /// Generate a default INI file with all available settings.
        /// </summary>
        public static void GenerateDefaultIniFile(string filePath)
        {
            var content = @"; AutoStand Text Controller Configuration File
; 
; Priority: Command-line arguments > Environment variables > INI file > Default values
; 
; Note: You can use environment variables in your system instead of this file.
; This file is useful when deploying to different PCs.

[API]
; API Key for AutoStand service (required)
; Environment variable: AUTOSTAND_API_KEY
ApiKey=

; Stand ID (required)
; Environment variable: AUTOSTAND_STAND_ID
StandId=

; API Base URL (optional, default: https://api-autostand-prod.azurewebsites.net/v1/)
; Environment variable: AUTOSTAND_BASE_URL
BaseUrl=https://api-autostand-prod.azurewebsites.net/v1/

[Timeouts]
; Operation timeout in seconds (default: 30.0)
; Environment variable: AUTOSTAND_OP_TIMEOUT_SEC
OperationTimeoutSec=30.0

; Wrangler tail confirmation timeout in seconds (default: same as OperationTimeoutSec)
; Environment variable: AUTOSTAND_WRANGLER_TAIL_TIMEOUT_SEC
WranglerTailTimeoutSec=30.0

[Logging]
; Enable HTTP response logging (default: true)
; Environment variable: AUTOSTAND_HTTP_LOG
; Values: true, false, 1, 0, yes, no, on, off
HttpLogEnabled=true

; HTTP log file path (default: autostand_http_responses.txt)
; Environment variable: AUTOSTAND_HTTP_LOG_PATH
; Set to 'off' or 'none' to disable logging
HttpLogPath=autostand_http_responses.txt

; Log all HTTP requests (default: false, only logs up/down/status operations)
; Environment variable: AUTOSTAND_HTTP_LOG_ALL
; Values: true, false, 1, 0, yes, no, on, off
HttpLogAll=false

[Wrangler]
; Enable wrangler tail confirmation (default: auto - enabled for workers.dev URLs)
; Environment variable: AUTOSTAND_WRANGLER_TAIL
; Values: true, false, 1, 0, yes, no, on, off, auto
WranglerTailEnabled=auto

; Wrangler tail application name (default: autostand-webhook)
; Environment variable: AUTOSTAND_WRANGLER_TAIL_APP
WranglerTailApp=autostand-webhook

[Library]
; Path to Autostand.dll (for documentation purposes)
; Note: The DLL should be in the same directory as the executable or in a referenced location.
; This setting is not used for dynamic loading but serves as documentation.
AutostandDllPath=Autostand.dll

[Deployment]
; Deployment notes (optional, for documentation)
; Use this section to document deployment-specific information
Notes=
";

            try
            {
                File.WriteAllText(filePath, content, Encoding.UTF8);
            }
            catch
            {
                // Ignore write errors
            }
        }
    }
}
