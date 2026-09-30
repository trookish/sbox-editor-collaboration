// Holds the Cloudflare Worker to the wire contract with plain WebSocket clients, independent of the .NET client, so a change to either side that breaks the
// contract is caught from two directions. Run against a local `wrangler dev`:
//
//     npm run dev            (in one terminal)
//     npm test               (in another)
//
// The .NET conformance run (dotnet run --project Tools/RelayTests -- worker ws://localhost:8787/relay) is the broader suite; this one is fast and has no
// dependencies beyond Node.

import { hkdfSync, randomBytes } from "node:crypto";
import { request } from "node:http";
import { request as secureRequest } from "node:https";

const base = process.argv[2] || "ws://127.0.0.1:8787/relay";
const http = base.replace(/^wss:/, "https:").replace(/^ws:/, "http:").replace(/\/relay$/, "");

function derive(secret, label, length) {
	return Buffer.from(hkdfSync("sha256", secret, Buffer.alloc(32), Buffer.from("TeamCreate/relay/" + label), length));
}

function open(url) {
	return new Promise((resolve, reject) => {
		const ws = new WebSocket(url);
		ws.binaryType = "arraybuffer";
		const inbox = [];
		let waiter = null;
		ws.onmessage = (e) => {
			inbox.push(e.data);
			if (waiter) {
				const w = waiter;
				waiter = null;
				w();
			}
		};
		let closed = null;
		ws.onclose = (e) => {
			closed = e.code;
			if (waiter) {
				const w = waiter;
				waiter = null;
				w();
			}
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

let failures = 0;
function check(ok, name) {
	console.log((ok ? "PASS: " : "FAIL: ") + name);
	if (!ok) failures++;
}

function upgradeStatus(address) {
	return new Promise((resolve, reject) => {
		const u = new URL(address.replace(/^wss:/, "https:").replace(/^ws:/, "http:"));
		const send = u.protocol === "https:" ? secureRequest : request;
		const req = send(u, { headers: { Connection: "Upgrade", Upgrade: "websocket", "Sec-WebSocket-Version": "13", "Sec-WebSocket-Key": "dGhlIHNhbXBsZSBub25jZQ==" } }, (res) => {
			res.resume();
			resolve(res.statusCode);
		});
		req.on("upgrade", (res) => resolve(res.statusCode));
		req.on("error", reject);
		req.end();
	});
}

const secret = randomBytes(16);
const room = derive(secret, "room", 16).toString("hex");
const hostProof = derive(secret, "host-proof", 32).toString("hex");
const guestProof = derive(secret, "guest-proof", 32).toString("hex");
const at = (r) => `${base}?room=${r}`;

(async () => {
	const health = await (await fetch(http + "/health")).json();
	check(health.status === "ok" && health.service === "collab-relay", "GET /health answers ok and names the service");
	const home = await fetch(http + "/");
	check(home.status === 200 && (await home.text()).includes("relay is running"), "GET / says the relay is running, so a person can check the address in a browser");
	check((await fetch(http + "/nope")).status === 404, "an unknown path is a 404");
	check((await fetch(http + "/relay?room=" + room)).status === 426, "a plain HTTP request to the relay path is told to upgrade");
	// Node's fetch will not send an Upgrade header, so these two use a raw request. The answer arrives as an ordinary HTTP response because the relay
	// refuses before any upgrade happens.
	check((await upgradeStatus(base)) === 400, "a socket with no room in the address is refused before it connects");
	check((await upgradeStatus(base + "?room=zzzz")) === 400, "a socket with a malformed room is refused before it connects");

	const host = await open(at(room));
	host.ws.send(JSON.stringify({ t: "host", v: 1, room, host: hostProof, guest: guestProof }));
	check(JSON.parse(await host.next()).t === "ok", "a host registers its room");

	const strangerRoom = randomBytes(16).toString("hex");
	const wrongRoomSocket = await open(at(strangerRoom));
	wrongRoomSocket.ws.send(JSON.stringify({ t: "guest", v: 1, room, proof: guestProof }));
	check(JSON.parse(await wrongRoomSocket.next()).code === "bad-hello", "a hello naming a different room than the address is refused");

	const badGuest = await open(at(room));
	badGuest.ws.send(JSON.stringify({ t: "guest", v: 1, room, proof: randomBytes(32).toString("hex") }));
	check(JSON.parse(await badGuest.next()).code === "bad-proof", "a guest with the wrong proof is refused");

	const guest = await open(at(room));
	guest.ws.send(JSON.stringify({ t: "guest", v: 1, room, proof: guestProof }));
	check(JSON.parse(await guest.next()).t === "ok", "a guest with the right proof is admitted");
	const opened = new Uint8Array(await host.next());
	check(opened.length === 5 && opened[0] === 2, "the host is told a channel opened");
	const channel = opened.slice(1, 5);

	guest.ws.send(new Uint8Array([1, 2, 3, 4, 5]));
	const data = new Uint8Array(await host.next());
	check(data[0] === 1 && Buffer.from(data.slice(1, 5)).equals(Buffer.from(channel)) && Buffer.from(data.slice(5)).equals(Buffer.from([1, 2, 3, 4, 5])), "guest bytes reach the host as a Data frame tagged with the channel");
	host.ws.send(Buffer.concat([Buffer.from([1]), Buffer.from(channel), Buffer.from([9, 8, 7])]));
	check(Buffer.from(new Uint8Array(await guest.next())).equals(Buffer.from([9, 8, 7])), "host bytes reach the guest untagged");

	const big = new Uint8Array(16 * 1024).fill(7);
	guest.ws.send(big);
	const bigAtHost = new Uint8Array(await host.next());
	check(bigAtHost.length === 5 + big.length, "a full 16 KiB chunk crosses intact");
	guest.ws.send(new Uint8Array(16 * 1024 + 1));
	await guest.next(1500);
	check(guest.closedCode() !== null, "a chunk over the limit closes the guest's socket");

	const garbage = await open(at(randomBytes(16).toString("hex")));
	garbage.ws.send("not json");
	check(JSON.parse(await garbage.next()).code === "bad-hello", "garbage as a first message is refused");

	// The refusal message arrives after ten seconds; the close frame follows within the runtime's own idle window (about ten more).
	const silent = await open(at(randomBytes(16).toString("hex")));
	const silentReply = await silent.next(12500);
	check(silentReply !== undefined && JSON.parse(silentReply).code === "bad-hello", "a socket that never says hello is refused after ten seconds");
	for (let waited = 0; silent.closedCode() === null && waited < 14000; waited += 500) {
		await new Promise((r) => setTimeout(r, 500));
	}
	check(silent.closedCode() !== null, "and the silent socket is closed within the runtime's idle window");

	host.ws.close();
	await new Promise((r) => setTimeout(r, 500));
	const late = await open(at(room));
	late.ws.send(JSON.stringify({ t: "guest", v: 1, room, proof: guestProof }));
	check(JSON.parse(await late.next()).code === "no-host", "after the host leaves the room is gone and a later joiner is told so");

	console.log(failures === 0 ? "worker smoke passed" : failures + " worker smoke check(s) failed");
	process.exit(failures === 0 ? 0 : 1);
})().catch((e) => {
	console.log("FAIL: " + e.message);
	process.exit(1);
});
