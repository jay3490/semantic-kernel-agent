# High 5xx error rate

Alert: `OrdersApiHighErrorRate` fires when more than 2% of requests fail for 5 minutes.

## First five minutes

- Check whether a deploy happened in the last hour; if so, roll it back first and investigate after.
- Look at the most frequent error signatures in the API logs (the agent's `analyze_log_file` tool groups them).

## Payment gateway timeout

Errors such as `payment gateway timeout after 5000ms` mean the upstream payment provider is slow.

1. Check the provider status page.
2. Confirm the circuit breaker opened: the log shows `circuit open` within a minute of the first timeouts.
3. If it did not open, lower `Payments__TimeoutMs` to 2000 so threads are not held, and page the payments
   on-call.

Do not retry payment calls more than once; a retried capture can charge a customer twice.

## Database connection pool exhausted

`timeout expired while waiting for a connection from the pool` means requests hold connections too long.
Restart one replica at a time to clear stuck connections, then look for slow queries in the database metrics.
