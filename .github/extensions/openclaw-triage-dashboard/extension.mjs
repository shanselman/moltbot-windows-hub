import { execFile, execFileSync } from "node:child_process";
import { existsSync } from "node:fs";
import { createServer } from "node:http";
import { homedir } from "node:os";
import { dirname, isAbsolute, join, relative, resolve } from "node:path";
import { promisify } from "node:util";
import { fileURLToPath } from "node:url";
import {
    CanvasError,
    createCanvas,
    joinSession,
} from "@github/copilot-sdk/extension";
import {
    mergeLiveState,
    normalizeTriageInput,
} from "./triage-state.mjs";
import {
    requestHostMatches,
    requestItemAction,
    requestTokenMatches,
} from "./triage-actions.mjs";
import { renderDashboardHtml } from "./triage-ui.mjs";

const execFileAsync = promisify(execFile);
const instances = new Map();
const instanceStarts = new Map();
const extensionDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = dirname(dirname(dirname(extensionDirectory)));
let copilotSession;
let ghPath;

function pathIsInside(candidate, parent) {
    const child = resolve(candidate);
    const root = resolve(parent);
    const relativePath = relative(root, child);
    return relativePath === "" || (!relativePath.startsWith("..") && !isAbsolute(relativePath));
}

function resolveGhPath() {
    const candidates = [
        process.env.ProgramFiles && join(process.env.ProgramFiles, "GitHub CLI", "gh.exe"),
        process.env.LOCALAPPDATA && join(process.env.LOCALAPPDATA, "Programs", "GitHub CLI", "gh.exe"),
    ].filter(Boolean);

    const windowsDirectory = process.env.WINDIR ?? "C:\\Windows";
    const wherePath = join(windowsDirectory, "System32", "where.exe");
    if (existsSync(wherePath)) {
        try {
            const output = execFileSync(wherePath, ["gh.exe"], {
                cwd: homedir(),
                encoding: "utf8",
                stdio: ["ignore", "pipe", "ignore"],
                windowsHide: true,
            });
            candidates.push(...output.split(/\r?\n/).map((entry) => entry.trim()).filter(Boolean));
        } catch {
            // Fall through to the fixed installation candidates.
        }
    }

    const ghPath = candidates.find((candidate) =>
        existsSync(candidate) && !pathIsInside(candidate, repositoryRoot));
    if (!ghPath) {
        throw new Error("GitHub CLI was not found outside the repository");
    }
    return ghPath;
}

function getGhPath() {
    ghPath ??= resolveGhPath();
    return ghPath;
}

async function runGhJson(argumentsList) {
    let lastError;
    for (let attempt = 0; attempt < 2; attempt += 1) {
        try {
            const { stdout } = await execFileAsync(getGhPath(), argumentsList, {
                cwd: homedir(),
                encoding: "utf8",
                maxBuffer: 16 * 1024 * 1024,
                timeout: 45_000,
                windowsHide: true,
            });
            return JSON.parse(stdout);
        } catch (error) {
            lastError = error;
            const message = error instanceof Error ? error.message : String(error);
            if (attempt === 0 && /HTTP 50[234]|ETIMEDOUT|timed out/i.test(message)) {
                await new Promise((resolveDelay) => setTimeout(resolveDelay, 1_500));
                continue;
            }
            throw error;
        }
    }
    throw lastError;
}

