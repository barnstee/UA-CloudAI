using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Opc.Ua.Cloud.AI.Clients;
using Opc.Ua.Cloud.AI.Configuration;

namespace Opc.Ua.Cloud.AI.Tools
{
    /// <summary>
    /// MCP tools over the I3X semantic graph.
    ///
    /// The [Description] text on each tool and parameter is the ONLY thing a language model
    /// sees when deciding what to call, so each one states what the tool returns, when to
    /// prefer it, and what the identifiers look like. Vague descriptions are the most common
    /// reason an otherwise working MCP server goes unused.
    /// </summary>
    [McpServerToolType]
    public sealed class I3XTools
    {
        private readonly I3XClient _client;
        private readonly ServerSettings _settings;

        public I3XTools(I3XClient client, ServerSettings settings)
        {
            _client = client;
            _settings = settings;
        }

        [McpServerTool(Name = "i3x_get_server_info")]
        [Description(
            "Returns the I3X server's version and capabilities (whether it supports history, " +
            "updates and streaming subscriptions). Call this first if you are unsure whether a " +
            "capability such as historical data is available before relying on it.")]
        public async Task<string> GetServerInfoAsync(CancellationToken cancellationToken)
        {
            return await SafeAsync(async () =>
                ToolResult.Json(await _client.GetInfoAsync(cancellationToken).ConfigureAwait(false)))
                .ConfigureAwait(false);
        }

        [McpServerTool(Name = "i3x_browse_hierarchy")]
        [Description(
            "Browses the ISA-95 asset hierarchy (enterprise -> site -> area -> production line -> " +
            "workcell/station -> variables). THIS IS THE USUAL STARTING POINT for exploring what " +
            "exists in the plant. Call it with root=true to get the top level, then take an " +
            "'elementId' from the result and pass it to i3x_get_related_objects to walk downwards. " +
            "Each returned object has an 'elementId' (use it in other i3x tools), a 'displayName', " +
            "and 'isComposition' which is true for structural container nodes and false for " +
            "value-bearing variables.")]
        public async Task<string> BrowseHierarchyAsync(
            [Description("True to return only the top level of the hierarchy. Set false or omit to return every object, which can be large.")]
            bool root = true,
            [Description("Optional: return only objects of this type id, as returned by i3x_list_object_types (for example 'ISA95:Workcell').")]
            string? typeElementId = null,
            [Description("Include extra metadata (description, relationships, schema extensions) on each object.")]
            bool includeMetadata = false,
            CancellationToken cancellationToken = default)
        {
            return await SafeAsync(async () =>
            {
                JsonElement result = await _client.GetObjectsAsync(root, typeElementId, includeMetadata, cancellationToken).ConfigureAwait(false);
                return ToolResult.JsonCapped(result, _settings.MaxResults);
            }).ConfigureAwait(false);
        }

        [McpServerTool(Name = "i3x_get_objects")]
        [Description(
            "Looks up specific objects by their element ids. Use this when you already have one or " +
            "more 'elementId' values (from i3x_browse_hierarchy or i3x_get_related_objects) and want " +
            "their details. Passing no ids returns nothing - use i3x_browse_hierarchy to discover ids first.")]
        public async Task<string> GetObjectsAsync(
            [Description("The element ids to look up, exactly as returned by a previous i3x call.")]
            string[] elementIds,
            [Description("Include extra metadata on each object.")]
            bool includeMetadata = false,
            CancellationToken cancellationToken = default)
        {
            if ((elementIds == null) || (elementIds.Length == 0))
            {
                return ToolResult.Guidance("No elementIds supplied. Call i3x_browse_hierarchy first to discover element ids.");
            }

            return await SafeAsync(async () =>
                ToolResult.JsonCapped(await _client.ListObjectsAsync(elementIds, includeMetadata, cancellationToken).ConfigureAwait(false), _settings.MaxResults))
                .ConfigureAwait(false);
        }

        [McpServerTool(Name = "i3x_get_related_objects")]
        [Description(
            "Follows typed relationships from one or more objects - this is how you WALK THE " +
            "HIERARCHY. From a site you reach its areas, from a line its stations, from a station " +
            "its variables. Relationships are traversable in both directions: omit relationshipType " +
            "to get both the children (HasComponent) and the parent (ComponentOf), or pass a specific " +
            "type from i3x_list_relationship_types to go one way only.")]
        public async Task<string> GetRelatedObjectsAsync(
            [Description("The element ids to start from.")]
            string[] elementIds,
            [Description("Optional relationship type id to follow, for example 'HasComponent' for children or 'ComponentOf' for the parent. Omit to return both directions.")]
            string? relationshipType = null,
            [Description("Include extra metadata on each related object.")]
            bool includeMetadata = false,
            CancellationToken cancellationToken = default)
        {
            if ((elementIds == null) || (elementIds.Length == 0))
            {
                return ToolResult.Guidance("No elementIds supplied. Call i3x_browse_hierarchy first to discover element ids.");
            }

            return await SafeAsync(async () =>
                ToolResult.JsonCapped(await _client.GetRelatedAsync(elementIds, relationshipType, includeMetadata, cancellationToken).ConfigureAwait(false), _settings.MaxResults))
                .ConfigureAwait(false);
        }

