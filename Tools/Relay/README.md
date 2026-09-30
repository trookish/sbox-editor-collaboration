# Collaboration relay

> **Most people do not need this.** The library ships a shared relay that runs on Cloudflare's free plan with nothing to host: see `Tools/RelayWorker`. Use this .NET relay only when you want to run the relay on your own machine or server.

A small forwarder that lets two friends behind home routers edit the same project. Both editors dial **out** to
this server over a WebSocket, so neither needs a forwarded port, UPnP, or a public address. The relay pairs the host
with each guest and forwards their bytes. It **cannot read them**: everything between the two editors is
AES-256-GCM sealed under a key the relay never receives.

## What it knows and what it does not

| The relay knows | The relay never has |
| --- | --- |
| A room id (16 bytes, one-way derived from the invite secret) | The invite secret |
| Two proofs derived from the secret with different labels | The session key or any plaintext |
| The client IP addresses that connect | Scene data, object names, Steam tokens, avatars |
| How many bytes each connection sends | Anything inside the sealed frames |

A guest is admitted only by presenting the proof for the room; a host by presenting an **access key** (if you set
one) and the room's host proof. The relay is not what admits a collaborator to the project: the host still applies
its own gates (Steam friend, organization member, library version, project manifest, identity verification) to every
guest that arrives through it.

## Run it

```bash
cd Tools/Relay
RELAY_ACCESS_KEYS="a-long-random-key" ASPNETCORE_URLS="http://0.0.0.0:8080" dotnet run -c Release
```

Requires the .NET 10 runtime. `GET /health` returns `{"status":"ok","rooms":N}`.

| Variable | Default | Meaning |
| --- | --- | --- |
| `RELAY_ACCESS_KEYS` | none | Comma-separated keys a **host** must present. **Set at least one before exposing the relay to the internet**; with none, anyone who can reach it can open rooms (the relay logs a warning at start). |
| `RELAY_MAX_ROOMS` | 64 | Live sessions at once. |
| `RELAY_MAX_GUESTS_PER_ROOM` | 16 | Matches the library's 16-connection limit. |
| `RELAY_MAX_CONNECTIONS_PER_IP` | 32 | Simultaneous sockets from one address. |
| `RELAY_MAX_BYTES_PER_SECOND` | 8388608 | Sustained rate per connection; a 16 MiB burst is allowed so one large scene snapshot is not mistaken for abuse. |
| `RELAY_TRUST_FORWARDED_FOR` | false | Read the client address from `X-Forwarded-For`. Enable **only** behind a reverse proxy you control. |
| `ASPNETCORE_URLS` | `http://localhost:5000` | Listen address. |

Other limits (hello timeout 10 s, idle timeout 45 s, ten wrong proofs per minute per address, 512 queued frames per
receiver, eight unauthenticated sockets per address) are defaults in `RelayOptions.cs`; they are not read from the
environment, so change them there and rebuild.

## Deploy it (about 15 minutes)

You need: a small Linux server with a public IP (any 1 vCPU / 512 MB VPS is enough; the relay forwards bytes and keeps nothing on disk), a domain name you control, and Docker with the Compose plugin on that server.

**1. Point a DNS name at the server.** Create an `A` record, for example `relay.yourdomain.com`, pointing at the server's public IP. The editor refuses a raw IP address and refuses plain `ws://` to another machine, so this step is not optional. Wait until `nslookup relay.yourdomain.com` returns the server's IP.

**2. Open ports 80 and 443** on the server's firewall (Caddy needs 80 to obtain the certificate and redirect to HTTPS, 443 for the relay). Nothing else needs to be open; the relay container publishes no port of its own.

**3. Copy the relay to the server.** Copy `Tools/Relay/` and the two files it compiles from the library, keeping the folder layout:

```
Tools/Relay/                                        (whole folder)
Libraries/collaboration/Editor/Core/RelayProtocol.cs
Libraries/collaboration/Editor/Core/InviteCode.cs
```

**4. Set the two values.** On the server, in `Tools/Relay/`:

```bash
cp .env.example .env
# edit .env: RELAY_HOST=relay.yourdomain.com  and  RELAY_ACCESS_KEYS=<a long random key>
openssl rand -hex 32          # one way to make the key
```

