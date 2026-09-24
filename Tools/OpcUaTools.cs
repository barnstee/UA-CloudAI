using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Server;
using Opc.Ua.Cloud.AI.Clients;
using Opc.Ua.Cloud.AI.Configuration;

namespace Opc.Ua.Cloud.AI.Tools
{
    /// <summary>
    /// MCP tools over UA Cloud Action's OPC UA Web API - the raw OPC UA address space.
    /// </summary>
    [McpServerToolType]
    public sealed class OpcUaTools
    {
        private readonly OpcUaWebApiClient _client;
        private readonly ServerSettings _settings;

        public OpcUaTools(OpcUaWebApiClient client, ServerSettings settings)
        {
            _client = client;
            _settings = settings;
        }

        [McpServerTool(Name = "opcua_browse_nodes")]
        [Description(
            "Lists every queryable OPC UA node (tag) as a flat list, each with a 'nodeId', " +
            "'browseName' and 'displayName'. This is the RAW view of the address space, with no " +
            "hierarchy. Prefer i3x_browse_hierarchy when you want to understand plant STRUCTURE; " +
            "use this when you want an exhaustive tag list, or when you already know a node id and " +
            "want to confirm it exists. Take a 'nodeId' from here and pass it to opcua_read_values " +
            "or opcua_read_history.")]
        public async Task<string> BrowseNodesAsync(CancellationToken cancellationToken)
        {
            return await SafeAsync(async () =>
                ToolResult.JsonCapped(await _client.BrowseAsync(cancellationToken).ConfigureAwait(false), _settings.MaxResults))
                .ConfigureAwait(false);
        }

        [McpServerTool(Name = "opcua_read_values")]
        [Description(
            "Reads the current value of one or more OPC UA nodes by node id, returning an OPC UA " +
            "DataValue for each (value, status code, source and server timestamps). Node ids come " +
            "from opcua_browse_nodes. A statusCode other than 0 means the read did not succeed for " +
            "that node even though the call itself returned successfully - check it per node.")]
        public async Task<string> ReadValuesAsync(
            [Description("OPC UA node ids to read, exactly as returned by opcua_browse_nodes.")]
            string[] nodeIds,
            [Description("Maximum acceptable age of a cached value in milliseconds. 0 forces a fresh read from the source.")]
            double maxAgeMilliseconds = 0,
            CancellationToken cancellationToken = default)
        {
            if ((nodeIds == null) || (nodeIds.Length == 0))
            {
                return ToolResult.Guidance("No nodeIds supplied. Call opcua_browse_nodes first to discover node ids.");
            }

            return await SafeAsync(async () =>
                ToolResult.JsonCapped(await _client.ReadAsync(nodeIds, maxAgeMilliseconds, cancellationToken).ConfigureAwait(false), _settings.MaxResults))
                .ConfigureAwait(false);
        }

        [McpServerTool(Name = "opcua_read_history")]
        [Description(
            "Reads raw historical values for one or more OPC UA nodes over a time range. Times are " +
            "ISO-8601 UTC, for example '2026-09-02T10:00:00Z'. Use this for trends and aggregates " +
            "instead of polling opcua_read_values. numValuesPerNode bounds how much comes back per " +
            "node - keep it modest, because every value returned is consumed as context.")]
        public async Task<string> ReadHistoryAsync(
            [Description("OPC UA node ids whose history is wanted.")]
            string[] nodeIds,
            [Description("Start of the range, ISO-8601 UTC, e.g. '2026-09-02T08:00:00Z'.")]
            string startTime,
            [Description("End of the range, ISO-8601 UTC, e.g. '2026-09-02T09:00:00Z'.")]
            string endTime,
            [Description("Maximum number of values to return per node. Defaults to 100.")]
            uint numValuesPerNode = 100,
            CancellationToken cancellationToken = default)
        {
            if ((nodeIds == null) || (nodeIds.Length == 0))
            {
                return ToolResult.Guidance("No nodeIds supplied. Call opcua_browse_nodes first to discover node ids.");
            }

            if (!TryParseUtc(startTime, out DateTime start))
            {
                return ToolResult.Guidance($"Could not parse startTime '{startTime}'. Use ISO-8601 UTC, for example '2026-09-02T08:00:00Z'.");
            }

            if (!TryParseUtc(endTime, out DateTime end))
            {
                return ToolResult.Guidance($"Could not parse endTime '{endTime}'. Use ISO-8601 UTC, for example '2026-09-02T09:00:00Z'.");
            }

            if (end <= start)
            {
                return ToolResult.Guidance("endTime must be later than startTime.");
            }

            return await SafeAsync(async () =>
                ToolResult.JsonCapped(await _client.HistoryReadAsync(nodeIds, start, end, numValuesPerNode, cancellationToken).ConfigureAwait(false), _settings.MaxResults))
                .ConfigureAwait(false);
        }

        private static bool TryParseUtc(string value, out DateTime parsed)
        {
            return DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out parsed);
        }

        private static async Task<string> SafeAsync(Func<Task<string>> action)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (OpcUaWebApiRequestException ex) when (ex.StatusCode == 401)
            {
                return ToolResult.Guidance(
                    "UA Cloud Action rejected the credentials (HTTP 401). The OPCUA_WEBAPI_USERNAME / " +
                    "OPCUA_WEBAPI_PASSWORD this server was configured with do not match the deployment.");
            }
            catch (OpcUaWebApiRequestException ex)
            {
                return ToolResult.Guidance(ex.Message);
            }
            catch (HttpRequestException ex)
            {
                return ToolResult.Guidance(
                    "Could not reach UA Cloud Action's OPC UA Web API: " + ex.Message +
                    ". Check OPCUA_WEBAPI_BASE_URL and that the ua-cloudaction container is running.");
            }
            catch (TaskCanceledException)
            {
                return ToolResult.Guidance("The OPC UA Web API request timed out. Try a narrower time range or fewer node ids.");
            }
        }
    }
}
