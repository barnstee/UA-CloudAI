using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Opc.Ua.Cloud.AI.Authentication;
using Opc.Ua.Cloud.AI.Clients;
using Opc.Ua.Cloud.AI.Configuration;

ServerSettings settings = ServerSettings.FromEnvironment();

if (settings.UseStdio)
{
	await RunStdioAsync(settings, args).ConfigureAwait(false);
}
else
{
	await RunHttpAsync(settings, args).ConfigureAwait(false);
}

/// <summary>
/// Registers the downstream clients and the MCP server itself. Shared by both transports so
/// the two hosting paths cannot drift apart.
/// </summary>
static void AddCommonServices(IServiceCollection services, ServerSettings settings)
{
	services.AddSingleton(settings);

	services.AddHttpClient(I3XClient.HttpClientName, client =>
	{
		// The trailing slash matters: without it, BaseAddress + "v1/info" would drop the
		// last path segment of the configured URL.
		client.BaseAddress = new Uri(settings.I3XBaseUrl + "/");
		client.Timeout = TimeSpan.FromSeconds(settings.HttpTimeoutSeconds);
	})
	.ConfigurePrimaryHttpMessageHandler(() => CreateBackendHandler(settings))
	.AddHttpMessageHandler(() => new BasicAuthHandler(settings.I3XUsername, settings.I3XPassword));

	services.AddHttpClient(OpcUaWebApiClient.HttpClientName, client =>
	{
		client.BaseAddress = new Uri(settings.OpcUaWebApiBaseUrl + "/");
		client.Timeout = TimeSpan.FromSeconds(settings.HttpTimeoutSeconds);
	})
	.ConfigurePrimaryHttpMessageHandler(() => CreateBackendHandler(settings))
	.AddHttpMessageHandler(() => new BasicAuthHandler(settings.OpcUaWebApiUsername, settings.OpcUaWebApiPassword));

	services.AddSingleton<I3XClient>();
	services.AddSingleton<OpcUaWebApiClient>();
}

/// <summary>
/// Builds the outbound handler used for both backends, applying whatever certificate trust
/// has been configured.
///
/// Default behaviour is ordinary system-root validation. The two overrides exist because the
/// reference solution issues its own certificates, which a container does not trust by
/// default - pinning a CA keeps verification on, whereas disabling validation does not.
/// </summary>
static HttpMessageHandler CreateBackendHandler(ServerSettings settings)
{
	SocketsHttpHandler handler = new();

	if (settings.AllowUntrustedBackendCerts)
	{
		handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
		return handler;
	}

	if (!string.IsNullOrEmpty(settings.BackendCaCertPath))
	{
		// Verify against the system roots PLUS this CA, rather than replacing the trust
		// store, so public certificates keep working alongside privately issued ones.
		X509Certificate2Collection extraRoots = [];
		extraRoots.ImportFromPemFile(settings.BackendCaCertPath);

		handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, sslErrors) =>
		{
			if (sslErrors == System.Net.Security.SslPolicyErrors.None)
			{
				return true;
			}

			if (certificate is null)
			{
				return false;
			}

			using X509Chain chain = new();
			chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
			chain.ChainPolicy.CustomTrustStore.AddRange(extraRoots);
			chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

			return chain.Build(new X509Certificate2(certificate));
		};
	}

	return handler;
}

/// <summary>
/// Loads the server certificate from whichever form was configured: PKCS#12 (.pfx), or a
/// PEM certificate/key pair as mounted by a Kubernetes TLS secret.
/// </summary>
static X509Certificate2 LoadServerCertificate(ServerSettings settings)
{
	if (!string.IsNullOrEmpty(settings.TlsCertPath))
	{
		return X509CertificateLoader.LoadPkcs12FromFile(settings.TlsCertPath, settings.TlsCertPassword);
	}

	X509Certificate2 fromPem = X509Certificate2.CreateFromPemFile(settings.TlsPemPath!, settings.TlsKeyPath!);

	if (!OperatingSystem.IsWindows())
	{
		return fromPem;
	}

	// On Windows, Kestrel cannot use the ephemeral key that CreateFromPemFile produces;
	// round-tripping through PKCS#12 gives it a key handle it can actually bind to.
	using (fromPem)
	{
		return X509CertificateLoader.LoadPkcs12(fromPem.Export(X509ContentType.Pkcs12), null);
	}
}

