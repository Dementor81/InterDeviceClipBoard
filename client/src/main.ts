const TOKEN_KEY = "idcb-token";
const TOKEN = /^[A-Za-z0-9_-]{43}$/;

type FileRow = {
  id: string;
  name: string;
  mediaType: string;
  size: number;
  createdAt: string;
  downloadPath: string;
};

const message = element<HTMLParagraphElement>("message");
const board = element<HTMLElement>("board");
const missing = element<HTMLElement>("missing");
const boardLink = element<HTMLInputElement>("board-link");
const copyBoard = element<HTMLButtonElement>("copy-board");
const drop = element<HTMLLabelElement>("drop");
const picker = element<HTMLInputElement>("picker");
const progress = element<HTMLParagraphElement>("progress");
const empty = element<HTMLParagraphElement>("empty");
const filesList = element<HTMLUListElement>("files");
const newBoard = element<HTMLButtonElement>("new-board");

let token = "";
let queue: File[] = [];
let pumping = false;
let dragDepth = 0;

copyBoard.addEventListener("click", () => {
  void copyText(boardLink.value, copyBoard);
});

picker.addEventListener("change", () => {
  enqueue(Array.from(picker.files ?? []));
  picker.value = "";
});

drop.addEventListener("dragenter", (event) => {
  event.preventDefault();
  dragDepth += 1;
  drop.classList.add("hot");
});

drop.addEventListener("dragover", (event) => {
  event.preventDefault();
});

drop.addEventListener("dragleave", () => {
  dragDepth -= 1;
  if (dragDepth <= 0) {
    dragDepth = 0;
    drop.classList.remove("hot");
  }
});

drop.addEventListener("drop", (event) => {
  event.preventDefault();
  dragDepth = 0;
  drop.classList.remove("hot");
  enqueue(Array.from(event.dataTransfer?.files ?? []));
});

document.addEventListener("paste", (event) => {
  if (board.hidden || !event.clipboardData)
    return;

  const files = filesFromPaste(event.clipboardData);
  if (files.length === 0)
    return;

  event.preventDefault();
  enqueue(files);
});

newBoard.addEventListener("click", () => {
  localStorage.removeItem(TOKEN_KEY);
  location.assign("/");
});

void start();

async function start(): Promise<void> {
  const pathToken = tokenFromPath();
  if (pathToken) {
    token = pathToken;
    await refresh();
    return;
  }

  const saved = localStorage.getItem(TOKEN_KEY);
  if (saved && TOKEN.test(saved)) {
    location.replace(`/c/${saved}`);
    return;
  }

  const response = await fetch("/api/clipboards", { method: "POST", cache: "no-store" });
  if (response.status === 429) {
    setStatus("Too many new clipboards from this network. Try again later.");
    return;
  }
  if (!response.ok) {
    setStatus("Couldn't create a clipboard.");
    return;
  }

  const body = (await response.json()) as { token: string };
  localStorage.setItem(TOKEN_KEY, body.token);
  location.replace(`/c/${body.token}`);
}

async function refresh(): Promise<void> {
  const response = await fetch("/api/files", {
    headers: { Authorization: `Bearer ${token}` },
    cache: "no-store"
  });
  if (response.status === 404) {
    showMissing();
    return;
  }
  if (!response.ok) {
    setStatus("Couldn't load files.");
    return;
  }

  localStorage.setItem(TOKEN_KEY, token);
  const files = (await response.json()) as FileRow[];
  boardLink.value = `${location.origin}/c/${token}`;
  setStatus("");
  showBoard();
  render(files);
}

function enqueue(files: File[]): void {
  if (files.length === 0)
    return;
  queue.push(...files);
  void pump();
}

async function pump(): Promise<void> {
  if (pumping)
    return;

  pumping = true;
  while (queue.length > 0) {
    const file = queue.shift();
    if (!file)
      break;
    const ok = await upload(file);
    if (!ok) {
      queue = [];
      break;
    }
    await refresh();
  }
  pumping = false;
  progress.textContent = "";
}

function upload(file: File): Promise<boolean> {
  return new Promise((resolve) => {
    const xhr = new XMLHttpRequest();
    xhr.open("POST", "/api/files");
    xhr.setRequestHeader("Authorization", `Bearer ${token}`);
    xhr.upload.onprogress = (event) => {
      if (event.lengthComputable && event.total > 0) {
        const percent = Math.round((event.loaded / event.total) * 100);
        progress.textContent = `Uploading ${file.name} — ${percent}%`;
      } else {
        progress.textContent = `Uploading ${file.name}…`;
      }
    };
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        resolve(true);
        return;
      }
      if (xhr.status === 413)
        setStatus("That file doesn't fit.");
      else if (xhr.status === 404)
        showMissing();
      else
        setStatus("Upload failed.");
      resolve(false);
    };
    xhr.onerror = () => {
      setStatus("Upload failed.");
      resolve(false);
    };
    const body = new FormData();
    body.append("file", file, file.name);
    xhr.send(body);
  });
}

