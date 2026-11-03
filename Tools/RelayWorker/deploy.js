// One command to put the collaboration relay on the internet, for free:
//
//     npm install
//     npm run deploy
//
// It signs you in to Cloudflare if you are not already (a browser tab opens; a free account is enough), deploys the Worker, reads back the address it was
// given, and writes that address into the library so every project uses it by default. Run it once; after that nobody needs to set anything up.

import { spawnSync } from "node:child_process";
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const defaultsFile = resolve(here, "../../Libraries/collaboration/Editor/Core/RelayDefaults.cs");
const npx = process.platform === "win32" ? "npx.cmd" : "npx";

function run(args, opts = {}) {
	return spawnSync(npx, ["wrangler", ...args], { cwd: here, encoding: "utf8", shell: process.platform === "win32", env: { ...process.env, WRANGLER_SEND_METRICS: "false" }, ...opts });
}

function say(text) {
	console.log(text);
}

function fail(text) {
	console.error("\n" + text);
	process.exit(1);
}

if (!existsSync(join(here, "node_modules"))) {
	fail("Run `npm install` in Tools/RelayWorker first, then `npm run deploy`.");
}
if (!existsSync(defaultsFile)) {
	fail("Could not find " + defaultsFile + ". Run this from a checkout of the collaboration library.");
}

say("Checking your Cloudflare sign-in...");
let who = run(["whoami"]);
if (who.status !== 0 || /not authenticated|not logged in/i.test((who.stdout || "") + (who.stderr || ""))) {
	say("You are not signed in. A browser tab will open; sign in or create a free account, then come back here.");
	const login = run(["login"], { stdio: "inherit" });
	if (login.status !== 0) {
		fail("Sign-in did not finish. Run `npm run deploy` again when you are ready.");
	}
}

say("\nDeploying the relay...");
// This is the relay every install of the library uses by default, so it runs in shared mode: no access key for hosts, a per-address connection cap and
// idle rooms closed instead (see README.md, Shared mode). A relay a person sets up for themselves from the editor is not deployed by this script.
const deploy = run(["deploy", "--var", "RELAY_MODE:shared"]);
const output = (deploy.stdout || "") + (deploy.stderr || "");
if (deploy.status !== 0) {
	console.error(output);
	fail(
		"The deploy did not finish. The message above says why. Two common causes:\n" +
			"  - Your account has no workers.dev subdomain yet: open https://dash.cloudflare.com, choose Workers & Pages, and pick a name when asked.\n" +
			"  - Durable Objects need the free plan's terms accepted once: the same page will offer it."
	);
}

const match = output.match(/https:\/\/([a-z0-9-]+(?:\.[a-z0-9-]+)*\.workers\.dev)/i);
if (!match) {
	console.error(output);
	fail("The deploy succeeded but no workers.dev address was printed, so the library was not updated. Add the address above to RelayDefaults.BuiltInUrls by hand as wss://<address>/relay.");
}
const relayUrl = "wss://" + match[1] + "/relay";

// Prove the address answers before writing it anywhere, so a typo or a slow first deploy cannot leave the library pointing at nothing.
say("\nChecking " + match[1] + " answers...");
let healthy = false;
for (let attempt = 0; attempt < 10 && !healthy; attempt++) {
	try {
		const response = await fetch("https://" + match[1] + "/health");
		const body = await response.json();
		healthy = response.ok && body.status === "ok" && body.service === "collab-relay";
	} catch {
		// Not resolvable yet; a new Worker takes a few seconds to appear.
	}
	if (!healthy) {
		await new Promise((r) => setTimeout(r, 3000));
	}
}
if (!healthy) {
	fail("Deployed, but https://" + match[1] + "/health did not answer in 30 seconds. Try it in a browser in a minute; the library was not changed. Then re-run `npm run deploy`.");
}

const source = readFileSync(defaultsFile, "utf8");
// The new relay goes first in RelayDefaults.BuiltInUrls; any earlier deployment stays in the list after it, so a library already in people's hands
// that still names the old address keeps working, and the same address is never listed twice.
const pattern = /public static readonly string\[\] BuiltInUrls = \{([^}]*)\};/;
const found = source.match(pattern);
if (!found) {
	fail("RelayDefaults.cs no longer has the BuiltInUrls line this script edits; add " + relayUrl + " to it by hand.");
}
const existing = [...found[1].matchAll(/"([^"]*)"/g)].map((m) => m[1]).filter((u) => u !== relayUrl);
const list = [relayUrl, ...existing].map((u) => JSON.stringify(u)).join(", ");
writeFileSync(defaultsFile, source.replace(pattern, `public static readonly string[] BuiltInUrls = { ${list} };`));

say("\nDone. Your relay is live at " + relayUrl);
say("The library now uses it by default. Rebuild/commit the library so your friends get it:");
say("  - people who install the library need nothing else: Host, send the code, Join.");
