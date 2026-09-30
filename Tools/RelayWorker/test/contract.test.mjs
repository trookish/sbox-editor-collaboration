// Holds the editor's Cloudflare deploy to what Wrangler (Cloudflare's own tool) actually sends for this Worker.
//
// capture-wrangler.mjs runs a real `wrangler deploy` against a local stand-in and records every request. This test reads that recording and checks the
// facts the editor's own deploy (Libraries/collaboration/Editor/Core/CloudflareApi.cs and RelayDeployer.cs) depends on, by reading the C# source for the
// same values. If Wrangler changes what it sends, or the editor drifts from it, this fails and names the difference.
//
//     npm run contract        (records wrangler with capture-wrangler.mjs, then checks the recording)

import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
let capture;
try {
	capture = JSON.parse(readFileSync(resolve(here, "wrangler-capture.json"), "utf8"));
} catch {
	console.error("No recording of wrangler found. Run `npm run contract`, which records one and then checks it.");
	process.exit(2);
}
const api = readFileSync(resolve(here, "../../../Libraries/collaboration/Editor/Core/CloudflareApi.cs"), "utf8");
const deployer = readFileSync(resolve(here, "../../../Libraries/collaboration/Editor/Core/RelayDeployer.cs"), "utf8");

let failed = 0;
function check(ok, name, detail = "") {
	console.log((ok ? "PASS" : "FAIL") + ": " + name + (ok ? "" : detail ? "  [" + detail + "]" : ""));
	if (!ok) failed++;
}

function constant(name) {
	const m = api.match(new RegExp("const string " + name + " = \"([^\"]*)\""));
	return m ? m[1] : null;
}

const route = (c) => c.method + " " + c.url.replace(capture.account, "<acct>");
const upload = capture.calls.find((c) => c.method === "PUT");
const toggle = capture.calls.find((c) => c.method === "POST");
const boundary = upload.headers["content-type"].match(/boundary=([^\s;]+)/)[1];
const parts = {};
for (const raw of upload.body.split("--" + boundary)) {
	const [head, ...rest] = raw.split("\r\n\r\n");
	const name = head.match(/name="([^"]+)"/);
	if (name) parts[name[1]] = { head, body: rest.join("\r\n\r\n").replace(/\r\n$/, "") };
}
const metadata = JSON.parse(parts.metadata.body);

check(capture.exit === 0, "the recorded wrangler deploy finished (exit 0)");
check(upload.headers.authorization === "Bearer " + capture.token, "wrangler authenticates with a bearer token");
check(route(upload).startsWith("PUT /client/v4/accounts/<acct>/workers/scripts/collab-relay?"), "wrangler uploads with PUT to /workers/scripts/collab-relay");
check(route(upload).endsWith(constant("UploadQuery")), "the editor sends wrangler's upload query", route(upload));
check(constant("ScriptName") === "collab-relay" && constant("MainModule") === metadata.main_module, "the script name and main module equal wrangler's", metadata.main_module);
check(/name="metadata"/.test(parts.metadata.head) && /name="index.js"; filename="index.js"/.test(parts["index.js"].head), "wrangler names its two parts metadata and index.js, with quoted names");
check(/application\/javascript\+module/.test(parts["index.js"].head), "wrangler sends the module as application/javascript+module");
check(
	/ContentDispositionHeaderValue\("form-data"\) \{ Name = Quote\("metadata"\) \}/.test(api) && /Name = Quote\(MainModule\), FileName = Quote\(MainModule\)/.test(api),
	"the editor quotes the part names the same way",
);
check(metadata.bindings.length === 1 && metadata.bindings[0].type === "durable_object_namespace" && metadata.bindings[0].name === constant("BindingName") && metadata.bindings[0].class_name === constant("DurableObjectClass"), "the Durable Object binding has wrangler's type, name and class");
check(metadata.compatibility_date === constant("CompatibilityDate"), "the compatibility date equals wrangler.jsonc's", metadata.compatibility_date);
check(metadata.migrations.new_tag === constant("MigrationTag") && metadata.migrations.steps[0].new_sqlite_classes[0] === constant("DurableObjectClass"), "a first deploy carries the v1 SQLite Durable Object migration");
check(/\["new_sqlite_classes"\] = new JsonArray \{ DurableObjectClass \}/.test(api) && /\["new_tag"\] = MigrationTag/.test(api), "the editor builds the same migration shape");
// Wrangler bundles the Worker (it rewrites the class into `export { Room, index_default as default }`); the editor uploads the source as written, which
// is also what `wrangler dev` runs. What must agree is that the module exports the Durable Object class the binding and migration name.
check(parts["index.js"].body.length > 1000 && /export\s*\{[^}]*\bRoom\b[^}]*\bdefault\b/s.test(parts["index.js"].body), "the module wrangler uploads exports the Room class and a default handler");
const embedded = readFileSync(resolve(here, "../../../Libraries/collaboration/Editor/Core/RelayWorkerSource.cs"), "utf8");
check(/export class Room extends DurableObject/.test(embedded) && /export default \{/.test(embedded), "the source the editor uploads exports the same Room class and default handler");
check(toggle.body === '{"enabled":true}', "wrangler switches the workers.dev address on with exactly {\"enabled\":true}", toggle.body);
check(toggle.headers["cloudflare-workers-script-api-date"] === constant("ScriptApiDate"), "wrangler selects the same script API date for that call");
check(/\["enabled"\] = true \}/.test(deployer) && !/previews_enabled/.test(deployer), "the editor sends the same body and no more");

const order = capture.calls.map(route).filter((r) => /services\/collab-relay|scripts\/collab-relay\?|scripts\/collab-relay\/subdomain|workers\/subdomain/.test(r));
check(order.some((r) => r.includes("/workers/services/collab-relay")) && order.some((r) => r.endsWith("/workers/subdomain")), "wrangler looks up the existing Worker and the account's workers.dev name before uploading");
check(capture.calls.indexOf(upload) < capture.calls.indexOf(toggle), "wrangler switches the address on after the upload");

console.log(failed === 0 ? "\nAll contract checks passed." : "\n" + failed + " contract check(s) failed.");
process.exit(failed === 0 ? 0 : 1);
