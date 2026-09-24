# UA-CloudAI

An **MCP (Model Context Protocol) server** that gives agentic AI applications access to a
live industrial plant. It fronts two complementary APIs from the
[Cloud Initiative Reference Solution](https://github.com/OPCF-Members/Cloud-Initiative-Reference-Solution):

| Backend | What it provides | Identifier |
|---|---|---|
| **[I3X](https://i3x.dev)** (`i3x4influx`) | The **semantic graph** — an ISA-95 hierarchy (enterprise → site → area → line → station → variable) joined by typed relationships | `elementId` |
| **OPC UA Web API** (UA Cloud Action) | The **raw address space** — a flat list of OPC UA nodes addressed by `NodeId` | `nodeId` |

Both read the same underlying telemetry, but they answer different questions. The server
exposes a `describe_available_data` tool precisely so a model chooses correctly between them.

Built with **.NET 10** and the official
[ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore) SDK.

---

## Table of Contents

- [Tools](#tools)
- [Quick Start](#quick-start)
  - [Run with Docker](#run-with-docker)
  - [Run from source](#run-from-source)
- [Configuration](#configuration)
- [Connecting a Client](#connecting-a-client)
  - [MCP Inspector (browser)](#mcp-inspector-browser)
  - [Claude Desktop](#claude-desktop)
  - [VS Code / Visual Studio](#vs-code--visual-studio)
  - [Raw curl](#raw-curl)
- [Deploying to Kubernetes](#deploying-to-kubernetes)
- [Security](#security)
- [Troubleshooting](#troubleshooting)

---

## Tools

Fourteen tools in three groups.

**Orientation** — start here.

| Tool | Purpose |
|---|---|
| `describe_available_data` | Explains both interfaces, which to use when, and a suggested call sequence. Costs nothing and makes later calls far more likely to succeed. |
| `check_connectivity` | Reports each backend separately, so a failure can be attributed to connectivity, credentials or the query itself. |

**I3X — semantic graph.**

| Tool | Purpose |
|---|---|
| `i3x_browse_hierarchy` | Browse the ISA-95 hierarchy. The usual starting point. |
| `i3x_get_related_objects` | Follow typed relationships — how you walk from a line to its stations. |
| `i3x_get_objects` | Look up specific objects by `elementId`. |
| `i3x_read_current_values` | Current value, quality and timestamp. |
| `i3x_read_history` | Historical values over a time range. |
| `i3x_list_namespaces` | The OPC UA namespaces present. |
| `i3x_list_object_types` | Available object types, e.g. `ISA95:Workcell`. |
| `i3x_list_relationship_types` | Relationship types and their reverses. |
| `i3x_get_server_info` | Server version and capabilities. |

**OPC UA Web API — raw address space.**

| Tool | Purpose |
|---|---|
| `opcua_browse_nodes` | Flat list of every queryable tag. |
| `opcua_read_values` | Current `DataValue` for one or more `NodeId`s. |
| `opcua_read_history` | Raw historical values over a time range. |

---

## Quick Start

### Run with Docker

```sh
docker build -t ua-cloudai .

docker run --rm -p 5000:5000 \
  -e I3X_BASE_URL="http://<device-ip>:8084" \
  -e OPCUA_WEBAPI_BASE_URL="http://<device-ip>:8082" \
  -e IOT_USERNAME="<username>" \
  -e IOT_PASSWORD="<password>" \
  -e MCP_USERNAME="<mcp-user>" \
  -e MCP_PASSWORD="<mcp-password>" \
  ua-cloudai
```

Confirm it is alive — `/health` needs no credentials:

```sh
curl -s http://localhost:5000/health
# {"status":"ok"}
```

### Run from source

```sh
dotnet run
```

---

## Configuration

Everything is environment variables, so the container never needs rebuilding.

| Variable | Default | Purpose |
|---|---|---|
| `MCP_TRANSPORT` | `http` | `http` for Docker and browser clients; `stdio` when a desktop app launches the binary directly. |
| `MCP_PORT` | `5000` | Port for the HTTP transport. |
| `I3X_BASE_URL` | `http://i3x4influx.cloud.svc.cluster.local:8084` | I3X server. |
| `OPCUA_WEBAPI_BASE_URL` | `http://ua-cloudaction.cloud.svc.cluster.local:8082` | UA Cloud Action. |
| `IOT_USERNAME` / `IOT_PASSWORD` | — | Credentials for **both** backends. Matches the reference solution's convention. |
| `I3X_USERNAME` / `I3X_PASSWORD` | falls back to `IOT_*` | Override for I3X only. |
| `OPCUA_WEBAPI_USERNAME` / `OPCUA_WEBAPI_PASSWORD` | falls back to `IOT_*` | Override for the Web API only. |
| `MCP_USERNAME` / `MCP_PASSWORD` | — | Credentials clients must present **to this server**. Unset means anonymous access. |
| `MCP_MAX_RESULTS` | `200` | Caps items per tool result, protecting the model's context window. |
| `HTTP_TIMEOUT_SECONDS` | `100` | Downstream request timeout. |
| `MCP_TLS_CERT_PATH` | — | PKCS#12 (`.pfx`) certificate. Setting it serves **HTTPS**. |
| `MCP_TLS_CERT_PASSWORD` | — | Password for the `.pfx`, if it has one. |
| `MCP_TLS_PEM_PATH` / `MCP_TLS_KEY_PATH` | — | PEM certificate and key, as mounted by a `kubernetes.io/tls` Secret. Alternative to the `.pfx`. |
| `BACKEND_CA_CERT_PATH` | — | PEM CA to additionally trust when calling the backends over HTTPS. Validation stays **on**. |
| `ALLOW_UNTRUSTED_BACKEND_CERTS` | `false` | Skips backend certificate validation. Last resort — see below. |

> ℹ️ **Two directions of authentication.** `IOT_*` (or the per-backend variables) authenticate
> this server *to* I3X and UA Cloud Action. `MCP_USERNAME` / `MCP_PASSWORD` authenticate
> clients *to this server*. They are independent and may differ.

---

## TLS

Basic auth sends **reversible** credentials on every single request. That is not a flaw in
Basic auth as such — it is only a problem over an unencrypted transport, where anyone on the
network path can read and replay them. **Basic auth over TLS is a sound combination**, and is
how this server is intended to run outside a local test.

Point it at a certificate and it serves HTTPS. Two forms are supported.

**PKCS#12 (`.pfx`):**

```sh
docker run --rm -p 5443:5443 \
  -v /path/to/certs:/certs:ro \
  -e MCP_PORT=5443 \
  -e MCP_TLS_CERT_PATH=/certs/server.pfx \
  -e MCP_TLS_CERT_PASSWORD='<pfx-password>' \
  -e MCP_USERNAME='<mcp-user>' \
  -e MCP_PASSWORD='<mcp-password>' \
  ua-cloudai
```

**PEM pair** — the shape a Kubernetes TLS Secret mounts, so no conversion step is needed:

```sh
kubectl create secret tls ua-cloudai-tls -n cloud --cert=tls.crt --key=tls.key
```

```sh
-e MCP_TLS_PEM_PATH=/tls/tls.crt \
-e MCP_TLS_KEY_PATH=/tls/tls.key
```

The endpoint then becomes `https://<host>:5443/mcp`, and the startup banner reflects the
scheme in use. If Basic auth is enabled **without** TLS, the server logs a warning at startup
rather than failing silently.

### Calling HTTPS backends

Once I3X and UA Cloud Action are themselves moved to HTTPS with privately issued
certificates, a container will not trust them by default. Pin their CA:

```sh
-e BACKEND_CA_CERT_PATH=/backend-ca/ca.crt
```

This keeps full chain verification enabled, checking against the system roots *plus* that CA.

> ⚠️ `ALLOW_UNTRUSTED_BACKEND_CERTS=true` exists as an escape hatch for quick diagnosis, but
> it disables certificate validation entirely and so removes protection against an active
> man-in-the-middle. Prefer `BACKEND_CA_CERT_PATH`. The server warns loudly when this is on.

---

## Connecting a Client

### MCP Inspector (browser)

The quickest way to see the tools and call them by hand:

```sh
npx @modelcontextprotocol/inspector
```

In the Inspector: set **Transport** to `Streamable HTTP`, **URL** to
`http://localhost:5000/mcp`, and if you set `MCP_USERNAME` / `MCP_PASSWORD`, add a header:

```
Authorization: Basic <base64 of user:password>
```

Generate that value with:

```sh
printf '%s' "user:password" | base64
```

### Claude Desktop

Claude Desktop speaks **stdio**, so it launches the server itself. This avoids any HTTP
listener and needs no `mcp-remote` bridge. Edit `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "ua-cloudai": {
      "command": "dotnet",
      "args": ["run", "--project", "D:\\Code\\UA-CloudAI", "--no-build"],
      "env": {
        "MCP_TRANSPORT": "stdio",
        "I3X_BASE_URL": "http://<device-ip>:8084",
        "OPCUA_WEBAPI_BASE_URL": "http://<device-ip>:8082",
        "IOT_USERNAME": "<username>",
        "IOT_PASSWORD": "<password>"
      }
    }
  }
}
```

To use an already-running **HTTP** instance instead, bridge it with `mcp-remote`:

```json
{
  "mcpServers": {
    "ua-cloudai": {
      "command": "npx",
      "args": [
        "-y", "mcp-remote",
        "http://<device-ip>:5001/mcp",
        "--header", "Authorization:Basic <base64 of user:password>"
      ]
    }
  }
}
```

> ⚠️ In stdio mode the MCP protocol travels over **stdout**, so anything else printed there
> corrupts the stream and the client fails to start the server. This build redirects all
> logging to stderr when `MCP_TRANSPORT=stdio` — keep it that way if you add logging.

### VS Code / Visual Studio

Add to `.vscode/mcp.json`:

```json
{
  "servers": {
    "ua-cloudai": {
      "type": "http",
      "url": "http://localhost:5000/mcp",
      "headers": { "Authorization": "Basic <base64 of user:password>" }
    }
  }
}
```

### Raw curl

Useful for confirming the server works before involving any client:

```sh
curl -s -u "<mcp-user>:<mcp-password>" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"curl","version":"1.0"}}}' \
  http://localhost:5000/mcp
```

---

## Deploying to Kubernetes

[`deployment.yaml`](./deployment.yaml) places the server in the `cloud` namespace alongside
the rest of the reference solution, already pointed at the in-cluster I3X and UA Cloud Action
services:

```sh
export IOT_USERNAME='<username>'
export IOT_PASSWORD='<password>'

envsubst '${IOT_USERNAME} ${IOT_PASSWORD}' < deployment.yaml | kubectl apply -f -
```

The MCP endpoint is then at `http://<device-ip>:5001/mcp`.

---

## Security

- **Basic auth both ways.** Inbound is enforced by middleware on `/mcp`; outbound credentials
  are attached to every downstream call.
- **`/health` is deliberately unauthenticated** so Kubernetes probes can reach it — a probe
  that cannot authenticate would restart a healthy container forever.
- **Credentials are compared in fixed time**, so a caller cannot recover them from response
  timing.
- **TLS is supported and recommended.** Basic auth transmits reversible credentials on every
  request, so set `MCP_TLS_CERT_PATH` (or the PEM pair) whenever the server is reachable
  beyond localhost. Plain HTTP remains the default only so a first local run needs no
  certificate; the server warns at startup if Basic auth is enabled without TLS.
- **Anonymous when unconfigured.** Leaving `MCP_USERNAME` / `MCP_PASSWORD` unset disables
  inbound auth — reasonable for stdio, where the transport is a private pipe, and not
  acceptable for a network-exposed deployment. The server logs a warning at startup when this
  is the case.

> ⚠️ This server is **read-only**: it browses and reads, and exposes no write, method-call or
> actuation path. That is a deliberate boundary — an agent can analyse the plant but cannot
> change it.

---

## Troubleshooting

| Symptom | Cause |
|---|---|
| `401` from `/mcp` | `MCP_USERNAME` / `MCP_PASSWORD` set but the client sent no or wrong credentials. |
| Tool returns `"Could not reach the I3X server"` | `I3X_BASE_URL` wrong, or `i3x4influx` not running. Call `check_connectivity`. |
| Tool returns I3X `HTTP 503` | The **I3X server itself** has no auth configured; it fails closed. Set its Basic auth variables. |
| Empty hierarchy from `i3x_browse_hierarchy` | Nothing published inside I3X's browse window. The simulated line is idle between shifts. |
| Claude Desktop shows the server as failed | In stdio mode, something wrote to stdout. Check the Claude logs; all logging must go to stderr. |
| History returns few values | The range covers a shift gap, or exceeds the historian's retention. |
| Client rejects the certificate | Self-signed cert not trusted by the client. Trust the CA, or use a properly issued certificate. |
| Backend call fails with a certificate error | Backend uses a privately issued certificate. Set `BACKEND_CA_CERT_PATH` to its CA. |
| Connection reset when calling `https://` | The server is serving plain HTTP — no certificate was configured. Check the startup banner's scheme. |
