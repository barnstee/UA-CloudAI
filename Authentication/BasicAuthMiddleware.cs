using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Opc.Ua.Cloud.AI.Configuration;

namespace Opc.Ua.Cloud.AI.Authentication
{
    /// <summary>
    /// Requires HTTP Basic authentication on the MCP endpoint.
    ///
    /// <para>
    /// <b>/health is deliberately exempt</b> so Kubernetes liveness and readiness probes can
    /// reach it without credentials - a probe that cannot authenticate would restart a
    /// perfectly healthy container forever.
    /// </para>
    /// <para>
    /// When no credentials are configured the middleware serves anonymously rather than
    /// failing closed. That is a deliberate difference from the I3X server: this process is
    /// also run over stdio as a child of a desktop AI application, where the transport is a
    /// private pipe and there is nothing to authenticate. The README calls out that leaving
    /// it unset on a network-exposed deployment is not acceptable.
    /// </para>
    /// </summary>
    public sealed class BasicAuthMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ServerSettings _settings;
        private readonly ILogger<BasicAuthMiddleware> _logger;

        public BasicAuthMiddleware(RequestDelegate next, ServerSettings settings, ILogger<BasicAuthMiddleware> logger)
        {
            _next = next;
            _settings = settings;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (IsExempt(context) || !_settings.InboundAuthConfigured)
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            if (!TryGetCredentials(context, out string username, out string password))
            {
                await ChallengeAsync(context).ConfigureAwait(false);
                return;
            }

            if (!FixedTimeEquals(username, _settings.McpUsername!) ||
                !FixedTimeEquals(password, _settings.McpPassword!))
            {
                _logger.LogWarning("Rejected MCP request: invalid credentials for user '{User}'.", username);
                await ChallengeAsync(context).ConfigureAwait(false);
                return;
            }

            await _next(context).ConfigureAwait(false);
        }

        private static bool IsExempt(HttpContext context)
        {
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                return true;
            }

            PathString path = context.Request.Path;
            return path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetCredentials(HttpContext context, out string username, out string password)
        {
            username = string.Empty;
            password = string.Empty;

            string? header = context.Request.Headers.Authorization;
            if (string.IsNullOrEmpty(header) ||
                !AuthenticationHeaderValue.TryParse(header, out AuthenticationHeaderValue? parsed) ||
                !string.Equals(parsed.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(parsed.Parameter))
            {
                return false;
            }

            try
            {
                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parsed.Parameter));
                int separator = decoded.IndexOf(':');
                if (separator < 0)
                {
                    return false;
                }

                username = decoded[..separator];
                password = decoded[(separator + 1)..];
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static async Task ChallengeAsync(HttpContext context)
        {
            // The realm makes browsers and MCP Inspector prompt for credentials.
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"UA-CloudAI\", charset=\"UTF-8\"";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized. Supply HTTP Basic credentials.").ConfigureAwait(false);
        }

        /// <summary>
        /// Compares two strings in time independent of how many leading characters match, so a
        /// caller cannot recover the expected value byte by byte from response timing.
        /// </summary>
        private static bool FixedTimeEquals(string left, string right)
        {
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(left),
                Encoding.UTF8.GetBytes(right));
        }
    }
}
