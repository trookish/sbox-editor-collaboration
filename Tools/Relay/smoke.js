// Smoke test for the STANDALONE relay executable (Tools/Relay). The in-process RelayTests map their own
// endpoint, so nothing else exercises Program.cs: the /health route, the /relay mapping, the startup
// options and the access-key wiring. Node's built-in WebSocket client is a different implementation from
// the .NET client the other tests use, which is a useful second opinion on the wire behaviour.
const crypto = require('crypto');
const base = process.argv[2] || 'ws://127.0.0.1:8080/relay';
// The access key the relay under test was started with. Pass it as RELAY_KEY=... when testing a server that uses your own key.
const accessKey = process.env.RELAY_KEY || 'smoke-key-1';
// wss:// must map to https:// as well; replacing only ws:// left a wss:// address unfetchable, which looked like a relay failure.
const httpBase = base.replace(/^wss:\/\//, 'https://').replace(/^ws:\/\//, 'http://').replace('/relay', '');

function hkdf(secret, label, length) {
  return Buffer.from(crypto.hkdfSync('sha256', secret, Buffer.alloc(32), Buffer.from('TeamCreate/relay/' + label), length));
}
// InviteCode.HkdfSha256 with a null salt uses 32 zero bytes, which is what hkdfSync is given above.
const secret = crypto.randomBytes(16);
const room = hkdf(secret, 'room', 16).toString('hex');
const hostProof = hkdf(secret, 'host-proof', 32).toString('hex');
const guestProof = hkdf(secret, 'guest-proof', 32).toString('hex');

function open(url) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(url);
    ws.binaryType = 'arraybuffer';
    const inbox = [];
    let waiter = null;
    ws.onmessage = (e) => { inbox.push(e.data); if (waiter) { const w = waiter; waiter = null; w(); } };
    ws.onerror = (e) => reject(new Error('socket error'));
    ws.onopen = () => resolve({
      ws,
      next: async (ms = 4000) => {
        if (!inbox.length) await new Promise((r) => { waiter = r; setTimeout(r, ms); });
        return inbox.shift();
      },
    });
  });
}

let failures = 0;
function check(ok, name) { console.log((ok ? 'PASS: ' : 'FAIL: ') + name); if (!ok) failures++; }

(async () => {
  const health = await (await fetch(httpBase + '/health')).json();
  check(health.status === 'ok' && health.rooms === 0, 'GET /health answers ok with no rooms');

  const noKey = await open(base);
  noKey.ws.send(JSON.stringify({ t: 'host', v: 1, room, host: hostProof, guest: guestProof }));
  const refusal = JSON.parse(await noKey.next());
  check(refusal.t === 'error' && refusal.code === 'bad-key', 'a host without the configured access key is refused with bad-key');

  const host = await open(base);
  host.ws.send(JSON.stringify({ t: 'host', v: 1, room, host: hostProof, guest: guestProof, key: accessKey }));
  const ok = JSON.parse(await host.next());
  check(ok.t === 'ok', 'a host with the right access key registers its room');
  check((await (await fetch(httpBase + '/health')).json()).rooms === 1, '/health reports the room');

  const badGuest = await open(base);
  badGuest.ws.send(JSON.stringify({ t: 'guest', v: 1, room, proof: crypto.randomBytes(32).toString('hex') }));
  check(JSON.parse(await badGuest.next()).code === 'bad-proof', 'a guest with a wrong proof is refused with bad-proof');

  const guest = await open(base);
  guest.ws.send(JSON.stringify({ t: 'guest', v: 1, room, proof: guestProof }));
  check(JSON.parse(await guest.next()).t === 'ok', 'a guest with the right proof is admitted');
  const opened = new Uint8Array(await host.next());
  check(opened.length === 5 && opened[0] === 2, 'the host is told a channel opened (frame type 2, five-byte header)');
  const channel = opened.slice(1, 5);

  guest.ws.send(new Uint8Array([1, 2, 3, 4, 5]));
  const data = new Uint8Array(await host.next());
  check(data[0] === 1 && Buffer.from(data.slice(1, 5)).equals(Buffer.from(channel)) && Buffer.from(data.slice(5)).equals(Buffer.from([1, 2, 3, 4, 5])),
    'bytes from the guest reach the host as a Data frame tagged with the guest channel');
  host.ws.send(Buffer.concat([Buffer.from([1]), Buffer.from(channel), Buffer.from([9, 8, 7])]));
  const back = new Uint8Array(await guest.next());
  check(Buffer.from(back).equals(Buffer.from([9, 8, 7])), 'bytes from the host reach the guest untagged');

  const garbage = await open(base);
  garbage.ws.send('not json');
  check(JSON.parse(await garbage.next()).code === 'bad-hello', 'garbage as a first message is refused');

  const plain = await fetch(httpBase + '/relay');
  check(plain.status === 426, 'a plain HTTP request to the relay path is told to upgrade');

  for (const s of [host, guest]) s.ws.close();
  console.log(failures === 0 ? 'relay smoke passed' : failures + ' relay smoke check(s) failed');
  process.exit(failures === 0 ? 0 : 1);
})().catch((e) => { console.log('FAIL: ' + e.message); process.exit(1); });
