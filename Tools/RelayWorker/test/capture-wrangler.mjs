// Records what Wrangler (Cloudflare's own deploy tool) actually sends when it deploys this Worker, by pointing it at a local stand-in for Cloudflare's API.
// The editor's own deploy (Libraries/collaboration/Editor/Core/RelayDeployer.cs) is meant to send the same calls; contract.test.mjs compares the two.
//
//     node test/capture-wrangler.mjs            writes test/wrangler-capture.json
//
// It needs no Cloudflare account and sends nothing to the internet: every request goes to 127.0.0.1.

import { createServer } from "node:http";
import { spawn } from "node:child_process";
import { writeFileSync, mkdtempSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, "..");
const ACCOUNT = "0123456789abcdef0123456789abcdef";
const TOKEN = "cfut_CaptureTokenAbCdEfGhIjKlMnOpQrStUvWxYz012345";
const calls = [];

function ok(result) {
	return JSON.stringify({ success: true, errors: [], messages: [], result });
}

const server = createServer((req, res) => {
	const chunks = [];
	req.on("data", (c) => chunks.push(c));
	req.on("end", () => {
		const body = Buffer.concat(chunks);
		const path = req.url.split("?")[0];
		calls.push({ method: req.method, url: req.url, headers: req.headers, body: body.toString("latin1"), bodyBytes: body.length });
		let result = {};
		let status = 200;
		if (req.method === "GET" && path.endsWith("/secrets")) result = [];
		else if (req.method === "GET" && path.endsWith("/deployments")) result = { deployments: [] };
		else if (req.method === "GET" && path.includes("/workers/workers/")) result = { id: "collab-relay", name: "collab-relay", subdomain: { enabled: false, previews_enabled: false } };
		else if (req.method === "GET" && path.endsWith("/settings")) result = { bindings: [], compatibility_date: "2026-09-01" };
		else if (req.method === "GET" && path.endsWith("/workers/subdomain")) result = { subdomain: "capture" };
		else if (req.method === "GET" && path.includes("/workers/services/")) {
			res.writeHead(404, { "content-type": "application/json" });
			res.end(JSON.stringify({ success: false, errors: [{ code: 10090, message: "not found" }], messages: [], result: null }));
			return;
		} else if (req.method === "PUT" && path.includes("/workers/scripts/")) {
			result = { id: "collab-relay", startup_time_ms: 1, has_modules: true, migration_tag: "v1", etag: "x" };
		} else if (req.method === "POST" && path.endsWith("/subdomain")) result = { enabled: true, previews_enabled: false };
		else if (req.method === "GET" && path.endsWith("/accounts")) result = [{ id: ACCOUNT, name: "Capture" }];
		res.writeHead(status, { "content-type": "application/json" });
		res.end(ok(result));
	});
});

await new Promise((r) => server.listen(0, "127.0.0.1", r));
const port = server.address().port;
const scratch = mkdtempSync(join(tmpdir(), "wrangler-capture-"));
const npx = process.platform === "win32" ? "npx.cmd" : "npx";
// Asynchronous on purpose: this process is also the stand-in server, and a synchronous wait would stop it answering.
const run = await new Promise((resolveRun) => {
	const child = spawn(npx, ["wrangler", "deploy", "--outdir", join(scratch, "out")], {
	cwd: root,
	shell: process.platform === "win32",
	env: {
		...process.env,
		CLOUDFLARE_API_BASE_URL: "http://127.0.0.1:" + port + "/client/v4",
		CLOUDFLARE_API_TOKEN: TOKEN,
		CLOUDFLARE_ACCOUNT_ID: ACCOUNT,
		WRANGLER_SEND_METRICS: "false",
		CI: "true",
		WRANGLER_LOG_PATH: scratch,
		XDG_CONFIG_HOME: scratch,
	},
	});
	let output = "";
	child.stdout.on("data", (d) => (output += d));
	child.stderr.on("data", (d) => (output += d));
	const timer = setTimeout(() => child.kill(), 240000);
	child.on("close", (status) => {
		clearTimeout(timer);
		resolveRun({ status, stdout: output, stderr: "" });
	});
});
server.close();

console.log(((run.stdout || "") + (run.stderr || "")).split("\n").slice(-25).join("\n"));
const relevant = calls.filter((c) => c.url.includes("/workers/"));
writeFileSync(join(here, "wrangler-capture.json"), JSON.stringify({ exit: run.status, token: TOKEN, account: ACCOUNT, calls: relevant }, null, "\t"));
console.log("wrangler exit " + run.status + "; captured " + calls.length + " calls, " + relevant.length + " about workers:");
for (const c of relevant) console.log("  " + c.method + " " + c.url + " (" + c.bodyBytes + " bytes)");
if (run.status !== 0) console.log((run.stdout || "") + (run.stderr || ""));
