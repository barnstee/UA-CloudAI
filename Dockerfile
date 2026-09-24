# UA-CloudAI - MCP server over the I3X API and UA Cloud Action's OPC UA Web API.
#
# Multi-stage build. The runtime image is the ASP.NET Core one rather than the plain
# runtime because the server hosts an HTTP (Streamable HTTP) MCP transport.

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
# 5000 when serving plain HTTP, 5443 for the HTTPS example in the README.
EXPOSE 5000
EXPOSE 5443

# Run as a non-root user. The MCP server needs no write access to its own filesystem.
USER $APP_UID

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["UA-CloudAI.csproj", "./"]
RUN dotnet restore "UA-CloudAI.csproj"
COPY . .
RUN dotnet build "UA-CloudAI.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "UA-CloudAI.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Defaults suit the Cloud Initiative Reference Solution running in the "cloud" namespace.
# Override any of these at run time; see the README.
ENV MCP_TRANSPORT=http \
	MCP_PORT=5000 \
	ASPNETCORE_URLS=

ENTRYPOINT ["dotnet", "UA-CloudAI.dll"]
