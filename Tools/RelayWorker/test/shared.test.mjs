// Shared mode of the relay Worker: no access key for hosts, a cap on new connections per address, and rooms that sit idle are closed. Run against a
// `wrangler dev` started with the shared settings, short enough to test:
//
//     npx wrangler dev --var RELAY_MODE:shared --var SHARED_ROOM_IDLE_SECONDS:3 --var SHARED_CONNECTS_PER_MINUTE:12
//     node test/shared.test.mjs ws://127.0.0.1:8787/relay
//
// The keyed mode is held to its contract by worker.test.mjs, which must run against a server started without these settings.

import { hkdfSync, randomBytes } from "node:crypto";
import { request } from "node:http";

const base = process.argv[2] || "ws://127.0.0.1:8787/relay";
const perMinute = Number(process.argv[3] || 12);

function derive(secret, label, length) {
	return Buffer.from(hkdfSync("sha256", secret, Buffer.alloc(32), Buffer.from("TeamCreate/relay/" + label), length));
}

function open(url) {
	return new Promise((resolve, reject) => {
		const ws = new WebSocket(url);
		ws.binaryType = "arraybuffer";
		const inbox = [];
		let waiter = null;
		let closed = null;
		const wake = () => {
			if (waiter) {
				const w = waiter;
				waiter = null;
				w();
			}
		};
		ws.onmessage = (e) => {
			inbox.push(e.data);
			wake();
		};
		ws.onclose = (e) => {
			closed = e.code;
			wake();
		};
		ws.onerror = () => reject(new Error("socket error"));
		ws.onopen = () =>
			resolve({
				ws,
				closedCode: () => closed,
				next: async (ms = 4000) => {
					if (!inbox.length) {
						await new Promise((r) => {
							waiter = r;
							setTimeout(r, ms);
						});
					}
					return inbox.shift();
				},
			});
	});
}

function upgradeStatus(address) {
	return new Promise((resolve, reject) => {
		const u = new URL(address.replace(/^ws:/, "http:"));
		const req = request(u, { headers: { Connection: "Upgrade", Upgrade: "websocket", "Sec-WebSocket-Version": "13", "Sec-WebSocket-Key": "dGhlIHNhbXBsZSBub25jZQ==" } }, (res) => {
			res.resume();
			resolve(res.statusCode);
		});
		req.on("upgrade", (res, socket) => {
			socket.destroy();
			resolve(res.statusCode);
		});
		req.on("error", reject);
		req.end();
	});
}

let failures = 0;
function check(ok, name) {
	console.log((ok ? "PASS: " : "FAIL: ") + name);
	if (!ok) failures++;
}

function roomFor(secret) {
	return {
		room: derive(secret, "room", 16).toString("hex"),
		host: derive(secret, "host-proof", 32).toString("hex"),
		guest: derive(secret, "guest-proof", 32).toString("hex"),
	};
}

const at = (r) => `${base}?room=${r}`;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

(async () => {
	// A host with no key at all is admitted: the shared relay needs no account and no key.
	const a = roomFor(randomBytes(16));
	const host = await open(at(a.room));
	host.ws.send(JSON.stringify({ t: "host", v: 1, room: a.room, host: a.host, guest: a.guest }));
	check(JSON.parse(await host.next()).t === "ok", "a host with no access key opens a room on the shared relay");

	// The rest of the room contract is the keyed one: a stranger cannot take the room, the right guest is admitted.
	const thief = await open(at(a.room));
	thief.ws.send(JSON.stringify({ t: "host", v: 1, room: a.room, host: randomBytes(32).toString("hex"), guest: a.guest }));
	check(JSON.parse(await thief.next()).code === "room-taken", "another host still cannot take a room on the shared relay");
	const guest = await open(at(a.room));
	guest.ws.send(JSON.stringify({ t: "guest", v: 1, room: a.room, proof: a.guest }));
	check(JSON.parse(await guest.next()).t === "ok", "a guest with the right proof is admitted on the shared relay");
	await host.next();

	// Idle expiry: nothing is sent for longer than the idle limit, so the room is closed and every socket is told why.
	await sleep(7000);
	const hostIdle = JSON.parse((await host.next(6000)) || "{}");
	check(hostIdle.code === "idle", "an idle room is closed and its host is told the code idle");
	const guestIdle = JSON.parse((await guest.next(4000)) || "{}");
	check(guestIdle.code === "idle", "the idle room's guest is told too");
	// The room was forgotten, so the same host registers it again at once.
	const back = await open(at(a.room));
	back.ws.send(JSON.stringify({ t: "host", v: 1, room: a.room, host: a.host, guest: a.guest }));
	check(JSON.parse(await back.next()).t === "ok", "the host reopens its room after the idle close");

	// A room in use is not closed: a guest keeps sending inside the idle limit.
	const b = roomFor(randomBytes(16));
	const busyHost = await open(at(b.room));
	busyHost.ws.send(JSON.stringify({ t: "host", v: 1, room: b.room, host: b.host, guest: b.guest }));
	await busyHost.next();
	const busyGuest = await open(at(b.room));
	busyGuest.ws.send(JSON.stringify({ t: "guest", v: 1, room: b.room, proof: b.guest }));
	await busyGuest.next();
	await busyHost.next();
	let stillOpen = true;
	for (let i = 0; i < 7; i++) {
		busyGuest.ws.send(new Uint8Array([i]));
		const frame = await busyHost.next(2000);
		stillOpen = stillOpen && frame instanceof ArrayBuffer && new Uint8Array(frame)[0] === 1;
		await sleep(800);
	}
	check(stillOpen && busyHost.closedCode() === null, "a room with traffic outlives the idle limit");

	for (const s of [host, thief, guest, back, busyHost, busyGuest]) s.ws.close();

	// Connection cap per address: past the configured count in a minute, upgrades are refused with 429 before a socket is opened.
	const statuses = [];
	for (let i = 0; i < perMinute + 2; i++) {
		statuses.push(await upgradeStatus(at(randomBytes(16).toString("hex"))));
	}
	check(statuses.includes(429), "past the per-minute connection cap, the shared relay answers 429");
	check(statuses.lastIndexOf(101) < statuses.indexOf(429), "connections under the cap are admitted and only the excess is refused");

	if (failures) {
		console.log(failures + " shared-mode checks failed");
		process.exit(1);
	}
	console.log("shared mode passed");
	process.exit(0);
})().catch((e) => {
	console.log("FAIL: " + e.message);
	process.exit(1);
});
