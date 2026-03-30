# Pods in CrashLoopBackOff

Owner: platform team. Applies to every Deployment in the shop namespace.

## Symptoms

`kubectl get pods` shows `CrashLoopBackOff` and a restart count that keeps growing. The service may still
answer if some replicas are healthy, but new rollouts stall.

## Diagnosis

1. Read the last crash, not the current attempt:

   ```bash
   kubectl logs deploy/orders-api --previous --tail=100
   ```

2. Check the exit reason with `kubectl describe pod <pod>`. `OOMKilled` means the memory limit is too low
   for the workload; exit code 137 without OOMKilled usually means the liveness probe killed it.
3. If the probe is killing it, compare `initialDelaySeconds` with how long the app takes to start.

## Mitigation

- Roll back with `kubectl rollout undo deploy/orders-api` if the crash started with a deploy.
- For OOMKilled, raise `resources.limits.memory` and redeploy; do not remove the limit (lint rule K8S002).
- For slow starts, add a `startupProbe` instead of loosening the liveness probe.
