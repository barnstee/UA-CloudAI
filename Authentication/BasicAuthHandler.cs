using System.Net.Http.Headers;
using System.Text;

namespace Opc.Ua.Cloud.AI.Authentication
{
    /// <summary>
    /// Attaches HTTP Basic credentials to every outbound request to a downstream API.
    ///
    /// This is the "outbound" half of the server's authentication: the MCP server acts as a
    /// client of the I3X server and of UA Cloud Action's OPC UA Web API, both of which
    /// require Basic auth. The credentials are attached per-request rather than once on the
    /// <see cref="HttpClient"/> so that a handler instance can safely be shared.
    /// </summary>
    public sealed class BasicAuthHandler : DelegatingHandler
    {
        private readonly string? _header;

        public BasicAuthHandler(string? username, string? password)
        {
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            {
                _header = Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Never overwrite an Authorization header the caller set deliberately.
            if ((_header != null) && (request.Headers.Authorization == null))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _header);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