        [McpServerTool(Name = "i3x_read_current_values")]
        [Description(
            "Reads the CURRENT value of one or more objects, returning value, quality and timestamp " +
            "for each. Pass the elementId of a variable (an object whose 'isComposition' is false). " +
            "If you pass a container such as a station, set maxDepth above 1 to include the values of " +
            "the variables beneath it. An empty or stale result usually means the asset has not " +
            "published recently rather than that the id is wrong - check the timestamp.")]
        public async Task<string> ReadCurrentValuesAsync(
            [Description("The element ids whose current values are wanted.")]
            string[] elementIds,
            [Description("How many levels of composition to include. 1 reads only the named objects; 2 or more also reads their children.")]
            int maxDepth = 1,
            CancellationToken cancellationToken = default)
        {
            if ((elementIds == null) || (elementIds.Length == 0))
            {
                return ToolResult.Guidance("No elementIds supplied. Call i3x_browse_hierarchy first to discover element ids.");
            }

            return await SafeAsync(async () =>
                ToolResult.JsonCapped(await _client.GetValueAsync(elementIds, maxDepth, cancellationToken).ConfigureAwait(false), _settings.MaxResults))
                .ConfigureAwait(false);
        }

        [McpServerTool(Name = "i3x_read_history")]
        [Description(
            "Reads HISTORICAL values over a time range - use this for trends, averages, or 'what " +
            "happened' questions rather than calling the current-value tool repeatedly. Times are " +
            "ISO-8601 UTC, for example '2026-09-02T10:00:00Z'. Omitting them lets the server choose " +
            "a default window. Note that the underlying historian retains a limited period, and the " +
            "simulated production line is idle between shifts, so a range covering a break will " +
            "legitimately return few or no values.")]
        public async Task<string> ReadHistoryAsync(
            [Description("The element ids whose history is wanted.")]
            string[] elementIds,
            [Description("Start of the range, ISO-8601 UTC, e.g. '2026-09-02T08:00:00Z'.")]
            string? startTime = null,
            [Description("End of the range, ISO-8601 UTC. Omit for 'now'.")]
            string? endTime = null,
            [Description("How many levels of composition to include.")]
            int maxDepth = 1,
            CancellationToken cancellationToken = default)
        {
            if ((elementIds == null) || (elementIds.Length == 0))
            {
                return ToolResult.Guidance("No elementIds supplied. Call i3x_browse_hierarchy first to discover element ids.");
            }

            return await SafeAsync(async () =>
                ToolResult.JsonCapped(await _client.GetHistoryAsync(elementIds, startTime, endTime, maxDepth, cancellationToken).ConfigureAwait(false), _settings.MaxResults))
                .ConfigureAwait(false);
        }

        [McpServerTool(Name = "i3x_list_namespaces")]
        [Description(
            "Lists the OPC UA namespaces present in the data. Useful for understanding which " +
            "information models are in play; most exploration should start with i3x_browse_hierarchy instead.")]
        public async Task<string> ListNamespacesAsync(CancellationToken cancellationToken)
        {
            return await SafeAsync(async () =>
                ToolResult.JsonCapped(await _client.GetNamespacesAsync(cancellationToken).ConfigureAwait(false), _settings.MaxResults))
                .ConfigureAwait(false);
        }

        [McpServerTool(Name = "i3x_list_object_types")]
        [Description(
            "Lists the available object types, for example the ISA-95 container levels " +
            "('ISA95:Site', 'ISA95:Workcell') and the OPC UA data types of the variables. Pass one of " +
            "these ids as 'typeElementId' to i3x_browse_hierarchy to find every object of that type.")]
        public async Task<string> ListObjectTypesAsync(CancellationToken cancellationToken)
        {
            return await SafeAsync(async () =>
                ToolResult.JsonCapped(await _client.GetObjectTypesAsync(cancellationToken).ConfigureAwait(false), _settings.MaxResults))
                .ConfigureAwait(false);
        }

        [McpServerTool(Name = "i3x_list_relationship_types")]
        [Description(
            "Lists the relationship types that can be followed with i3x_get_related_objects, and " +
            "which type is the reverse of which (for example HasComponent / ComponentOf).")]
        public async Task<string> ListRelationshipTypesAsync(CancellationToken cancellationToken)
        {
            return await SafeAsync(async () =>
                ToolResult.JsonCapped(await _client.GetRelationshipTypesAsync(cancellationToken).ConfigureAwait(false), _settings.MaxResults))
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Turns transport and HTTP failures into a description the model can act on, rather
        /// than letting an exception surface as an opaque MCP protocol error.
        /// </summary>
        private static async Task<string> SafeAsync(Func<Task<string>> action)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (I3XRequestException ex) when (ex.StatusCode == 401)
            {
                return ToolResult.Guidance(
                    "The I3X server rejected the credentials (HTTP 401). The I3X_USERNAME / I3X_PASSWORD " +
                    "this server was configured with do not match the I3X deployment.");
            }
            catch (I3XRequestException ex) when (ex.StatusCode == 503)
            {
                return ToolResult.Guidance(
                    "The I3X server has no authentication configured and is refusing all requests (HTTP 503). " +
                    "Set I3X_BASIC_AUTH_USERNAME / I3X_BASIC_AUTH_PASSWORD on the I3X deployment.");
            }
            catch (I3XRequestException ex)
            {
                return ToolResult.Guidance(ex.Message);
            }
            catch (HttpRequestException ex)
            {
                return ToolResult.Guidance(
                    "Could not reach the I3X server: " + ex.Message +
                    ". Check I3X_BASE_URL and that the i3x4influx container is running.");
            }
            catch (TaskCanceledException)
            {
                return ToolResult.Guidance("The I3X request timed out. Try a narrower time range or fewer element ids.");
            }
        }
    }
}