/// <summary>
/// stdio transport, used when a desktop client such as Claude Desktop launches this binary
/// as a child process and speaks MCP over the pipe.
///
/// This deliberately uses the PLAIN GENERIC HOST rather than WebApplication: a web host
/// would start Kestrel and take over console lifetime handling, which prevents the stdio
/// transport from writing its responses to stdout at all.
/// </summary>
static async Task RunStdioAsync(ServerSettings settings, string[] args)
{
	HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

	// stdout carries the MCP protocol, so every log line must go to stderr or the client
	// sees a corrupted stream and reports the server as failed.
	builder.Logging.ClearProviders();
	builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

	AddCommonServices(builder.Services, settings);

	builder.Services
		.AddMcpServer(options => options.ServerInfo = new() { Name = "UA-CloudAI", Version = "1.0.0" })
		.WithStdioServerTransport()
		.WithToolsFromAssembly();

	await builder.Build().RunAsync().ConfigureAwait(false);
}

/// <summary>
/// Streamable HTTP transport on <see cref="ServerSettings.Port"/>, used for Docker, Kubernetes,
/// MCP Inspector and any browser-based MCP test client.
/// </summary>
static async Task RunHttpAsync(ServerSettings settings, string[] args)
{
	WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

	AddCommonServices(builder.Services, settings);

	builder.Services
		.AddMcpServer(options => options.ServerInfo = new() { Name = "UA-CloudAI", Version = "1.0.0" })
		.WithHttpTransport()
		.WithToolsFromAssembly();

	builder.WebHost.ConfigureKestrel(kestrel =>
		kestrel.ListenAnyIP(settings.Port, listen =>
		{
			if (settings.TlsConfigured)
			{
				listen.UseHttps(LoadServerCertificate(settings));
			}
		}));

	WebApplication app = builder.Build();

	ILogger startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

	if (!settings.InboundAuthConfigured)
	{
		startupLogger.LogWarning(
			"MCP_USERNAME / MCP_PASSWORD are not set, so the MCP endpoint is UNAUTHENTICATED. " +
			"That is acceptable only on a trusted network or for local testing.");
	}
	else if (!settings.TlsConfigured)
	{
		// Basic auth transmits reversible credentials on every request, so without TLS they
		// are readable by anyone on the network path.
		startupLogger.LogWarning(
			"Basic authentication is enabled but TLS is NOT configured, so credentials are sent " +
			"in cleartext on every request. Set MCP_TLS_CERT_PATH (or MCP_TLS_PEM_PATH and " +
			"MCP_TLS_KEY_PATH), or terminate TLS in front of this server.");
	}

	if (settings.AllowUntrustedBackendCerts)
	{
		startupLogger.LogWarning(
			"ALLOW_UNTRUSTED_BACKEND_CERTS is enabled: backend certificates are NOT validated, " +
			"which removes protection against an active man-in-the-middle. Prefer " +
			"BACKEND_CA_CERT_PATH, which keeps validation enabled.");
	}

	startupLogger.LogInformation("I3X backend:            {Url}", settings.I3XBaseUrl);
	startupLogger.LogInformation("OPC UA Web API backend: {Url}", settings.OpcUaWebApiBaseUrl);
	startupLogger.LogInformation("MCP endpoint:           {Scheme}://0.0.0.0:{Port}/mcp", settings.Scheme, settings.Port);

	// Mapped before the auth middleware so Kubernetes probes can reach it without
	// credentials; a probe that cannot authenticate would restart a healthy container.
	app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

	app.UseMiddleware<BasicAuthMiddleware>();
	app.MapMcp("/mcp");

	await app.RunAsync().ConfigureAwait(false);
}
