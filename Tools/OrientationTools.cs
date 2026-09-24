using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Opc.Ua.Cloud.AI.Clients;
using Opc.Ua.Cloud.AI.Configuration;

namespace Opc.Ua.Cloud.AI.Tools
{
    /// <summary>
    /// Orientation tools.
    ///
    /// This server fronts two APIs that can both answer "what is the temperature of station X",
    /// which is exactly the situation in which a model picks arbitrarily and then gets stuck.
    /// These tools exist to make the choice explicit and to give a single call that proves
    /// end-to-end connectivity before any real work starts.
    /// </summary>
    [McpServerToolType]
    public sealed class OrientationTools
    {
        private readonly I3XClient _i3x;
        private readonly OpcUaWebApiClient _opcua;
        private readonly ServerSettings _settings;

        public OrientationTools(I3XClient i3x, OpcUaWebApiClient opcua, ServerSettings settings)
        {
            _i3x = i3x;
            _opcua = opcua;
            _settings = settings;
        }

        [McpServerTool(Name = "describe_available_data")]
        [Description(
            "READ THIS FIRST when you are new to this plant. Explains the two data interfaces this " +
            "server exposes, which one to use for which kind of question, and the usual sequence of " +
            "calls. Costs nothing and makes every later call more likely to succeed.")]
        public string DescribeAvailableData()
        {
            var guide = new
            {
                summary =
                    "This server exposes one industrial plant through two complementary APIs. " +
                    "They read the same underlying telemetry, but they present it differently.",

                interfaces = new object[]
                {
                    new
                    {
                        name = "I3X (semantic graph)",
                        tools = new[] { "i3x_browse_hierarchy", "i3x_get_related_objects", "i3x_read_current_values", "i3x_read_history" },
                        useWhen =
                            "You care about STRUCTURE or meaning: which lines exist, which stations belong to a " +
                            "line, what a value represents. Objects are arranged as an ISA-95 hierarchy " +
                            "(enterprise -> site -> area -> line -> station -> variable) joined by typed relationships.",
                        identifier = "elementId"
                    },
                    new
                    {
                        name = "OPC UA Web API (raw address space)",
                        tools = new[] { "opcua_browse_nodes", "opcua_read_values", "opcua_read_history" },
                        useWhen =
                            "You want the exhaustive flat tag list, or you already hold an OPC UA NodeId. " +
                            "There is no hierarchy here - it is the unstructured view.",
                        identifier = "nodeId"
                    }
                },

                identifiersAreNotInterchangeable =
                    "An I3X 'elementId' and an OPC UA 'nodeId' are different identifier spaces. Do not pass " +
                    "one where the other is expected; discover ids with the browse tool of the same family.",

                suggestedSequence = new[]
                {
                    "1. call describe_available_data (this tool)",
                    "2. call check_connectivity to confirm both backends are reachable",
                    "3. call i3x_browse_hierarchy with root=true to see the top of the plant",
                    "4. call i3x_get_related_objects on an elementId to walk down to stations and variables",
                    "5. call i3x_read_current_values or i3x_read_history on the variables you found"
                },

                gotchas = new[]
                {
                    "The simulated production line is idle between shifts, so a history range covering a break returns few or no values - that is expected, not an error.",
                    "Results are capped at " + _settings.MaxResults + " items per call; a truncated response says so explicitly in a 'note' field.",
                    "A non-zero OPC UA statusCode means that individual node failed even when the overall call succeeded."
                }
            };

            return JsonSerializer.Serialize(guide, new JsonSerializerOptions { WriteIndented = false });
        }

        [McpServerTool(Name = "check_connectivity")]
        [Description(
            "Checks whether this server can actually reach both backends, and reports each one " +
            "separately with the configured URL. Call this when a data tool fails, to find out " +
            "whether the problem is connectivity, credentials, or the query itself.")]
        public async Task<string> CheckConnectivityAsync(CancellationToken cancellationToken)
        {
            string i3xStatus;
            try
            {
                JsonElement info = await _i3x.GetInfoAsync(cancellationToken).ConfigureAwait(false);
                string name = info.TryGetProperty("result", out JsonElement result) && result.TryGetProperty("serverName", out JsonElement serverName)
                    ? serverName.GetString() ?? "unknown"
                    : "unknown";
                i3xStatus = "reachable (serverName: " + name + ")";
            }
            catch (I3XRequestException ex)
            {
                i3xStatus = "responded with HTTP " + ex.StatusCode +
                            (ex.StatusCode == 401 ? " - credentials rejected" :
                             ex.StatusCode == 503 ? " - the I3X server has no authentication configured" : string.Empty);
            }
            catch (Exception ex)
            {
                i3xStatus = "unreachable: " + ex.Message;
            }

            string opcuaStatus;
            try
            {
                await _opcua.BrowseAsync(cancellationToken).ConfigureAwait(false);
                opcuaStatus = "reachable";
            }
            catch (OpcUaWebApiRequestException ex)
            {
                opcuaStatus = "responded with HTTP " + ex.StatusCode +
                              (ex.StatusCode == 401 ? " - credentials rejected" : string.Empty);
            }
            catch (Exception ex)
            {
                opcuaStatus = "unreachable: " + ex.Message;
            }

            var status = new
            {
                i3x = new { url = _settings.I3XBaseUrl, status = i3xStatus },
                opcUaWebApi = new { url = _settings.OpcUaWebApiBaseUrl, status = opcuaStatus }
            };

            return JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = false });
        }
    }
}
