using System.Net.Http.Json;
using System.Text.Json;

namespace Opc.Ua.Cloud.AI.Clients
{
    /// <summary>
    /// Typed client for UA Cloud Action's OPC UA Web API - an OpenAPI representation of the
    /// OPC UA Services (IEC 62541-4).
    ///
    /// <para>
    /// Where I3X offers a semantic graph, this API offers the RAW address space: a flat list
    /// of queryable tags from <c>Browse</c>, and values addressed by OPC UA <c>NodeId</c> from
    /// <c>Read</c> and <c>HistoryRead</c>. The request and response bodies follow the OPC UA
    /// service definitions rather than a REST convention, which is why the shapes below look
    /// verbose: they are the spec's.
    /// </para>
    /// </summary>
    public sealed class OpcUaWebApiClient
    {
        private readonly HttpClient _http;
        private readonly ILogger<OpcUaWebApiClient> _logger;

        public const string HttpClientName = "opcua-webapi";

        public OpcUaWebApiClient(IHttpClientFactory factory, ILogger<OpcUaWebApiClient> logger)
        {
            _http = factory.CreateClient(HttpClientName);
            _logger = logger;
        }

        /// <summary>
        /// POST /opcua/browse - enumerate the queryable tags. The request body is optional for
        /// this historian-backed implementation, so an empty header is sent.
        /// </summary>
        public Task<JsonElement> BrowseAsync(CancellationToken ct) =>
            PostAsync("opcua/browse", new { requestHeader = new { } }, ct);

        /// <summary>
        /// POST /opcua/read - read the current value of one or more nodes.
        /// </summary>
        /// <param name="nodeIds">OPC UA NodeIds, as returned by <see cref="BrowseAsync"/>.</param>
        /// <param name="maxAge">
        /// Maximum acceptable age of a cached value in milliseconds; 0 forces a fresh read.
        /// </param>
        public Task<JsonElement> ReadAsync(IEnumerable<string> nodeIds, double maxAge, CancellationToken ct)
        {
            var body = new
            {
                requestHeader = new { },
                maxAge,
                // 2 = Both source and server timestamps (OPC 10000-4, TimestampsToReturn).
                timestampsToReturn = 2,
                nodesToRead = nodeIds.Select(id => new
                {
                    nodeId = id,
                    // 13 = the Value attribute (OPC 10000-6, AttributeIds).
                    attributeId = 13
                }).ToArray()
            };

            return PostAsync("opcua/read", body, ct);
        }

        /// <summary>
        /// POST /opcua/historyread - read raw historical values for one or more nodes over a
        /// time range.
        /// </summary>
        public Task<JsonElement> HistoryReadAsync(
            IEnumerable<string> nodeIds,
            DateTime startTime,
            DateTime endTime,
            uint numValuesPerNode,
            CancellationToken ct)
        {
            var body = new
            {
                requestHeader = new { },
                historyReadDetails = new
                {
                    isReadModified = false,
                    startTime,
                    endTime,
                    numValuesPerNode,
                    returnBounds = false
                },
                timestampsToReturn = 2,
                releaseContinuationPoints = false,
                nodesToRead = nodeIds.Select(id => new { nodeId = id }).ToArray()
            };

            return PostAsync("opcua/historyread", body, ct);
        }

        private async Task<JsonElement> PostAsync(string path, object body, CancellationToken ct)
        {
            using HttpResponseMessage response = await _http.PostAsJsonAsync(path, body, ct).ConfigureAwait(false);
            string content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("OPC UA Web API {Path} returned {Status}: {Body}", path, (int)response.StatusCode, Truncate(content));
                throw new OpcUaWebApiRequestException((int)response.StatusCode, path, Truncate(content));
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return default;
            }

            using JsonDocument document = JsonDocument.Parse(content);
            return document.RootElement.Clone();
        }

        private static string Truncate(string value) =>
            (value.Length <= 500) ? value : value[..500] + "...";
    }

    public sealed class OpcUaWebApiRequestException : Exception
    {
        public OpcUaWebApiRequestException(int statusCode, string path, string body)
            : base($"OPC UA Web API request to '{path}' failed with HTTP {statusCode}: {body}")
        {
            StatusCode = statusCode;
        }

        public int StatusCode { get; }
    }
}
