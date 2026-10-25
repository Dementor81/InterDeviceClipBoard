import { encode } from "uqr";

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
const boardActions = element<HTMLDivElement>("board-actions");
const missing = element<HTMLElement>("missing");
const copyBoard = element<HTMLButtonElement>("copy-board");
const qrBoard = element<HTMLButtonElement>("qr-board");
const qrPanel = element<HTMLDivElement>("qr-panel");
const qrCode = element<HTMLDivElement>("qr-code");
const drop = element<HTMLLabelElement>("drop");
const picker = element<HTMLInputElement>("picker");
const progress = element<HTMLParagraphElement>("progress");
const empty = element<HTMLParagraphElement>("empty");
const filesList = element<HTMLUListElement>("files");
const newBoard = element<HTMLButtonElement>("new-board");
const about = element<HTMLElement>("about");

let token = "";
let queue: File[] = [];
let pumping = false;
let dragDepth = 0;
let qrFor = "";

copyBoard.addEventListener("click", () => {
  void copyBoardLink();
});

qrBoard.addEventListener("click", () => {
  setQrOpen(qrPanel.hidden);
});

qrPanel.addEventListener("click", (event) => {
  if (event.target === qrPanel)
    setQrOpen(false);
});

document.addEventListener("keydown", (event) => {
  if (event.key === "Escape" && !qrPanel.hidden)
    setQrOpen(false);
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

void loadAbout();
void start();

async function loadAbout(): Promise<void> {
  const response = await fetch("/api/version", { cache: "no-store" });
  if (!response.ok)
    return;

  const body = (await response.json()) as { version: string; deployed: string };
  const date = deployedDate(body.deployed);
  about.textContent = date ? `Version ${body.version} · ${date}` : `Version ${body.version}`;
}

function deployedDate(iso: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(iso);
  if (!match)
    return "";

  const date = new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3])));
  return date.toLocaleDateString(undefined, { timeZone: "UTC", day: "numeric", month: "short", year: "numeric" });
}

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
  if (response.status === 409) {
    setStatus("This server isn't accepting new clipboards.");
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
  if (!qrPanel.hidden)
    paintQr();
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
    download.title = "Download";
    download.setAttribute("aria-label", `Download ${file.name}`);
    download.append(icon(
      `<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><path d="m7 10 5 5 5-5"/><path d="M12 15V3"/>`
    ));
    const copy = document.createElement("button");
    copy.type = "button";
    copy.title = "Copy link";
    copy.setAttribute("aria-label", `Copy link to ${file.name}`);
    copy.append(
      icon(`<rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V5h10"/>`, "icon-copy"),
      icon(`<path d="M5 12.5 9.5 17 19 7"/>`, "icon-check")
    );
    copy.addEventListener("click", () => {
      void copyText(`${location.origin}${file.downloadPath}`, copy);
    });
    const trash = document.createElement("button");
    trash.type = "button";
    trash.className = "trash";
    trash.title = "Delete";
    trash.setAttribute("aria-label", `Delete ${file.name}`);
    trash.append(icon(`<path d="M4 7h16M9 7V5h6v2M7 7l1 13h8l1-13"/>`));
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

async function copyBoardLink(): Promise<void> {
  await copyText(boardUrl(), copyBoard);
}

async function copyText(text: string, button: HTMLButtonElement): Promise<void> {
  if (button.classList.contains("copied"))
    return;

  const label = button.getAttribute("aria-label") ?? "Copy link";
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    setStatus("Couldn't copy the link.");
    return;
  }

  button.classList.add("copied");
  button.setAttribute("aria-label", "Copied");
  window.setTimeout(() => {
    button.classList.remove("copied");
    button.setAttribute("aria-label", label);
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

function boardUrl(): string {
  return `${location.origin}/c/${token}`;
}

function setQrOpen(open: boolean): void {
  qrPanel.hidden = !open;
  qrBoard.setAttribute("aria-expanded", String(open));
  if (open)
    paintQr();
}

function paintQr(): void {
  const text = boardUrl();
  if (!text || text === qrFor)
    return;

  qrFor = text;
  const qr = encode(text, { ecc: "M", border: 2 });
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("viewBox", `0 0 ${qr.size} ${qr.size}`);
  svg.setAttribute("role", "img");
  svg.setAttribute("aria-label", "QR code for this clipboard link");
  const background = document.createElementNS("http://www.w3.org/2000/svg", "rect");
  background.setAttribute("width", String(qr.size));
  background.setAttribute("height", String(qr.size));
  svg.append(background);
  const modules = document.createElementNS("http://www.w3.org/2000/svg", "path");
  const parts: string[] = [];
  for (let y = 0; y < qr.size; y++) {
    const row = qr.data[y];
    if (!row)
      continue;
    for (let x = 0; x < qr.size; x++) {
      if (row[x])
        parts.push(`M${x} ${y}h1v1h-1z`);
    }
  }
  modules.setAttribute("d", parts.join(""));
  svg.append(modules);
  qrCode.replaceChildren(svg);
}

function tokenFromPath(): string | null {
  return location.pathname.match(/^\/c\/([A-Za-z0-9_-]{43})$/)?.[1] ?? null;
}

function showBoard(): void {
  board.hidden = false;
  boardActions.hidden = false;
  missing.hidden = true;
}

function showMissing(): void {
  setQrOpen(false);
  board.hidden = true;
  boardActions.hidden = true;
  missing.hidden = false;
  setStatus("");
}

function setStatus(text: string): void {
  message.textContent = text;
}

function icon(markup: string, className = ""): SVGSVGElement {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("aria-hidden", "true");
  svg.setAttribute("fill", "none");
  svg.setAttribute("stroke", "currentColor");
  svg.setAttribute("stroke-width", "1.8");
  svg.setAttribute("stroke-linecap", "round");
  svg.setAttribute("stroke-linejoin", "round");
  if (className)
    svg.setAttribute("class", className);
  svg.innerHTML = markup;
  return svg;
}

function element<T extends HTMLElement>(id: string): T {
  const node = document.getElementById(id);
  if (!node)
    throw new Error(`Missing #${id}`);
  return node as T;
}
