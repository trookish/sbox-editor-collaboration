# Collaboration relay image.
#
# Build from the WORKSPACE ROOT, because the relay compiles two shared files from the library so the wire
# contract cannot drift:
#
#   docker build -f Tools/Relay/Dockerfile -t collab-relay .
#
# The image listens on plain HTTP/WebSocket on port 8080 and is meant to sit behind a TLS reverse proxy
# (see docker-compose.yml). It holds no secrets: the access keys arrive as an environment variable at run time.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Tools/Relay/ Tools/Relay/
COPY Libraries/collaboration/Editor/Core/RelayProtocol.cs Libraries/collaboration/Editor/Core/RelayProtocol.cs
COPY Libraries/collaboration/Editor/Core/InviteCode.cs Libraries/collaboration/Editor/Core/InviteCode.cs
RUN dotnet publish Tools/Relay/Relay.csproj -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS run
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# The aspnet image ships an unprivileged user; the relay needs no privileges and writes nothing to disk.
USER $APP_UID
ENTRYPOINT ["dotnet", "TeamCreate.Relay.dll"]
