export const DECISIONS = new Set([
    "TAKE",
    "TAKE_AFTER_CHECKS",
    "NEEDS_HUMAN_TEST",
    "NEEDS_INFO",
    "HOLD_FOR_AUTHOR",
    "DECLINE",
]);

export const KNOWN_PROOF_POOLS = new Set([
    "windows-11-sac-on",
    "windows-wsl-mxc",
    "windows-11-arm64",
    "windows-wsl-dgx-blackwell",
    "windows-clean-installer-upgrade",
    "windows-wsl-gateway-e2e",
    "windows-winui-interactive",
]);

export const SUPPORTED_REPOSITORY = "openclaw/openclaw-windows-node";

const FAILED_CONCLUSIONS = new Set([
    "ACTION_REQUIRED",
    "CANCELLED",
    "ERROR",
    "FAILURE",
    "STARTUP_FAILURE",
    "STALE",
    "TIMED_OUT",
]);

const COMPLETED_CONCLUSIONS = new Set(["NEUTRAL", "SKIPPED", "SUCCESS"]);
const PROOF_COMPLETE = new Set(["passed", "not-applicable"]);

function assert(condition, message) {
    if (!condition) {
        throw new Error(message);
    }
}

function normalizeString(value, field, maximum = 2_000) {
    assert(typeof value === "string" && value.trim(), `${field} must be a non-empty string`);
    const normalized = value.trim();
    assert(normalized.length <= maximum, `${field} is too long`);
    return normalized;
}

function normalizeOptionalString(value, field, maximum = 2_000) {
    if (value == null || value === "") {
        return "";
    }
    return normalizeString(value, field, maximum);
}

function normalizeConfidence(value, field) {
    assert(Number.isInteger(value) && value >= 0 && value <= 100, `${field} must be an integer from 0 to 100`);
    return value;
}

function normalizeNumber(value, field) {
    assert(Number.isInteger(value) && value > 0, `${field} must be a positive integer`);
    return value;
}

function normalizeUrl(value, field) {
    const normalized = normalizeString(value, field, 1_000);
    let parsed;
    try {
        parsed = new URL(normalized);
    } catch {
        throw new Error(`${field} must be a valid HTTP or HTTPS URL`);
    }
    assert(
        parsed.protocol === "http:" || parsed.protocol === "https:",
        `${field} must be a valid HTTP or HTTPS URL`,
    );
    return normalized;
}

function normalizeStatus(value, field, allowed) {
    assert(allowed.has(value), `${field} has an unsupported value`);
    return value;
}

function normalizeStringArray(value, field, maximum = 100) {
    assert(Array.isArray(value) && value.length <= maximum, `${field} must be an array with at most ${maximum} entries`);
    return value.map((entry, index) => normalizeString(entry, `${field}[${index}]`, 200));
}

function normalizeItem(item, index) {
    assert(item && typeof item === "object" && !Array.isArray(item), `items[${index}] must be an object`);
    const type = normalizeStatus(item.type, `items[${index}].type`, new Set(["pr", "issue"]));
    const number = normalizeNumber(item.number, `items[${index}].number`);
    const proofPools = normalizeStringArray(item.proofPools ?? [], `items[${index}].proofPools`, 10);
    for (const pool of proofPools) {
        assert(KNOWN_PROOF_POOLS.has(pool), `items[${index}].proofPools contains unknown pool ${pool}`);
    }

    const expectedChecks = normalizeStringArray(
        item.expectedChecks ?? [],
        `items[${index}].expectedChecks`,
        30,
    );
    assert(type !== "pr" || expectedChecks.length > 0,
        `items[${index}].expectedChecks must name at least one required check for pull requests`);
    assert(type !== "issue" || expectedChecks.length === 0,
        `items[${index}].expectedChecks must be empty for issues`);

    return {
        id: `${type}-${number}`,
        type,
        number,
        title: normalizeString(item.title, `items[${index}].title`, 500),
        url: normalizeUrl(item.url, `items[${index}].url`),
        decision: normalizeStatus(item.decision, `items[${index}].decision`, DECISIONS),
        takeConfidence: normalizeConfidence(item.takeConfidence, `items[${index}].takeConfidence`),
        recommendationConfidence: normalizeConfidence(
            item.recommendationConfidence,
            `items[${index}].recommendationConfidence`,
        ),
        effort: normalizeOptionalString(item.effort, `items[${index}].effort`, 100),
        risk: normalizeOptionalString(item.risk, `items[${index}].risk`, 100),
        owner: normalizeString(item.owner, `items[${index}].owner`, 300),
        nextAction: normalizeString(item.nextAction, `items[${index}].nextAction`),
        proofPools,
        proofStatus: normalizeStatus(
            item.proofStatus,
            `items[${index}].proofStatus`,
            new Set(["blocked", "not-applicable", "passed", "required"]),
        ),
        reviewStatus: normalizeStatus(
            item.reviewStatus,
            `items[${index}].reviewStatus`,
            new Set(["blocked", "complete", "required"]),
        ),
        reviewedHeadSha: normalizeOptionalString(item.reviewedHeadSha, `items[${index}].reviewedHeadSha`, 64),
        expectedChecks,
        dependencies: (item.dependencies ?? []).map((entry, dependencyIndex) =>
            normalizeNumber(entry, `items[${index}].dependencies[${dependencyIndex}]`)),
    };
}

