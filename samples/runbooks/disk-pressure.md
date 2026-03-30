# Node disk pressure

Kubernetes sets the `DiskPressure` condition when a node's free disk space falls under the eviction threshold,
and starts evicting pods.

## Diagnosis

Run `kubectl describe node <node>` and look for `DiskPressure=True` and `Evicted` events. On the node,
`du -sh /var/lib/containerd /var/log` shows whether images or logs fill the disk.

## Mitigation

- Prune unused images: `crictl rmi --prune`.
- Container logs: make sure every app logs to stdout and that log rotation is on; large files under
  `/var/log/pods` usually come from a debug log level left on.
- Images that keep apt caches waste space on every node; the Dockerfile lint rule DF005 catches it.
