export function buildPlanLanes(plan, legacyDayPlan = [], legacyQueue = []) {
    if (!plan.length) {
        const legacySteps = [...new Set([...legacyQueue, ...legacyDayPlan])];
        return legacySteps.map((title, index) => ({
            id: `legacy-${index}`,
            kind: "independent",
            levels: [[{
                dependsOn: [],
                id: `legacy-${index}`,
                itemNumbers: [],
                legacy: true,
                liveStatus: "pending",
                title,
            }]],
            title: "Independent task",
        }));
    }

    const byId = new Map(plan.map((step) => [step.id, step]));
    const neighbors = new Map(plan.map((step) => [step.id, new Set()]));
    for (const step of plan) {
        for (const dependency of step.dependsOn) {
            neighbors.get(step.id).add(dependency);
            neighbors.get(dependency).add(step.id);
        }
    }

    const visited = new Set();
    const components = [];
    for (const step of plan) {
        if (visited.has(step.id)) continue;
        const ids = [];
        const pending = [step.id];
        visited.add(step.id);
        while (pending.length) {
            const id = pending.shift();
            ids.push(id);
            for (const neighbor of neighbors.get(id)) {
                if (visited.has(neighbor)) continue;
                visited.add(neighbor);
                pending.push(neighbor);
            }
        }
        components.push(ids);
    }

    return components.map((ids) => {
        const componentIds = new Set(ids);
        const remaining = new Set(ids);
        const ordered = [];
        while (remaining.size) {
            const ready = plan.filter((step) =>
                remaining.has(step.id) &&
                step.dependsOn.every((dependency) =>
                    !componentIds.has(dependency) || !remaining.has(dependency)));
            for (const step of ready) {
                ordered.push(step);
                remaining.delete(step.id);
            }
        }

        const depthById = new Map();
        for (const step of ordered) {
            const dependencyDepths = step.dependsOn
                .filter((dependency) => componentIds.has(dependency))
                .map((dependency) => depthById.get(dependency));
            depthById.set(step.id, dependencyDepths.length
                ? Math.max(...dependencyDepths) + 1
                : 0);
        }
        const levels = [];
        for (const step of ordered) {
            const depth = depthById.get(step.id);
            levels[depth] ??= [];
            levels[depth].push(step);
        }
        const kind = ordered.length === 1
            ? "independent"
            : levels.every((level) => level.length === 1)
                ? "sequential"
                : "parallel";
        return {
            id: ordered.map((step) => step.id).join("--"),
            kind,
            levels,
            title: kind === "sequential"
                ? "Sequential workstream"
                : kind === "parallel"
                    ? "Parallel workstream"
                    : "Independent task",
        };
    });
}

export function limitPlanLanes(lanes, visibleCount) {
    const count = Math.max(1, visibleCount);
    return {
        hiddenCount: Math.max(0, lanes.length - count),
        lanes: lanes.slice(0, count),
    };
}

export function limitLaneLevels(levels, visibleCount) {
    let remaining = Math.max(1, visibleCount);
    const visibleLevels = [];
    let total = 0;
    for (const level of levels) {
        total += level.length;
        if (remaining <= 0) continue;
        const visible = level.slice(0, remaining);
        if (visible.length) visibleLevels.push(visible);
        remaining -= visible.length;
    }
    const visibleTotal = visibleLevels.reduce((sum, level) => sum + level.length, 0);
    return {
        hiddenCount: total - visibleTotal,
        levels: visibleLevels,
    };
}

export function limitPlanRows(lanes, visibleCount) {
    let remaining = Math.max(1, visibleCount);
    const visibleLanes = [];
    const total = lanes.reduce((sum, lane) =>
        sum + lane.levels.reduce((laneSum, level) => laneSum + level.length, 0), 0);
    for (const lane of lanes) {
        if (remaining <= 0) break;
        const window = limitLaneLevels(lane.levels, remaining);
        const visible = window.levels.reduce((sum, level) => sum + level.length, 0);
        if (!visible) continue;
        visibleLanes.push({
            ...lane,
            levels: window.levels,
            totalSteps: lane.levels.reduce((sum, level) => sum + level.length, 0),
        });
        remaining -= visible;
    }
    const visible = visibleLanes.reduce((sum, lane) =>
        sum + lane.levels.reduce((laneSum, level) => laneSum + level.length, 0), 0);
    return {
        hiddenCount: total - visible,
        lanes: visibleLanes,
    };
}