function normalizePlanStep(step, index, itemTypes) {
    assert(step && typeof step === "object" && !Array.isArray(step), `plan[${index}] must be an object`);
    const numbers = (step.itemNumbers ?? []).map((entry, itemIndex) =>
        normalizeNumber(entry, `plan[${index}].itemNumbers[${itemIndex}]`));
    for (const number of numbers) {
        assert(itemTypes.has(number), `plan[${index}] references item #${number} outside this triage`);
    }
    const gates = (step.gates ?? []).map((gate, gateIndex) => {
        assert(gate && typeof gate === "object" && !Array.isArray(gate),
            `plan[${index}].gates[${gateIndex}] must be an object`);
        const itemNumber = normalizeNumber(
            gate.itemNumber,
            `plan[${index}].gates[${gateIndex}].itemNumber`,
        );
        assert(itemTypes.has(itemNumber),
            `plan[${index}].gates[${gateIndex}] references item #${itemNumber} outside this triage`);
        const stage = normalizeStatus(
            gate.stage,
            `plan[${index}].gates[${gateIndex}].stage`,
            new Set(["checks", "inventory", "landing", "proof", "review"]),
        );
        assert(stage !== "landing" || itemTypes.get(itemNumber) === "pr",
            `plan[${index}].gates[${gateIndex}] cannot use landing for an issue`);
        return {
            itemNumber,
            stage,
        };
    });
    return {
        id: normalizeString(step.id, `plan[${index}].id`, 100),
        title: normalizeString(step.title, `plan[${index}].title`, 500),
        detail: normalizeOptionalString(step.detail, `plan[${index}].detail`),
        dependsOn: normalizeStringArray(
            step.dependsOn ?? [],
            `plan[${index}].dependsOn`,
            100,
        ),
        horizon: normalizeStatus(
            step.horizon ?? "today",
            `plan[${index}].horizon`,
            new Set(["later", "today"]),
        ),
        itemNumbers: numbers,
        gates,
        status: normalizeStatus(
            step.status,
            `plan[${index}].status`,
            new Set(["blocked", "done", "in_progress", "pending"]),
        ),
    };
}

function normalizePlan(steps, itemTypes) {
    const plan = steps.map((step, index) =>
        normalizePlanStep(step, index, itemTypes));
    const planById = new Map(plan.map((step) => [step.id, step]));
    assert(planById.size === plan.length, "plan must not contain duplicate IDs");
    for (const step of plan) {
        for (const dependency of step.dependsOn) {
            assert(dependency !== step.id, `plan step ${step.id} must not depend on itself`);
            assert(planById.has(dependency),
                `plan step ${step.id} depends on unknown step ${dependency}`);
        }
    }
    const visiting = new Set();
    const visited = new Set();
    const visit = (step) => {
        if (visited.has(step.id)) return;
        assert(!visiting.has(step.id), `plan contains a dependency cycle at ${step.id}`);
        visiting.add(step.id);
        for (const dependency of step.dependsOn) {
            visit(planById.get(dependency));
        }
        visiting.delete(step.id);
        visited.add(step.id);
    };
    for (const step of plan) visit(step);
    return plan;
}

function normalizeReportEntry(entry, field, index, keys) {
    assert(entry && typeof entry === "object" && !Array.isArray(entry), `${field}[${index}] must be an object`);
    return Object.fromEntries(keys.map((key) => [
        key,
        normalizeOptionalString(entry[key], `${field}[${index}].${key}`, 2_000),
    ]));
}

