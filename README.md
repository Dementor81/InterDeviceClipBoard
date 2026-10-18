# Clipboard

Personal clipboard for moving files between devices. The link is the key.

Opening the site creates a clipboard and remembers it in that browser. The same `/c/{token}` link on another device shows the same files. Each file also has a download link that can fetch that file and nothing else.

Uploaded bytes are encrypted on disk. The server stores only a hash of the clipboard token. A clipboard that is not opened for `AbandonedAfterDays` (default 30) is deleted with its files. Opening the link resets that timer.

## Run locally

Requires the .NET 10 SDK and Node.js.

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

### Caddy

```
example.com {
    reverse_proxy 127.0.0.1:8080
    log {
        output discard
    }
}
```

### nginx

```
server {
    listen 443 ssl;
    server_name example.com;
    access_log off;
    client_max_body_size 1100m;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Forwarded-Proto https;
        proxy_request_buffering off;
        proxy_read_timeout 1h;
    }
}
```

Do not log request URIs. A clipboard token or a download key in an access log is a copy of the secret.

## Configuration

- `DataDir` — database and encrypted files. Default `data`. In Compose, `/data`.
- `MaxUploadBytes` — single file cap. Default 1073741824 (1 GiB).
- `MaxTotalBytes` — total plaintext cap. Default 10737418240 (10 GiB).
- `AbandonedAfterDays` — delete a clipboard that has not been opened for this many days. Default 30.
