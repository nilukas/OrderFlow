# OrderFlow

A small but production-shaped system that shows tech-lead-level .NET design: **two services talking through a message broker, containerised, with an AI step that is safe to fail**.

```
 client ──JWT──▶  Orders API  ──(one DB transaction)──▶  PostgreSQL (orders + outbox)
                     │
                outbox publisher (background)
                     │  persistent message + publisher confirm
                     ▼
                 RabbitMQ  ──▶  Notifications worker  ──▶  PostgreSQL (processed_messages)
                     │                  │
                     └─ dead-letter     └─ AI summary (Claude)  ──fallback──▶  deterministic template
                        queue
```

## What each piece demonstrates

| Piece | Why a tech lead cares |
|---|---|
| `docs/openapi.yaml` | **Contract first.** The API is defined before the code; clients are generated from it. |
| `Order` aggregate (`Domain/Order.cs`) | **Invariants live in the domain**, with bounded inputs (OWASP input validation). |
| Transactional **outbox** | The order and its event are saved in **one transaction**, so you never lose or invent events. |
| RabbitMQ publisher with **confirms** | The broker has accepted the message before we mark it published. |
| **Idempotent consumer** | Brokers deliver *at least once*. A primary key on `MessageId` makes duplicates harmless. |
| **Dead-letter queue** | Unreadable (poison) messages are parked instead of retried forever. |
| JWT + **scope policies** | Every endpoint is protected; the service holds no secret (authority and audience only). |
| `TimeProvider` | Time is injected, so tests are deterministic. |
| AI with **guardrails** | Only non-personal data goes to the model; hard timeout; output validated; template fallback. |
| Dockerfiles + compose + CI | Multi-stage, non-root images; infrastructure reproducible with one command. |

## Run the tests (no Docker needed)

```bash
dotnet test
```

23 tests cover the domain rules, the secured API (401/403/400/404/201), the outbox, the idempotent consumer and the AI guardrails.

## Run it locally

1. Copy `.env.example` to `.env` and set the passwords.
2. Start everything: `docker compose up --build`
3. RabbitMQ UI: http://localhost:15672 (use the user/password from `.env`).
4. The API needs a real access token. Either point `OIDC_AUTHORITY` at your identity provider, or for local `dotnet run` use Microsoft's dev tool: `dotnet user-jwts create --scope orders.write --scope orders.read`.
5. Create an order: `POST http://localhost:8080/api/v1/orders` with the body from `docs/openapi.yaml`, then watch the `notifications` container log the summary.

## Step-by-step: how it was built (and how to talk about it)

1. **Write the contract** (`docs/openapi.yaml`) so the API shape is agreed first.
2. **Model the domain** (`Order`, `OrderLine`) with validation inside the aggregate; write unit tests first.
3. **Persist with EF Core + PostgreSQL**; owned collection for lines; `OutboxMessage` table next to `orders`.
4. **Expose the API** as minimal-API endpoints; map `DomainException` to a `400` problem response.
5. **Add the outbox** in the same `SaveChanges` as the order (atomic), then a **background publisher** that sends pending rows and marks them published.
6. **Publish to RabbitMQ** with durable queues, persistent messages, and publisher confirms.
7. **Build the consumer** with manual acknowledgements, prefetch limit, and a dead-letter queue.
8. **Make it idempotent**: claim the `MessageId` first; release the claim if processing fails.
9. **Add the AI step** behind an interface, with a deterministic fallback, so AI can never break order flow.
10. **Secure it**: JWT bearer validation + scope-based authorization policies.
11. **Containerise**: multi-stage Dockerfiles, non-root user, compose with health checks.
12. **Automate**: CI builds, tests, and builds the images on every push.

## Deliberate simplifications (say these out loud in an interview)

- `EnsureCreated()` instead of EF migrations; use `dotnet ef migrations` in production.
- One outbox poller; with several instances use `SELECT ... FOR UPDATE SKIP LOCKED`.
- Failed messages are requeued immediately; production would use a delayed retry queue with a retry limit, then the DLQ.
- No Idempotency-Key header on `POST /orders`; add it so client retries cannot create duplicate orders.
- RabbitMQ and Postgres use passwords from environment variables; in cloud use managed identity / a secret store, and prefer OAuth where the service supports it.
- The Anthropic API is called with an API key (the provider's mechanism); keep it in a secret store, never in the repo.

## Good next steps

- Add **OpenTelemetry** traces and metrics across both services.
- Add a **React + TypeScript** front end generated from the OpenAPI spec.
- Add **Terraform** for the cloud infrastructure and deploy the images (Azure Container Apps or AWS ECS).
- Add **GitHub Actions** deployment and a contract test that checks the API against `openapi.yaml`.
