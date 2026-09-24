using System.Net.Http.Json;
using System.Text.Json;

namespace Opc.Ua.Cloud.AI.Clients
{
    /// <summary>
    /// Typed client for the I3X (Industrial Information Interoperability eXchange, https://i3x.dev)
    /// REST API.
    ///
    /// <para>
    /// I3X presents industrial data as a CONNECTED GRAPH: an ISA-95 hierarchy of objects
    /// (enterprise, site, area, line, workcell, and the variables beneath them) joined by
    /// typed relationships. Browsing and discovery are GET requests; the value and history
    /// operations are POSTs that take a batch of element ids.
    /// </para>
    /// <para>
    /// Every route is under <c>/v1</c>. This client keeps the responses as
    /// <see cref="JsonElement"/> rather than mapping them onto local record types: the tools
    /// pass the payloads through to a language model, so preserving the server's own field
    /// names is more useful than imposing a second, possibly drifting, schema.
    /// </para>
    /// </summary>
    public sealed class I3XClient
    {
        private readonly HttpClient _http;
        private readonly ILogger<I3XClient> _logger;

        public const string HttpClientName = "i3x";

        public I3XClient(IHttpClientFactory factory, ILogger<I3XClient> logger)
        {
            _http = factory.CreateClient(HttpClientName);
            _logger = logger;
        }

        /// <summary>GET /v1/info - server capabilities. Exempt from authentication upstream.</summary>
        public Task<JsonElement> GetInfoAsync(CancellationToken ct) =>
            GetAsync("v1/info", ct);

        /// <summary>GET /v1/namespaces - the OPC UA namespaces present in the data.</summary>
        public Task<JsonElement> GetNamespacesAsync(CancellationToken ct) =>
            GetAsync("v1/namespaces", ct);

        /// <summary>
        /// GET /v1/objects - browse the ISA-95 hierarchy. With <paramref name="root"/> true this
        /// returns the top level; with <paramref name="typeElementId"/> it returns every object of
        /// that type instead.
        /// </summary>
        public Task<JsonElement> GetObjectsAsync(bool? root, string? typeElementId, bool includeMetadata, CancellationToken ct)
        {
            List<string> query = new();
            if (root.HasValue)
            {
                query.Add("root=" + (root.Value ? "true" : "false"));
            }

            if (!string.IsNullOrWhiteSpace(typeElementId))
            {
                query.Add("typeElementId=" + Uri.EscapeDataString(typeElementId));
            }

            if (includeMetadata)
            {
                query.Add("includeMetadata=true");
            }

            string path = "v1/objects" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);
            return GetAsync(path, ct);
        }

        /// <summary>GET /v1/objecttypes - the available object types.</summary>
        public Task<JsonElement> GetObjectTypesAsync(CancellationToken ct) =>
            GetAsync("v1/objecttypes", ct);

        /// <summary>GET /v1/relationshiptypes - the available relationship types.</summary>
        public Task<JsonElement> GetRelationshipTypesAsync(CancellationToken ct) =>
            GetAsync("v1/relationshiptypes", ct);

        /// <summary>POST /v1/objects/list - look up specific objects by element id.</summary>
        public Task<JsonElement> ListObjectsAsync(IEnumerable<string> elementIds, bool includeMetadata, CancellationToken ct) =>
            PostAsync("v1/objects/list", new { elementIds = elementIds.ToArray(), includeMetadata }, ct);

        /// <summary>
        /// POST /v1/objects/related - follow typed relationships. With no relationship type the
        /// server returns both directions (parent and child edges).
        /// </summary>
        public Task<JsonElement> GetRelatedAsync(IEnumerable<string> elementIds, string? relationshipType, bool includeMetadata, CancellationToken ct) =>
            PostAsync("v1/objects/related", new { elementIds = elementIds.ToArray(), relationshipType, includeMetadata }, ct);

        /// <summary>POST /v1/objects/value - current value(s), with composition depth.</summary>
        public Task<JsonElement> GetValueAsync(IEnumerable<string> elementIds, int maxDepth, CancellationToken ct) =>
            PostAsync("v1/objects/value", new { elementIds = elementIds.ToArray(), maxDepth }, ct);

        /// <summary>POST /v1/objects/history - historical values over a time range.</summary>
        public Task<JsonElement> GetHistoryAsync(IEnumerable<string> elementIds, string? startTime, string? endTime, int maxDepth, CancellationToken ct) =>
            PostAsync("v1/objects/history", new { elementIds = elementIds.ToArray(), startTime, endTime, maxDepth }, ct);

        private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
        {
            using HttpResponseMessage response = await _http.GetAsync(path, ct).ConfigureAwait(false);
            return await ReadAsync(response, path, ct).ConfigureAwait(false);
        }

        private async Task<JsonElement> PostAsync(string path, object body, CancellationToken ct)
        {
            using HttpResponseMessage response = await _http.PostAsJsonAsync(path, body, ct).ConfigureAwait(false);
            return await ReadAsync(response, path, ct).ConfigureAwait(false);
        }

        private async Task<JsonElement> ReadAsync(HttpResponseMessage response, string path, CancellationToken ct)
        {
            string content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("I3X {Path} returned {Status}: {Body}", path, (int)response.StatusCode, Truncate(content));

                // Surface the status in the payload rather than throwing: the tool layer turns
                // this into a message the model can reason about and retry differently.
                throw new I3XRequestException((int)response.StatusCode, path, Truncate(content));
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

    public sealed class I3XRequestException : Exception
    {
        public I3XRequestException(int statusCode, string path, string body)
            : base($"I3X request to '{path}' failed with HTTP {statusCode}: {body}")
        {
            StatusCode = statusCode;
        }

        public int StatusCode { get; }
    }
}
