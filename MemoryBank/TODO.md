Backlog / Ideas (Backend)

- Add persistent idempotency store for webhook events (database-based), not only in-memory cache.
- Add a mapping table `StripeCustomerId -> UserId` to reduce dependency on email matching.
- Add scheduled reconciliation job to backfill subscriptions from Stripe API.
- Add observability:
  - metrics for webhook delivery and processing latency
  - counters for subscription state transitions
- Externalize ASP.NET DataProtection keys for multi-instance production scenarios.
- Add automated smoke checks post-deploy (`/health-check`, OAuth start redirect, webhook signature validation test).
- Add runbook section for Railway domain/ingress incidents (502 with healthy container).
