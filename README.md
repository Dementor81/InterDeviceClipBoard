# Clipboard

Personal clipboard for moving files between devices. The link is the key.

Opening the site creates a clipboard. Bookmark the `/c/{token}` link to return to it. The same link on another device shows the same files. Each file also has a download link that can fetch that file and nothing else.

Uploaded bytes are encrypted on disk. The server stores only a hash of the clipboard token. A clipboard that is not opened for `AbandonedAfterDays` (default 30) is deleted with its files. Opening the link resets that timer.

## Run locally

Requires the .NET 10 SDK and Node.js 24.

```bash
cd client
npm install
cd ..
dotnet run --project src/Clipboard.Server
```

Open http://localhost:8080.

## Docker

```bash
docker compose up --build
```

The app listens on 127.0.0.1:8080. Put HTTPS in front of it, and do not publish port 8080 on a public interface. Request paths contain the clipboard token and download keys.
Do not log request URIs. A clipboard token or a download key in an access log is a copy of the secret.

## Configuration

- `DataDir` — database and encrypted files. Default `data`. In Compose, `/data`.
- `MaxUploadBytes` — single file cap. Default 1073741824 (1 GiB).
- `MaxTotalBytes` — total plaintext cap. Default 10737418240 (10 GiB).
- `AbandonedAfterDays` — delete a clipboard that has not been opened for this many days. Default 30.