function normalizeReport(report = {}) {
    assert(report && typeof report === "object" && !Array.isArray(report), "report must be an object");
    const normalizeEntries = (field, keys, maximum = 100) => {
        const entries = report[field] ?? [];
        assert(Array.isArray(entries) && entries.length <= maximum,
            `report.${field} must be an array with at most ${maximum} entries`);
        return entries.map((entry, index) =>
            normalizeReportEntry(entry, `report.${field}`, index, keys));
    };
    return {
        changes: normalizeEntries("changes", ["change", "items"]),
        executiveQueue: normalizeStringArray(report.executiveQueue ?? [], "report.executiveQueue", 100),
        ownership: normalizeEntries("ownership", ["item", "assessment"]),
        reviews: normalizeEntries("reviews", ["item", "title", "decision", "summary"]),
        dayPlan: normalizeStringArray(report.dayPlan ?? [], "report.dayPlan", 100),
        automation: normalizeEntries("automation", ["opportunity", "why", "owner", "effort", "notes"]),
    };
}

export function normalizeTriageInput(input) {
    assert(input && typeof input === "object" && !Array.isArray(input), "canvas input must be an object");
    assert(input.schemaVersion === 1, "schemaVersion must be 1");
    const repo = normalizeString(input.repo, "repo", 200);
    assert(/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repo), "repo must use owner/name format");
    assert(repo.toLowerCase() === SUPPORTED_REPOSITORY,
        `repo must be ${SUPPORTED_REPOSITORY}`);
    assert(Array.isArray(input.items) && input.items.length > 0 && input.items.length <= 1_000,
        "items must contain between 1 and 1000 entries");
    const items = input.items.map(normalizeItem);
    const itemIds = new Set(items.map((item) => item.id));
    assert(itemIds.size === items.length, "items must not contain duplicate type/number pairs");
    const itemNumbers = new Set(items.map((item) => item.number));
    assert(itemNumbers.size === items.length, "items must not contain duplicate numbers");
    for (const item of items) {
        const parsedUrl = new URL(item.url);
        const expectedPath = `/${repo}/${item.type === "pr" ? "pull" : "issues"}/${item.number}`;
        assert(
            parsedUrl.protocol === "https:" &&
                parsedUrl.hostname.toLowerCase() === "github.com" &&
                parsedUrl.pathname.toLowerCase() === expectedPath.toLowerCase(),
            `items must link to their canonical ${repo} GitHub URL`,
        );
    }
    const refreshSeconds = input.refreshSeconds ?? 60;
    assert(Number.isInteger(refreshSeconds) && refreshSeconds >= 30 && refreshSeconds <= 300,
        "refreshSeconds must be an integer from 30 to 300");
    const itemTypes = new Map(items.map((item) => [item.number, item.type]));
    const plan = normalizePlan(input.plan ?? [], itemTypes);

    return {
        schemaVersion: 1,
        repo,
        title: normalizeString(input.title, "title", 500),
        scope: normalizeString(input.scope, "scope", 1_000),
        generatedAt: normalizeString(input.generatedAt, "generatedAt", 100),
        refreshSeconds,
        items,
        plan,
        report: normalizeReport(input.report),
    };
}

function checkName(check) {
    return String(check?.name ?? check?.context ?? "").trim();
}

function checkConclusion(check) {
    return String(check?.conclusion ?? check?.state ?? "").toUpperCase();
}

export function summarizeChecks(checks, expectedChecks = []) {
    const observed = Array.isArray(checks) ? checks : [];
    const relevant = observed.filter((check) => checkConclusion(check) !== "SKIPPED");
    const failed = relevant.filter((check) => FAILED_CONCLUSIONS.has(checkConclusion(check)));
    const pending = relevant.filter((check) => {
        const conclusion = checkConclusion(check);
        const status = String(check?.status ?? "").toUpperCase();
        return !FAILED_CONCLUSIONS.has(conclusion) &&
            !COMPLETED_CONCLUSIONS.has(conclusion) &&
            (status !== "COMPLETED" || !conclusion);
    });
    const names = observed.map(checkName).filter(Boolean);
    const missing = expectedChecks.filter((expected) =>
        !names.some((name) => name.toLowerCase() === expected.toLowerCase()));

    return {
        total: relevant.length,
        passed: relevant.length - failed.length - pending.length,
        failed: failed.length,
        pending: pending.length,
        missing,
        failedNames: failed.map(checkName).filter(Boolean),
        pendingNames: pending.map(checkName).filter(Boolean),
    };
}