async function collectLiveState(repo, items) {
    const [pullRequests, issues] = await Promise.all([
        runGhJson([
            "pr", "list",
            "--repo", repo,
            "--state", "open",
            "--limit", "1000",
            "--json",
            "number,title,url,state,isDraft,mergeable,mergeStateStatus,reviewDecision,headRefOid,updatedAt,labels,statusCheckRollup",
        ]),
        runGhJson([
            "issue", "list",
            "--repo", repo,
            "--state", "open",
            "--limit", "1000",
            "--json",
            "number,title,url,state,stateReason,updatedAt,labels",
        ]),
    ]);
    const pullRequestNumbers = new Set(pullRequests.map((item) => item.number));
    const issueNumbers = new Set(issues.map((item) => item.number));
    const missing = items.filter((item) =>
        item.type === "pr"
            ? !pullRequestNumbers.has(item.number)
            : !issueNumbers.has(item.number));
    const missingResults = await Promise.allSettled(missing.map(async (item) => {
        const fields = item.type === "pr"
            ? "number,title,url,state,isDraft,mergeable,mergeStateStatus,reviewDecision,headRefOid,updatedAt,labels,statusCheckRollup"
            : "number,title,url,state,stateReason,updatedAt,labels";
        const value = await runGhJson([
            item.type, "view",
            String(item.number),
            "--repo", repo,
            "--json", fields,
        ]);
        return { type: item.type, value };
    }));
    for (const result of missingResults) {
        if (result.status === "fulfilled") {
            (result.value.type === "pr" ? pullRequests : issues).push(result.value.value);
        }
    }
    const failedLookups = missingResults
        .map((result, index) => result.status === "rejected" ? missing[index] : null)
        .filter(Boolean)
        .map((item) => `${item.type.toUpperCase()} #${item.number}`);
    return {
        pullRequests,
        issues,
        refreshWarning: failedLookups.length > 0
            ? `Live lookup failed for ${failedLookups.join(", ")}. Other items are current.`
            : "",
    };
}

function safeErrorMessage(error) {
    const message = error instanceof Error ? error.message : String(error);
    const usefulLine = message
        .split(/\r?\n/)
        .map((line) => line.trim())
        .find((line) => /^HTTP \d{3}:|timed out|ETIMEDOUT/i.test(line));
    return (usefulLine ?? "GitHub refresh failed. Use Refresh now to retry.")
        .replaceAll(repositoryRoot, "<repo>")
        .slice(0, 500);
}

function clientErrorMessage(error) {
    return (error instanceof Error ? error.message : String(error))
        .replaceAll(repositoryRoot, "<repo>")
        .slice(0, 500);
}

function sendState(entry) {
    const payload = `event: state\ndata: ${JSON.stringify(entry.state)}\n\n`;
    for (const client of entry.eventClients) {
        try {
            client.write(payload);
        } catch {
            entry.eventClients.delete(client);
        }
    }
}

async function refreshEntry(entry, force = false) {
    if (entry.refreshPromise) {
        if (!force) {
            return entry.refreshPromise;
        }
        await entry.refreshPromise;
        if (entry.refreshPromise) {
            return refreshEntry(entry, true);
        }
    }
    const refreshPromise = (async () => {
        try {
            const live = await collectLiveState(entry.triage.repo, entry.triage.items);
            entry.pullRequests = live.pullRequests;
            entry.issues = live.issues;
            entry.state = {
                ...mergeLiveState(entry.triage, entry.pullRequests, entry.issues),
                refreshWarning: live.refreshWarning,
            };
        } catch (error) {
            entry.state = {
                ...mergeLiveState(
                    entry.triage,
                    entry.pullRequests,
                    entry.issues,
                    safeErrorMessage(error),
                ),
                refreshWarning: "",
            };
        }
        sendState(entry);
        return entry.state;
    })();
    entry.refreshPromise = refreshPromise;
    try {
        return await refreshPromise;
    } finally {
        if (entry.refreshPromise === refreshPromise) {
            entry.refreshPromise = null;
        }
    }
}

async function readJsonBody(request) {
    let body = "";
    for await (const chunk of request) {
        body += chunk;
        if (body.length > 8_192) {
            throw new Error("Request body is too large");
        }
    }
    return body ? JSON.parse(body) : {};
}

function writeJson(response, statusCode, value) {
    response.writeHead(statusCode, {
        "Cache-Control": "no-store",
        "Content-Type": "application/json; charset=utf-8",
    });
    response.end(JSON.stringify(value));
}

