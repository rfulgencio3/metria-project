Stripe Integration Notes (Backend)

Endpoints
- `POST /api/billing/checkout`
  - Creates Stripe Checkout Session in subscription mode.
  - Uses user context and attempts to reuse/create Stripe customer.
- `POST /api/billing/webhook`
  - Validates signature using `STRIPE_WEBHOOK_SECRET`.
  - Handles `checkout.session.completed` and `customer.subscription.*`.
- `POST /api/billing/sync`
  - Manual reconciliation by subscription/customer/email.
- `GET /api/billing/subscription`
  - Returns current user subscription status for paywall.

Required Config
- `STRIPE_SECRET_KEY`
- `STRIPE_WEBHOOK_SECRET`

Optional Config
- `STRIPE_MONTHLY_PRICE_ID`
- `STRIPE_ANNUAL_PRICE_ID`

Production Webhook Setup
1. Create endpoint in Stripe:
   - `https://SEU-BACKEND/api/billing/webhook`
2. Subscribe events:
   - `checkout.session.completed`
   - `customer.subscription.created`
   - `customer.subscription.updated`
   - `customer.subscription.deleted`
3. Save signing secret in backend variable:
   - `STRIPE_WEBHOOK_SECRET=whsec_...`

Validation Signals in Logs
- `Stripe webhook event received: type=... id=...`
- Success path logs for upsert/update on subscription rows.

Common Issues
- Live links with test keys (or inverse) break reconciliation.
- Wrong webhook signing secret causes signature validation failure.
- Payment email mismatch can require `/api/billing/sync` fallback.

Dev CLI Example
- `stripe listen --events checkout.session.completed,customer.subscription.created,customer.subscription.updated,customer.subscription.deleted --forward-to http://localhost:5104/api/billing/webhook`
- Add `--live` when validating live mode.