export function deriveItemStages(item, live) {
    const checks = summarizeChecks(live?.statusCheckRollup, item.expectedChecks);
    const checksStatus = live == null || checks.missing.length > 0
        ? "blocked"
        : checks.failed > 0
            ? "blocked"
            : checks.pending > 0
                ? "in_progress"
                : "done";
    const reviewStatus = item.reviewStatus === "complete"
        ? "done"
        : item.reviewStatus === "required"
            ? "pending"
            : "blocked";
    const proofStatus = PROOF_COMPLETE.has(item.proofStatus)
        ? "done"
        : item.proofStatus === "required"
            ? "pending"
            : "blocked";

    return {
        inventory: live == null ? "blocked" : "done",
        review: reviewStatus,
        checks: checksStatus,
        proof: proofStatus,
        ...(item.type === "pr"
            ? { landing: canRequestMerge(item, live).eligible ? "done" : "blocked" }
            : {}),
    };
}

export function canRequestMerge(item, live) {
    const reasons = [];
    if (item.type !== "pr") reasons.push("Only pull requests can merge");
    if (item.decision !== "TAKE") reasons.push("Decision must be TAKE");
    if (item.takeConfidence < 90) reasons.push("Take confidence must be at least 90%");
    if (item.reviewStatus !== "complete") reasons.push("Review is incomplete");
    if (!PROOF_COMPLETE.has(item.proofStatus)) reasons.push("Required proof is incomplete");
    if (!live) {
        reasons.push("Live GitHub status is unavailable");
        return { eligible: false, reasons };
    }

    if (String(live.state).toUpperCase() !== "OPEN") reasons.push("Pull request is not open");
    if (live.isDraft) reasons.push("Pull request is still a draft");
    if (String(live.mergeStateStatus).toUpperCase() !== "CLEAN") reasons.push("Merge state is not clean");
    const checks = summarizeChecks(live.statusCheckRollup, item.expectedChecks);
    if (checks.failed > 0) reasons.push(`${checks.failed} check(s) failed`);
    if (checks.pending > 0) reasons.push(`${checks.pending} check(s) pending`);
    if (checks.missing.length > 0) reasons.push(`Missing expected checks: ${checks.missing.join(", ")}`);
    if (!item.reviewedHeadSha) {
        reasons.push("Reviewed head SHA is missing");
    } else if (String(live.headRefOid).toLowerCase() !== item.reviewedHeadSha.toLowerCase()) {
        reasons.push("Live head differs from the reviewed head");
    }
    return { eligible: reasons.length === 0, reasons };
}

export function mergeLiveState(triage, pullRequests, issues, error = "") {
    const pullRequestMap = new Map((pullRequests ?? []).map((item) => [item.number, item]));
    const issueMap = new Map((issues ?? []).map((item) => [item.number, item]));
    const items = triage.items.map((item) => {
        const live = item.type === "pr" ? pullRequestMap.get(item.number) : issueMap.get(item.number);
        const checks = item.type === "pr"
            ? summarizeChecks(live?.statusCheckRollup, item.expectedChecks)
            : null;
        const mergeRequest = canRequestMerge(item, live);
        return {
            ...item,
            live: live ?? null,
            checks,
            stages: deriveItemStages(item, live),
            mergeRequest,
        };
    });

    const itemMap = new Map(items.map((item) => [item.number, item]));
    const planById = new Map(triage.plan.map((step) => [step.id, step]));
    const liveStatusById = new Map();
    const resolvePlanStatus = (step) => {
        if (liveStatusById.has(step.id)) return liveStatusById.get(step.id);
        const gateStatuses = step.gates.map((gate) => itemMap.get(gate.itemNumber)?.stages[gate.stage] ?? "blocked");
        let liveStatus = step.status;
        if (gateStatuses.length > 0) {
            liveStatus = gateStatuses.every((status) => status === "done")
                ? "done"
                : gateStatuses.some((status) => status === "blocked")
                    ? "blocked"
                    : gateStatuses.some((status) => status === "in_progress")
                        ? "in_progress"
                        : "pending";
        }
        const dependenciesComplete = step.dependsOn.every((dependencyId) =>
            resolvePlanStatus(planById.get(dependencyId)) === "done");
        if (!dependenciesComplete) liveStatus = "blocked";
        liveStatusById.set(step.id, liveStatus);
        return liveStatus;
    };
    const plan = triage.plan.map((step) => ({
        ...step,
        liveStatus: resolvePlanStatus(step),
    }));

    return {
        ...triage,
        items,
        plan,
        liveUpdatedAt: new Date().toISOString(),
        refreshError: error,
        summary: {
            total: items.length,
            ready: items.filter((item) => item.mergeRequest.eligible).length,
            blocked: items.filter((item) => Object.values(item.stages).includes("blocked")).length,
            checksRunning: items.filter((item) => item.checks?.pending > 0).length,
            needsProof: items.filter((item) => !PROOF_COMPLETE.has(item.proofStatus)).length,
        },
    };
}
