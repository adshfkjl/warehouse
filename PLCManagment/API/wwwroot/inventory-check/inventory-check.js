const terminalStatuses = new Set(["Completed", "CompletedWithErrors", "Failed", "Canceled"]);

const statusText = {
    Pending: "待执行",
    Running: "执行中",
    Succeeded: "成功",
    Failed: "失败",
    Skipped: "已跳过",
    NotReady: "未就绪",
    Ready: "可上架",
    Scheduled: "已预约",
    Completed: "已完成",
    CompletedWithErrors: "完成但有失败",
    Canceled: "已放弃"
};

const messageTranslations = [
    [/^Inventory check outbound task created\.$/i, "盘点下架任务已创建。"],
    [/^Inventory check outbound task is running\.$/i, "盘点下架任务正在执行。"],
    [/^Inventory check outbound is running\.$/i, "正在执行下架。"],
    [/^Inventory check outbound command failed\.$/i, "下架指令执行失败。"],
    [/^Inventory check outbound completed and tray was synced to loading point\.$/i, "下架完成，货框已同步到装载点。"],
    [/^Inventory check inbound scheduled\.$/i, "已预约上架。"],
    [/^Inventory check inbound scheduling failed\.$/i, "预约上架失败。"],
    [/^Missing outbound loading point; inbound scheduling is not possible\.$/i, "缺少下架装载点，无法预约上架。"],
    [/^Inventory check task was canceled by user\.$/i, "任务已由用户放弃。"],
    [/^Inventory check task was canceled because a new task was created for the same PLC\.$/i, "同一立库已创建新任务，原任务已放弃。"],
    [/^Inventory check item (\d+) is already scheduling inbound\.$/i, "该货框正在预约上架，请勿重复操作。"],
    [/^Inventory check item (\d+) does not exist\.$/i, "任务明细不存在。"],
    [/^Inventory check task (\d+) does not exist\.$/i, "任务不存在。"],
    [/^Inventory check item (\d+) is not ready for inbound scheduling\.$/i, "该货框当前不能预约上架。"],
    [/^Loading points 0 and 1 were not empty; this tray was skipped\.$/i, "装载点 0 和 1 均非空，等待空位后再继续。"],
    [/^PLC ([A-Z]\d+) is offline\. Retry after (\d+) seconds\. Last error: (.*)$/i, "PLC $1 离线，$2 秒后重试。最后错误：$3"],
    [/^Inventory check outbound completed\. Success: (\d+), failed: (\d+)\.$/i, "盘点下架完成。成功：$1，失败：$2。"],
    [/^Inventory check outbound task failed: (.*)$/i, "盘点下架任务失败：$1"],
    [/^Inventory check outbound failed: (.*)$/i, "下架失败：$1"],
    [/^Inventory check inbound scheduling failed: (.*)$/i, "预约上架失败：$1"]
];

const state = {
    currentTaskId: null,
    currentTaskStatus: null,
    pollingHandle: null,
    isBusy: false
};

const elements = {
    outboundForm: document.getElementById("outboundForm"),
    queryForm: document.getElementById("queryForm"),
    plcId: document.getElementById("plcId"),
    trayStart: document.getElementById("trayStart"),
    trayEnd: document.getElementById("trayEnd"),
    taskIdInput: document.getElementById("taskIdInput"),
    startOutboundButton: document.getElementById("startOutboundButton"),
    queryButton: document.getElementById("queryButton"),
    cancelTaskButton: document.getElementById("cancelTaskButton"),
    connectionStatus: document.getElementById("connectionStatus"),
    messageArea: document.getElementById("messageArea"),
    taskNo: document.getElementById("taskNo"),
    taskStatus: document.getElementById("taskStatus"),
    totalCount: document.getElementById("totalCount"),
    successCount: document.getElementById("successCount"),
    failedCount: document.getElementById("failedCount"),
    taskMessage: document.getElementById("taskMessage"),
    taskRange: document.getElementById("taskRange"),
    itemsBody: document.getElementById("itemsBody")
};

elements.outboundForm.addEventListener("submit", async event => {
    event.preventDefault();
    await startOutbound();
});

elements.queryForm.addEventListener("submit", async event => {
    event.preventDefault();
    const taskId = Number(elements.taskIdInput.value);
    if (!taskId) {
        showMessage("请输入有效的任务 ID。", "error");
        return;
    }

    stopPolling();
    await loadTask(taskId, false);
});

elements.cancelTaskButton.addEventListener("click", async () => {
    if (!state.currentTaskId) {
        showMessage("请先开始或查询一个任务。", "error");
        return;
    }

    await cancelTask(state.currentTaskId);
});

elements.itemsBody.addEventListener("click", async event => {
    const button = event.target.closest("[data-schedule-inbound-item]");
    if (!button || button.disabled) {
        return;
    }

    const itemId = Number(button.dataset.scheduleInboundItem);
    if (!itemId) {
        showMessage("无效的任务明细。", "error");
        return;
    }

    await scheduleInboundItem(itemId);
});

