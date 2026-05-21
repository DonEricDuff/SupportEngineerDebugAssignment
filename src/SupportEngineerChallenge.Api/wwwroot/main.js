const state = {
  tasks: []
};

const userSelect = document.getElementById("userSelect");
const titleInput = document.getElementById("titleInput");
const addBtn = document.getElementById("addBtn");
const refreshBtn = document.getElementById("refreshBtn");
const tbody = document.getElementById("tbody");
const errorEl = document.getElementById("error");

function setError(msg) {
  errorEl.textContent = msg || "";
}

function render() {
  tbody.innerHTML = "";
  for (const t of state.tasks) {
    const tr = document.createElement("tr");
    tr.innerHTML = `
      <td>${t.id}</td>
      <td>${escapeHtml(t.title)}</td>
      <td>${t.status}</td>
      <td>${new Date(t.createdAt).toLocaleString()}</td>
    `;
    tbody.appendChild(tr);
  }
}

function escapeHtml(str) {
  return String(str).replace(/[&<>"']/g, (m) => ({ "&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;","'":"&#039;" }[m]));
}

async function refresh() {
  setError("");
  const userId = userSelect.value;

  const res = await fetch(`/api/tasks?userId=${encodeURIComponent(userId)}&limit=50`);
  if (!res.ok) {
    setError(`Refresh failed: ${res.status} ${res.statusText}`);
    return;
  }

  const items = await res.json();

  state.tasks = items
    .sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));

  render();
}

async function addTask() {
  setError("");
  const userId = userSelect.value;
  const title = titleInput.value;

  const body = { userId, title };

  const headers = {
    "Content-Type": "application/json",
    "X-Client-Timestamp": new Date().toISOString()
  };

  const res = await fetch("/api/tasks", {
    method: "POST",
    headers,
    body: JSON.stringify(body)
  });

  if (!res.ok) {
    const txt = await res.text();
    setError(`Add failed: ${res.status} ${res.statusText}\n\n${txt}`);
    return;
  }

  titleInput.value = "";
  await refresh();
}

addBtn.addEventListener("click", addTask);
refreshBtn.addEventListener("click", refresh);
userSelect.addEventListener("change", () => { state.tasks = []; refresh(); });

refresh();
