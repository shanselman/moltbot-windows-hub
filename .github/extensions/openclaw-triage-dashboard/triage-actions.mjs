import { canRequestMerge } from "./triage-state.mjs";

function itemKind(type) {
    return type === "pr" ? "PR" : "Issue";
}

export function buildSubsessionRoutingPrompt(repo, item, actionPrompt) {
    const kind = itemKind(item.type);
    const sessionName = `Triage ${kind} #${item.number}`;
    const identity = `${repo} ${kind} #${item.number}`;

    return {
        sessionName,
        prompt:
            `Route this dashboard action to the dedicated child project session for ${identity}. ` +
            "Do not execute the item work in this parent session.\n\n" +
            "Use the app session tools as follows:\n" +
            `1. Call list_projects and resolve the project whose GitHub repository is exactly "${repo}". ` +
            "Use that project's ID for any create_session call.\n" +
            `2. Call list_sessions_and_chats and look for an existing child project session named ` +
            `"${sessionName}" in the current project and repository.\n` +
            "3. If exactly one matching session exists, verify its project repository is the exact repository above, " +
            "then call send_session_message with the action below so the work is appended to that session.\n" +
            "4. If more than one matching session exists, stop and report the ambiguity. Do not pick one or create another.\n" +
            `5. If no matching session exists, call create_session with the resolved project_id, name "${sessionName}", ` +
            "coordinate_with_creator enabled, base_branch unset, and a kickoff using the action below in interactive mode.\n" +
            "6. Do not create a duplicate session. Briefly report whether the child session was created or reused.\n\n" +
            `Action for the child session:\n${actionPrompt}`,
    };
}

export async function requireFreshGitHubEvidence(refresh, createError = (_, message) => new Error(message)) {
    const state = await refresh();
    if (state.refreshError) {
        throw createError(
            "refresh_failed",
            `Fresh GitHub evidence is required before preparing a merge: ${state.refreshError}`,
        );
    }
    return state;
}

function fail(createError, code, message) {
    throw createError(code, message);
}

export function itemDependencyBlocker(state, number) {
    const plan = Array.isArray(state?.plan) ? state.plan : [];
    const linkedSteps = plan.filter((step) => step.itemNumbers.includes(number));
    if (linkedSteps.length === 0) {
        return "";
    }
    const planById = new Map(plan.map((step) => [step.id, step]));
    const runnableStep = linkedSteps.some((step) =>
        step.dependsOn.every((dependencyId) =>
            planById.get(dependencyId)?.liveStatus === "done"));
    if (runnableStep) {
        return "";
    }
    const unresolved = [...new Set(linkedSteps.flatMap((step) =>
        step.dependsOn
            .map((dependencyId) => planById.get(dependencyId))
            .filter((dependency) => dependency?.liveStatus !== "done")
            .map((dependency) => dependency.title)))];
    return unresolved.length > 0
        ? `Complete dependencies first: ${unresolved.join(", ")}`
        : "Complete plan dependencies first";
}

export async function requestItemAction(entry, action, input, {
    createError = (code, message) => Object.assign(new Error(message), { code }),
    refresh,
    send,
} = {}) {
    const number = Number(input?.number);
    if (!Number.isInteger(number) || number <= 0) {
        fail(createError, "invalid_item", "A positive PR or issue number is required");
    }
    let item = entry.state.items.find((candidate) => candidate.number === number);
    if (!item) {
        fail(createError, "item_not_found", `#${number} is not in this triage`);
    }

    let actionPrompt;
    let result;
    if (action === "request_merge") {
        await requireFreshGitHubEvidence(
            refresh,
            createError,
        );
        item = entry.state.items.find((candidate) => candidate.number === number);
        if (!item) {
            fail(createError, "item_not_found", `#${number} is no longer in this triage`);
        }
        const dependencyBlocker = itemDependencyBlocker(entry.state, number);
        if (dependencyBlocker) {
            fail(createError, "plan_dependencies_incomplete", dependencyBlocker);
        }
        const eligibility = canRequestMerge(item, item.live);
        if (!eligibility.eligible) {
            fail(createError, "merge_not_ready", eligibility.reasons.join("; "));
        }
        const requestedHead = String(input?.headSha ?? "");
        if (!/^[0-9a-f]{7,64}$/i.test(requestedHead) ||
            requestedHead.toLowerCase() !== String(item.live.headRefOid).toLowerCase()) {
            fail(createError, "head_changed", "The requested head no longer matches live GitHub state");
        }
        actionPrompt =
            `The user selected Prepare merge for ${entry.triage.repo} PR #${number} at observed head ` +
            `${requestedHead}. Use the global-repo-triage skill guardrails. Re-fetch the PR, exact head, ` +
            "required checks, reviews, unresolved threads, proof declarations, real behavior evidence, " +
            "branch protection, and mergeability. Do not mutate GitHub yet. Present the fresh evidence and " +
            "ask for explicit confirmation before merging.";
        result = { headSha: requestedHead };
    } else if (action === "request_next_action") {
        const dependencyBlocker = itemDependencyBlocker(entry.state, number);
        if (dependencyBlocker) {
            fail(createError, "plan_dependencies_incomplete", dependencyBlocker);
        }
        actionPrompt =
            `The user selected Request next step for ${entry.triage.repo} ${item.type.toUpperCase()} #${number} ` +
            "from the interactive triage canvas. Invoke the global-repo-triage skill, refresh this item's " +
            "current GitHub evidence, and continue only the smallest safe next action. Preserve the read-only " +
            "default for GitHub mutations and ask for confirmation before any merge, close, label, comment, " +
            "push, rerun, or session deletion.";
        result = {};
    } else {
        fail(createError, "unsupported_action", "Unsupported triage action");
    }

    const routing = buildSubsessionRoutingPrompt(entry.triage.repo, item, actionPrompt);
    await send({ prompt: routing.prompt });
    return {
        queued: true,
        subsessionRoutingQueued: true,
        sessionName: routing.sessionName,
        action,
        number,
        ...result,
    };
}

export function requestHostMatches(request, expectedHost) {
    return request.headers.host === expectedHost;
}

export function requestTokenMatches(request, url, actionToken) {
    return request.headers["x-triage-token"] === actionToken ||
        url.searchParams.get("token") === actionToken;
}