async function handleRequest(entry, request, response) {
    const url = new URL(request.url ?? "/", "http://127.0.0.1");
    if (!requestHostMatches(request, entry.host)) {
        writeJson(response, 403, { error: "Request host rejected" });
        return;
    }
    if (request.method === "GET" && url.pathname === "/") {
        response.writeHead(200, {
            "Cache-Control": "no-store",
            "Content-Security-Policy":
                "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
                "connect-src 'self'; base-uri 'none'; form-action 'none'",
            "Content-Type": "text/html; charset=utf-8",
            "X-Content-Type-Options": "nosniff",
        });
        response.end(renderDashboardHtml());
        return;
    }

    if (request.method === "GET" && url.pathname === "/state") {
        if (!requestTokenMatches(request, url, entry.actionToken)) {
            writeJson(response, 403, { error: "Action token rejected" });
            return;
        }
        writeJson(response, 200, entry.state);
        return;
    }

    if (request.method === "GET" && url.pathname === "/events") {
        if (!requestTokenMatches(request, url, entry.actionToken)) {
            writeJson(response, 403, { error: "Action token rejected" });
            return;
        }
        response.writeHead(200, {
            "Cache-Control": "no-cache",
            "Connection": "keep-alive",
            "Content-Type": "text/event-stream",
            "X-Accel-Buffering": "no",
        });
        response.write(`event: state\ndata: ${JSON.stringify(entry.state)}\n\n`);
        entry.eventClients.add(response);
        request.on("close", () => entry.eventClients.delete(response));
        return;
    }

    if (request.method === "POST" && !requestTokenMatches(request, url, entry.actionToken)) {
        writeJson(response, 403, { error: "Action token rejected" });
        return;
    }

    try {
        if (request.method === "POST" && url.pathname === "/refresh") {
            await readJsonBody(request);
            writeJson(response, 200, { state: await refreshEntry(entry) });
            return;
        }
        if (request.method === "POST" && url.pathname === "/action") {
            const input = await readJsonBody(request);
            const result = await requestItemAction(entry, input.action, input, {
                createError: (code, message) => new CanvasError(code, message),
                refresh: () => refreshEntry(entry, true),
                send: (message) => copilotSession.send(message),
            });
            writeJson(response, 202, result);
            return;
        }
    } catch (error) {
        const statusCode = error instanceof CanvasError ? 409 : 400;
        writeJson(response, statusCode, { error: clientErrorMessage(error) });
        return;
    }

    writeJson(response, 404, { error: "Not found" });
}

async function startInstance(instanceId, triage) {
    const { randomBytes } = await import("node:crypto");
    const entry = {
        actionToken: randomBytes(24).toString("hex"),
        eventClients: new Set(),
        host: "",
        issues: [],
        pullRequests: [],
        refreshPromise: null,
        server: null,
        state: mergeLiveState(triage, [], []),
        timer: null,
        triage,
        url: "",
    };
    const server = createServer((request, response) => {
        handleRequest(entry, request, response).catch((error) => {
            if (!response.headersSent) {
                writeJson(response, 500, { error: safeErrorMessage(error) });
            } else {
                response.destroy();
            }
        });
    });
    await new Promise((resolveListen, rejectListen) => {
        server.once("error", rejectListen);
        server.listen(0, "127.0.0.1", resolveListen);
    });
    const address = server.address();
    const port = typeof address === "object" && address ? address.port : 0;
    entry.server = server;
    entry.host = `127.0.0.1:${port}`;
    entry.url = `http://${entry.host}/#token=${entry.actionToken}`;
    instances.set(instanceId, entry);
    return entry;
}

async function getOrStartInstance(instanceId, triage) {
    const existing = instances.get(instanceId);
    if (existing) {
        return existing;
    }
    let pending = instanceStarts.get(instanceId);
    if (!pending) {
        pending = startInstance(instanceId, triage);
        instanceStarts.set(instanceId, pending);
    }
    try {
        return await pending;
    } finally {
        if (instanceStarts.get(instanceId) === pending) {
            instanceStarts.delete(instanceId);
        }
    }
}