Compose refuses to start if either is empty, so the relay can never come up open to the internet by mistake. `.env` is git-ignored; never commit it.

**5. Start it.**

```bash
docker compose up -d --build
docker compose logs -f caddy      # wait for "certificate obtained successfully"
curl https://relay.yourdomain.com/health     # {"status":"ok","rooms":0}
```

**6. Test it from your PC** (needs Node):

```bash
RELAY_KEY=<the same key you put in .env> node Tools/Relay/smoke.js wss://relay.yourdomain.com/relay
```

It ends with `relay smoke passed`. It opens a throwaway room with a random secret, checks that a wrong key and a wrong proof are refused and that bytes cross in both directions through your TLS address, then closes it. On PowerShell use `$env:RELAY_KEY="..."; node Tools/Relay/smoke.js wss://...`.

**7. Point the project at it** (next section), then host a session and confirm the dock says *Relay connected*.

**Updating:** copy the new files over and run `docker compose up -d --build` again. Caddy keeps its certificate in a volume. **Stopping:** `docker compose down`. **Rotating the key:** change `RELAY_ACCESS_KEYS` in `.env`, run `docker compose up -d`, and update `.collaboration/relay-access-key.txt` on the host's machine.

**No server of your own?** Any host that runs a Docker container and gives you an HTTPS name with WebSocket support works with the same image (`docker build -f Tools/Relay/Dockerfile -t collab-relay .` from the workspace root, then run it with `RELAY_ACCESS_KEYS` set and the platform's HTTPS in front, and `RELAY_TRUST_FORWARDED_FOR=true` only if that platform sets `X-Forwarded-For`). A quick tunnel such as `cloudflared tunnel --url http://localhost:8080` also produces an `https://` name in seconds and is fine for a one-off test, but its address changes each run, so you would have to update `RelayUrl` on both machines every time.

**Verified:** the Dockerfile builds; the container runs as a non-root user and answers `/health`; the full smoke test passes against it directly and through Caddy's TLS with the WebSocket upgrade; the relay port is not reachable except through Caddy; Compose refuses to start with the values missing. **Not verified:** a real domain with a Let's Encrypt certificate on a public server (this ran on `localhost` with Caddy's own local certificate), and any host other than this PC.

## Without Docker

```bash
cd Tools/Relay
RELAY_ACCESS_KEYS="a-long-random-key" ASPNETCORE_URLS="http://127.0.0.1:8080" dotnet run -c Release
```

Requires the .NET 10 runtime. Put a TLS proxy in front of it. Caddy needs one line, `relay.yourdomain.com { reverse_proxy 127.0.0.1:8080 }`, and sets `X-Forwarded-For`; then also set `RELAY_TRUST_FORWARDED_FOR=true` and keep the relay bound to `127.0.0.1` so it cannot be reached around the proxy. For nginx, add `proxy_http_version 1.1;`, `proxy_set_header Upgrade $http_upgrade;`, `proxy_set_header Connection "upgrade";` and a `proxy_read_timeout` of at least 60 s.

## Tell the project about it

1. Put the address in the shared, committed `ProjectSettings/collaboration.json` (not a secret):

   ```json
   "RelayUrl": "wss://relay.example.com/relay"
   ```

2. Give **the host's machine** the access key in an untracked file, one line:
   `.collaboration/relay-access-key.txt`. Guests need nothing. `.collaboration/` is already in `.gitignore`.

3. Host a session. The dock shows *Relay connected* and the invite code now names the relay. A friend pastes the code
   and joins; the editor tries the local network first, then the relay, then the direct address.

## Local testing without a domain

The engine allows an unencrypted `ws://localhost:<port>` for these ports only: **80, 443, 8080, 8443**. Run the relay on
`http://127.0.0.1:8080` and set `"RelayUrl": "ws://localhost:8080/relay"`. Two editors on one machine cannot both sign
into one Steam account, so this proves the relay path, not cross-account admission; see the README's validation notes.

## Tests

```bash
dotnet run --project Tools/RelayTests/RelayTests.csproj   # 44 integration tests: real relay, real Wire, replay/drop tampering
node Tools/Relay/smoke.js                                  # against a running relay: independent WebSocket client
```
