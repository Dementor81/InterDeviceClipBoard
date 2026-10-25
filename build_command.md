docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t marcus360/world_wide_digital_clipboard:latest \
  --push \
  .