function render(files: FileRow[]): void {
  filesList.replaceChildren();
  empty.hidden = files.length > 0;
  for (const file of files) {
    const item = document.createElement("li");
    const info = document.createElement("div");
    const name = document.createElement("p");
    name.className = "name";
    name.textContent = file.name;
    const meta = document.createElement("p");
    meta.className = "meta";
    meta.textContent = `${formatSize(file.size)} · ${formatTime(file.createdAt)}`;
    info.append(name, meta);

    const actions = document.createElement("div");
    actions.className = "actions";
    const download = document.createElement("a");
    download.href = file.downloadPath;
    download.textContent = "Download";
    const copy = document.createElement("button");
    copy.type = "button";
    copy.textContent = "Copy link";
    copy.addEventListener("click", () => {
      void copyText(`${location.origin}${file.downloadPath}`, copy);
    });
    const trash = document.createElement("button");
    trash.type = "button";
    trash.className = "trash";
    trash.setAttribute("aria-label", `Delete ${file.name}`);
    trash.append(trashIcon());
    trash.addEventListener("click", () => {
      void remove(file);
    });
    actions.append(download, copy, trash);
    item.append(info, actions);
    filesList.append(item);
  }
}

async function remove(file: FileRow): Promise<void> {
  if (!confirm(`Delete ${file.name}?`))
    return;

  const response = await fetch(`/api/files/${file.id}`, {
    method: "DELETE",
    headers: { Authorization: `Bearer ${token}` },
    cache: "no-store"
  });
  if (!response.ok)
    await refresh();
  if (board.hidden)
    return;
  if (!response.ok) {
    setStatus("Couldn't delete that file.");
    return;
  }
  await refresh();
}

function filesFromPaste(data: DataTransfer): File[] {
  const listed = Array.from(data.files);
  if (listed.length > 0)
    return listed.map((file) => (file.type.startsWith("image/") ? pastedImage(file) : file));

  const fromItems: File[] = [];
  for (const item of data.items) {
    if (item.kind !== "file")
      continue;
    const file = item.getAsFile();
    if (file)
      fromItems.push(file.type.startsWith("image/") ? pastedImage(file) : file);
  }
  if (fromItems.length > 0)
    return fromItems;

  const text = data.getData("text/plain");
  return text ? [pastedText(text)] : [];
}

function pastedImage(file: File): File {
  const subtype = file.type.slice("image/".length).split("+")[0] ?? "img";
  const ext = subtype === "jpeg" ? "jpg" : subtype || "img";
  return new File([file], `pasted-image.${ext}`, { type: file.type });
}

function pastedText(text: string): File {
  const stamp = new Date().toISOString().replace(/[:.]/g, "-");
  return new File([text], `text-${stamp}.txt`, { type: "text/plain" });
}

async function copyText(text: string, button: HTMLButtonElement): Promise<void> {
  const previous = button.textContent ?? "";
  try {
    await navigator.clipboard.writeText(text);
    button.textContent = "Copied";
  } catch {
    button.textContent = "Copy failed";
  }
  window.setTimeout(() => {
    button.textContent = previous;
  }, 1500);
}

function formatSize(bytes: number): string {
  if (bytes < 1024)
    return `${bytes} B`;

  const units = ["KB", "MB", "GB", "TB"];
  let size = bytes / 1024;
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit += 1;
  }
  return `${size >= 10 ? size.toFixed(0) : size.toFixed(1)} ${units[unit]}`;
}

function formatTime(iso: string): string {
  return new Date(iso).toLocaleString();
}

function tokenFromPath(): string | null {
  return location.pathname.match(/^\/c\/([A-Za-z0-9_-]{43})$/)?.[1] ?? null;
}

function showBoard(): void {
  board.hidden = false;
  missing.hidden = true;
}

function showMissing(): void {
  board.hidden = true;
  missing.hidden = false;
  setStatus("");
}

function setStatus(text: string): void {
  message.textContent = text;
}

function trashIcon(): SVGSVGElement {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("aria-hidden", "true");
  svg.setAttribute("fill", "none");
  svg.setAttribute("stroke", "currentColor");
  svg.setAttribute("stroke-width", "1.8");
  svg.setAttribute("stroke-linecap", "round");
  svg.setAttribute("stroke-linejoin", "round");
  const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
  path.setAttribute("d", "M4 7h16M9 7V5h6v2M7 7l1 13h8l1-13");
  svg.append(path);
  return svg;
}

function element<T extends HTMLElement>(id: string): T {
  const node = document.getElementById(id);
  if (!node)
    throw new Error(`Missing #${id}`);
  return node as T;
}