if (window.location.protocol === "file:") {
    showMessage("当前页面是从本地文件打开的，浏览器不能这样调用 ASP.NET 接口。请先运行 API，然后用 http://服务器地址:端口/inventory-check/ 打开本页面。", "error");
    setConnectionStatus("打开方式错误", "idle");
}

async function startOutbound() {
    const plcId = elements.plcId.value.trim();
    const trayStart = elements.trayStart.value.trim();
    const trayEnd = elements.trayEnd.value.trim();

    if (!plcId || !trayStart || !trayEnd) {
        showMessage("请填写立库编号、Tray 起始和 Tray 结束。", "error");
        return;
    }

    setBusy(true);
    showMessage("正在创建盘点下架任务...", "info");

    try {
        const task = await requestJson("/api/inventory-check/outbound-range", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ plcId, trayStart, trayEnd })
        });

        state.currentTaskId = task.taskId;
        elements.taskIdInput.value = task.taskId;
        renderTask(task);
        showMessage(`任务 ${task.taskId} 已创建，正在自动刷新状态。`, "success");
        startPolling(task.taskId);
    } catch (error) {
        showMessage(error.message, "error");
    } finally {
        setBusy(false);
    }
}

async function loadTask(taskId, keepPolling) {
    setBusy(true);

    try {
        const task = await requestJson(`/api/inventory-check/tasks/${taskId}`);
        state.currentTaskId = task.taskId;
        elements.taskIdInput.value = task.taskId;
        renderTask(task);

        if (keepPolling && !terminalStatuses.has(task.status)) {
            setConnectionStatus("自动刷新中", "polling");
        } else {
            stopPolling();
            setConnectionStatus(terminalStatuses.has(task.status) ? "任务已结束" : "已查询", "done");
        }
    } catch (error) {
        if (!keepPolling) {
            showMessage(error.message, "error");
        }
        setConnectionStatus("查询失败", "idle");
    } finally {
        setBusy(false);
    }
}

async function scheduleInboundItem(itemId) {
    setBusy(true);
    showMessage("正在预约当前货框上架...", "info");

    try {
        const task = await requestJson(`/api/inventory-check/items/${itemId}/schedule-inbound`, {
            method: "POST"
        });

        renderTask(task);
        showMessage("已发送当前货框预约上架请求。", "success");
    } catch (error) {
        showMessage(error.message, "error");
    } finally {
        setBusy(false);
    }
}

async function cancelTask(taskId) {
    setBusy(true);
    showMessage("正在放弃当前任务...", "info");

    try {
        const task = await requestJson(`/api/inventory-check/tasks/${taskId}/cancel`, {
            method: "POST"
        });

        stopPolling();
        renderTask(task);
        showMessage("当前任务已放弃。", "success");
    } catch (error) {
        showMessage(error.message, "error");
    } finally {
        setBusy(false);
    }
}

async function requestJson(url, options = {}) {
    const requestUrl = createApiUrl(url);
    let response;

    try {
        response = await fetch(requestUrl, options);
    } catch {
        throw new Error(`无法连接接口：${requestUrl}。请确认 API 程序正在运行，并且当前页面是从 API 服务地址打开的。当前页面：${window.location.href}`);
    }

    const contentType = response.headers.get("content-type") || "";
    const payload = contentType.includes("application/json")
        ? await response.json()
        : await response.text();

    if (!response.ok) {
        const isHtmlPayload = typeof payload === "string" && /^\s*<!doctype html|^\s*<html/i.test(payload);
        const message = typeof payload === "object" && payload !== null
            ? translateMessage(payload.message || payload.data || `请求失败：${response.status}`)
            : isHtmlPayload
                ? `接口地址不存在或未进入 API 程序：${requestUrl}。HTTP 状态：${response.status}。请确认 IIS 应用路径和发布文件。`
                : translateMessage(payload || `请求失败：${response.status}`);
        throw new Error(message);
    }

    return payload;
}

function createApiUrl(path) {
    if (window.location.protocol === "file:") {
        throw new Error("当前页面是从本地文件打开的。请运行 ASP.NET API 后，通过 http://服务器地址:端口/inventory-check/ 打开页面。");
    }

    if (/^https?:\/\//i.test(path)) {
        return path;
    }

    const marker = "/inventory-check";
    const normalizedPath = path.replace(/^\/+/, "");
    const pagePath = window.location.pathname;
    const markerIndex = pagePath.toLowerCase().indexOf(marker);
    const appBasePath = markerIndex >= 0 ? pagePath.slice(0, markerIndex) : "";
    const basePath = appBasePath.endsWith("/") ? appBasePath.slice(0, -1) : appBasePath;

    return `${window.location.origin}${basePath}/${normalizedPath}`;
}

