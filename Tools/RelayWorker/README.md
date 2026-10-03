# Shared relay on Cloudflare Workers

The relay that lets two friends anywhere in the world join the same session with no server of their own. It is a Cloudflare Worker with one Durable Object per session, running on Cloudflare's free plan. It speaks the same protocol as `Tools/Relay` (the .NET relay), so the editor library does not know which one it is talking to.

**It cannot read your sessions.** Everything between the two editors is AES-256-GCM sealed under a key derived from the invite secret, which never reaches the Worker. What it does see: a room id and two proofs (one-way derivations of that secret), IP addresses, message sizes and timing.

## Deploy it once

The editor can do this itself: the **Relay** card in the Collaboration dock opens Cloudflare's token page, reads the token from the clipboard and deploys this Worker (the copy embedded in `Libraries/collaboration/Editor/Core/RelayWorkerSource.cs`) to the person's own account. The steps below do the same from a terminal.

After changing `src/index.js`, run `npm run embed` so the editor's copy matches; the library's test suite fails when they differ.

```bash
cd Tools/RelayWorker
npm install
npm run deploy
```

`npm run deploy` signs you in to Cloudflare if needed (a browser tab opens; a free account is enough), deploys the Worker, checks that its `/health` answers, and writes the address into `Libraries/collaboration/Editor/Core/RelayDefaults.cs`. Commit that change and publish the library; from then on nobody who installs it has anything to set up.

If the deploy stops, its message says why. The two usual causes are a Cloudflare account with no `workers.dev` name yet (pick one at dash.cloudflare.com → Workers & Pages) and Durable Object terms not yet accepted on the free plan (the same page offers it).

## Limits and cost

Cloudflare bills incoming WebSocket messages at 20 to 1 against 100,000 requests a day on the free plan (its pricing page, Durable Objects). Measured with the library's real code (`dotnet run --project Tools/RelayTests -- measure ws://localhost:8787/relay 20`, add `--moving` or `--legacy`):

| Two-person session | Messages into the relay | Billed requests per hour | Session-hours per day inside the free allowance, all users together |
| --- | --- | --- | --- |
| Before this change (presence sent every 100 ms even when nothing moved) | 50 per second | 9,000 | about 11 |
| Idle now | 2.5 per second | 450 | about 220 |
| Both people moving their cameras | 18 per second | 3,285 | about 30 |

Those are session-hours across **everyone** using the shared relay, and they are for two people; a session with more people costs more. If the free allowance is reached, Cloudflare refuses further messages until 00:00 UTC and sessions on the shared relay stop; the fix is the paid Workers plan (about $5 a month) or your own relay. **This has not been observed on a real deployment**; the figures come from a local run of the same Worker code.

The Worker also caps a room at three guests (four participants with the host), a message at 16 KiB, and a connection at 8 MB/s sustained. Set `RELAY_ACCESS_KEYS` (`wrangler secret put RELAY_ACCESS_KEYS`) if you want only hosts holding a key to be able to create rooms; the library reads the key from `.collaboration/relay-access-key.txt`.

## Tests

```bash
npm run contract            # records a real `wrangler deploy` against a local stand-in and checks the editor's deploy sends the same calls (19 checks)
npm run dev                # terminal 1: the real Cloudflare runtime, locally, on :8787
npm test                   # terminal 2: 18 checks with plain WebSocket clients
dotnet run --project ../RelayTests -- worker ws://localhost:8787/relay      # 24 checks with the library's own client code
```

The .NET suite is the same one the .NET relay passes, so the two implementations are held to one contract.