function reconfigureInstance(entry, triage) {
    entry.triage = triage;
    entry.state = mergeLiveState(triage, entry.pullRequests, entry.issues);
    clearInterval(entry.timer);
    entry.timer = setInterval(() => {
        refreshEntry(entry).catch(() => {});
    }, triage.refreshSeconds * 1_000);
    entry.timer.unref();
    refreshEntry(entry, true).catch(() => {});
}

async function closeInstance(instanceId) {
    const entry = instances.get(instanceId);
    if (!entry) {
        return;
    }
    instances.delete(instanceId);
    clearInterval(entry.timer);
    for (const client of entry.eventClients) {
        client.end();
    }
    await new Promise((resolveClose) => entry.server.close(resolveClose));
}

const inputSchema = {
    type: "object",
    required: ["schemaVersion", "repo", "title", "scope", "generatedAt", "items"],
    properties: {
        schemaVersion: { const: 1 },
        repo: { type: "string" },
        title: { type: "string" },
        scope: { type: "string" },
        generatedAt: { type: "string" },
        refreshSeconds: { type: "integer", minimum: 30, maximum: 300 },
        items: { type: "array", minItems: 1, maxItems: 1000 },
        plan: { type: "array", maxItems: 1000 },
        report: {
            type: "object",
            properties: {
                changes: { type: "array", maxItems: 100 },
                executiveQueue: { type: "array", maxItems: 100 },
                ownership: { type: "array", maxItems: 100 },
                reviews: { type: "array", maxItems: 100 },
                dayPlan: { type: "array", maxItems: 100 },
                automation: { type: "array", maxItems: 100 },
            },
            additionalProperties: false,
        },
    },
    additionalProperties: false,
};

const itemActionSchema = {
    type: "object",
    required: ["number"],
    properties: {
        number: { type: "integer", minimum: 1 },
    },
    additionalProperties: false,
};

const dashboard = createCanvas({
    id: "openclaw-triage-dashboard",
    displayName: "OpenClaw triage",
    description: "Shows live OpenClaw triage checks, execution-plan progress, proof gates, and guarded actions.",
    inputSchema,
    actions: [
        {
            name: "refresh",
            description: "Refresh GitHub checks and item state now.",
            handler: async (context) => {
                const entry = instances.get(context.instanceId);
                if (!entry) throw new CanvasError("instance_not_found", "Triage canvas is not open");
                return refreshEntry(entry);
            },
        },
        {
            name: "request_next_action",
            description: "Route a guarded next-step request to the item's dedicated child session.",
            inputSchema: itemActionSchema,
            handler: async (context) => {
                const entry = instances.get(context.instanceId);
                if (!entry) throw new CanvasError("instance_not_found", "Triage canvas is not open");
                return requestItemAction(entry, "request_next_action", context.input, {
                    createError: (code, message) => new CanvasError(code, message),
                    refresh: () => refreshEntry(entry, true),
                    send: (message) => copilotSession.send(message),
                });
            },
        },
        {
            name: "request_merge",
            description: "Route fresh merge verification to the item's dedicated child session.",
            inputSchema: {
                type: "object",
                required: ["number", "headSha"],
                properties: {
                    number: { type: "integer", minimum: 1 },
                    headSha: { type: "string", pattern: "^[0-9a-fA-F]{7,64}$" },
                },
                additionalProperties: false,
            },
            handler: async (context) => {
                const entry = instances.get(context.instanceId);
                if (!entry) throw new CanvasError("instance_not_found", "Triage canvas is not open");
                return requestItemAction(entry, "request_merge", context.input, {
                    createError: (code, message) => new CanvasError(code, message),
                    refresh: () => refreshEntry(entry, true),
                    send: (message) => copilotSession.send(message),
                });
            },
        },
    ],
    open: async (context) => {
        const triage = normalizeTriageInput(context.input);
        const entry = await getOrStartInstance(context.instanceId, triage);
        reconfigureInstance(entry, triage);
        return {
            title: triage.title,
            status: `Live checks every ${triage.refreshSeconds}s`,
            url: entry.url,
        };
    },
    onClose: async (context) => {
        await closeInstance(context.instanceId);
    },
});

copilotSession = await joinSession({ canvases: [dashboard] });