function startPolling(taskId) {
    stopPolling();
    setConnectionStatus("自动刷新中", "polling");
    state.pollingHandle = window.setInterval(() => {
        loadTask(taskId, true);
    }, 3000);
}

function stopPolling() {
    if (state.pollingHandle) {
        window.clearInterval(state.pollingHandle);
        state.pollingHandle = null;
    }
}

function renderTask(task) {
    elements.taskNo.textContent = task.taskNo || "--";
    state.currentTaskStatus = task.status || null;
    elements.taskStatus.textContent = translateStatus(task.status);
    elements.taskStatus.className = `status-badge ${statusClass(task.status)}`;
    elements.totalCount.textContent = task.totalCount ?? 0;
    elements.successCount.textContent = task.successCount ?? 0;
    elements.failedCount.textContent = task.failedCount ?? 0;
    elements.taskMessage.textContent = translateMessage(task.message) || "暂无消息";
    elements.taskRange.textContent = `${task.plcid || "--"} / ${task.trayStart || "--"} - ${task.trayEnd || "--"}`;
    elements.cancelTaskButton.disabled = !task.taskId || state.isBusy || terminalStatuses.has(task.status);

    renderItems(task.items || []);

    if (terminalStatuses.has(task.status)) {
        stopPolling();
        setConnectionStatus("任务已结束", "done");
    }
}

function renderItems(items) {
    if (items.length === 0) {
        elements.itemsBody.innerHTML = '<tr><td colspan="9" class="empty-row">暂无数据</td></tr>';
        return;
    }

    elements.itemsBody.innerHTML = items.map(item => `
        <tr>
            <td>${escapeHtml(item.tray)}</td>
            <td>${item.shelf ?? ""}</td>
            <td>${item.position ?? ""}</td>
            <td>${item.outboundLoadingPoint ?? "--"}</td>
            <td>${badge(item.outboundStatus)}</td>
            <td class="message-cell">${escapeHtml(translateMessage(item.outboundMessage))}</td>
            <td>${badge(item.inboundStatus)}</td>
            <td class="message-cell">${escapeHtml(translateMessage(item.inboundMessage))}</td>
            <td>${scheduleInboundButton(item)}</td>
        </tr>
    `).join("");
}

function scheduleInboundButton(item) {
    const canSchedule = item.outboundStatus === "Succeeded" && item.inboundStatus !== "Scheduled";
    const disabled = state.isBusy || !canSchedule ? " disabled" : "";
    const title = canSchedule ? "预约当前货框上架" : "当前货框暂不可预约上架";

    return `<button class="success-button row-action" type="button" data-schedule-inbound-item="${item.id}" data-can-schedule-inbound="${canSchedule}" title="${title}"${disabled}>预约上架</button>`;
}

function badge(status) {
    const value = status || "--";
    return `<span class="status-badge ${statusClass(value)}">${escapeHtml(translateStatus(value))}</span>`;
}

function translateStatus(status) {
    return statusText[status] || status || "--";
}

function translateMessage(message) {
    const value = String(message ?? "").trim();
    if (!value) {
        return "";
    }

    for (const [pattern, replacement] of messageTranslations) {
        if (pattern.test(value)) {
            return value.replace(pattern, replacement);
        }
    }

    return value
        .replace(/Read failed:/gi, "读取失败：")
        .replace(/Connection failed:/gi, "连接失败：")
        .replace(/Modbus probe failed/gi, "Modbus 通讯探测失败")
        .replace(/Unable to read data from the transport connection/gi, "无法从网络连接读取数据")
        .replace(/PLC ([A-Z]\d+) is offline/gi, "PLC $1 离线");
}

function statusClass(status) {
    return String(status || "neutral").toLowerCase().replace(/[^a-z0-9]/g, "") || "neutral";
}

function setBusy(isBusy) {
    state.isBusy = isBusy;
    elements.startOutboundButton.disabled = isBusy;
    elements.queryButton.disabled = isBusy;
    elements.cancelTaskButton.disabled = isBusy || !state.currentTaskId || terminalStatuses.has(state.currentTaskStatus);
    elements.itemsBody.querySelectorAll("[data-schedule-inbound-item]").forEach(button => {
        button.disabled = isBusy || button.dataset.canScheduleInbound !== "true";
    });
}

function setConnectionStatus(text, className) {
    elements.connectionStatus.textContent = text;
    elements.connectionStatus.className = `connection-status ${className}`;
}

function showMessage(message, type) {
    elements.messageArea.hidden = false;
    elements.messageArea.textContent = translateMessage(message);
    elements.messageArea.className = `message-area ${type === "error" || type === "success" ? type : ""}`;
}

function escapeHtml(value) {
    return String(value ?? "")
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;")
        .replace(/'/g, "&#039;");
}
