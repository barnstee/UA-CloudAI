namespace Opc.Ua.Cloud.AI.Configuration
{
    /// <summary>
    /// All configuration for the MCP server, read from environment variables so the
    /// container can be configured without rebuilding or mounting a config file.
    ///
    /// Every setting has a working default for the reference deployment described in
    /// the Cloud Initiative Reference Solution, so in the common case only the
    /// credentials need to be supplied.
    /// </summary>
    public sealed class ServerSettings
    {
        /// <summary>
        /// Transport to expose. "http" (default) serves Streamable HTTP on <see cref="Port"/>
        /// and is what you want in Docker and for MCP Inspector. "stdio" runs the server as a
        /// child process speaking over stdin/stdout, which is what Claude Desktop launches
        /// directly.
        /// </summary>
        public string Transport { get; init; } = "http";

        public int Port { get; init; } = 5000;

        // -----------------------------------------------------------------
        // Downstream: I3X server
        // -----------------------------------------------------------------

        public string I3XBaseUrl { get; init; } = "http://i3x4influx.cloud.svc.cluster.local:8084";

        public string? I3XUsername { get; init; }

        public string? I3XPassword { get; init; }

        // -----------------------------------------------------------------
        // Downstream: UA Cloud Action OPC UA Web API
        // -----------------------------------------------------------------

        public string OpcUaWebApiBaseUrl { get; init; } = "http://ua-cloudaction.cloud.svc.cluster.local:8082";

        public string? OpcUaWebApiUsername { get; init; }

        public string? OpcUaWebApiPassword { get; init; }

        // -----------------------------------------------------------------
        // Inbound: clients authenticating TO this MCP server
        // -----------------------------------------------------------------

        /// <summary>
        /// When set, every request to the MCP endpoint must present these HTTP Basic
        /// credentials. Leaving them unset disables inbound authentication, which is only
        /// appropriate for stdio (where the transport is a private pipe to the parent
        /// process) or a local test run.
        /// </summary>
        public string? McpUsername { get; init; }

        public string? McpPassword { get; init; }

        // -----------------------------------------------------------------
        // Inbound TLS: serving HTTPS
        // -----------------------------------------------------------------

        /// <summary>
        /// Path to a PKCS#12 (.pfx) certificate file. When set, the MCP endpoint is served
        /// over HTTPS instead of plain HTTP.
        ///
        /// This matters because Basic auth sends reversible credentials on EVERY request:
        /// over plain HTTP anyone on the network path can replay them. Basic auth over TLS
        /// is a perfectly sound combination - it is the unencrypted transport, not the
        /// scheme itself, that is the weakness.
        /// </summary>
        public string? TlsCertPath { get; init; }

        /// <summary>Password protecting <see cref="TlsCertPath"/>, if it has one.</summary>
        public string? TlsCertPassword { get; init; }

        /// <summary>
        /// PEM certificate path, as an alternative to PKCS#12. Pairs with
        /// <see cref="TlsKeyPath"/>. This is the shape Kubernetes TLS secrets mount
        /// (tls.crt / tls.key), so it avoids a conversion step in-cluster.
        /// </summary>
        public string? TlsPemPath { get; init; }

        /// <summary>Private key path accompanying <see cref="TlsPemPath"/>.</summary>
        public string? TlsKeyPath { get; init; }

        // -----------------------------------------------------------------
        // Outbound TLS: talking to the backends
        // -----------------------------------------------------------------

        /// <summary>
        /// When true, certificate validation for the downstream I3X and OPC UA Web API calls
        /// is skipped.
        ///
        /// This exists because the reference solution issues its own certificates, which a
        /// container will not trust by default. It disables protection against an active
        /// man-in-the-middle, so prefer <see cref="BackendCaCertPath"/>, which keeps
        /// validation switched on.
        /// </summary>
        public bool AllowUntrustedBackendCerts { get; init; }

        /// <summary>
        /// Path to a PEM CA certificate to trust when calling the backends. This is the
        /// correct way to accept a privately issued backend certificate: the chain is still
        /// verified, just against this CA as well as the system roots.
        /// </summary>
        public string? BackendCaCertPath { get; init; }

        // -----------------------------------------------------------------
        // Behaviour
        // -----------------------------------------------------------------

        public int HttpTimeoutSeconds { get; init; } = 100;

        /// <summary>
        /// Upper bound on how many items a tool will return in one response. Tool results are
        /// fed straight into a model's context window, so an unbounded browse of a large
        /// address space would crowd out everything else. Results above this are truncated
        /// with an explicit note so the model knows the list was cut rather than empty.
        /// </summary>
        public int MaxResults { get; init; } = 200;

        public static ServerSettings FromEnvironment()
        {
            return new ServerSettings
            {
                Transport = Get("MCP_TRANSPORT", "http")!.Trim().ToLowerInvariant(),
                Port = GetInt("MCP_PORT", 5000),

                I3XBaseUrl = Get("I3X_BASE_URL", "http://i3x4influx.cloud.svc.cluster.local:8084")!.TrimEnd('/'),
                I3XUsername = Get("I3X_USERNAME", Get("IOT_USERNAME", null)),
                I3XPassword = Get("I3X_PASSWORD", Get("IOT_PASSWORD", null)),

                OpcUaWebApiBaseUrl = Get("OPCUA_WEBAPI_BASE_URL", "http://ua-cloudaction.cloud.svc.cluster.local:8082")!.TrimEnd('/'),
                OpcUaWebApiUsername = Get("OPCUA_WEBAPI_USERNAME", Get("IOT_USERNAME", null)),
                OpcUaWebApiPassword = Get("OPCUA_WEBAPI_PASSWORD", Get("IOT_PASSWORD", null)),

                McpUsername = Get("MCP_USERNAME", null),
                McpPassword = Get("MCP_PASSWORD", null),

                TlsCertPath = Get("MCP_TLS_CERT_PATH", null),
                TlsCertPassword = Get("MCP_TLS_CERT_PASSWORD", null),
                TlsPemPath = Get("MCP_TLS_PEM_PATH", null),
                TlsKeyPath = Get("MCP_TLS_KEY_PATH", null),

                AllowUntrustedBackendCerts = GetBool("ALLOW_UNTRUSTED_BACKEND_CERTS", false),
                BackendCaCertPath = Get("BACKEND_CA_CERT_PATH", null),

                HttpTimeoutSeconds = GetInt("HTTP_TIMEOUT_SECONDS", 100),
                MaxResults = GetInt("MCP_MAX_RESULTS", 200)
            };
        }

        public bool InboundAuthConfigured =>
            !string.IsNullOrEmpty(McpUsername) && !string.IsNullOrEmpty(McpPassword);

        public bool UseStdio =>
            string.Equals(Transport, "stdio", StringComparison.OrdinalIgnoreCase);

        /// <summary>True when a certificate has been supplied in either supported form.</summary>
        public bool TlsConfigured =>
            !string.IsNullOrEmpty(TlsCertPath) ||
            (!string.IsNullOrEmpty(TlsPemPath) && !string.IsNullOrEmpty(TlsKeyPath));

        /// <summary>Scheme this server is actually serving, for logging and diagnostics.</summary>
        public string Scheme => TlsConfigured ? "https" : "http";

        private static string? Get(string name, string? fallback)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static int GetInt(string name, int fallback)
        {
            return int.TryParse(Environment.GetEnvironmentVariable(name), out int parsed) && (parsed > 0)
                ? parsed
                : fallback;
        }

        private static bool GetBool(string name, bool fallback)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            // Accept the spellings people actually write in YAML and shell scripts.
            return value.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "y" => true,
                "false" or "0" or "no" or "n" => false,
                _ => fallback
            };
        }
    }
}
