# Digital Co-Founder BPO Agent

**AI-Powered Autonomous BPO Platform**  
*Version 23.1 – Phase 3 (API + Workers + GraphQL + Tenant Isolation)*

A fully autonomous digital co-founder that runs a complete BPO operation: scrapes jobs, auto-bids, onboards clients via voice + web, manages projects, escrow payments, QA, and developer matching — all with multi-tenant isolation.

---

## ✨ Key Features

- **Multi-tenant architecture** with strict tenant isolation (EF Core + middleware)
- **REST API + full GraphQL** endpoint (`/graphql`)
- **Background workers** (job scraping, auto-bidding, onboarding, QA, pricing, etc.)
- **AI Decision Engine** powered by Semantic Kernel + OpenAI/Azure OpenAI
- **Voice onboarding** (Twilio TwiML ready)
- **Escrow & payment webhooks** (Grey, PayFast)
- **Redis caching + RabbitMQ event bus**
- **Serilog + OpenTelemetry + Prometheus metrics**
- **Rate limiting + security headers**
- **Docker + Docker Compose** ready

---

## 🛠 Tech Stack

- **Backend**: .NET 8 (ASP.NET Core Minimal APIs + Hot Chocolate GraphQL)
- **Database**: PostgreSQL + pgvector (for embeddings)
- **Cache**: Redis
- **Queue**: RabbitMQ
- **AI**: Microsoft Semantic Kernel + OpenAI
- **Auth**: JWT + Refresh Tokens
- **Frontend (Phase 4)**: Blazor Server/WebAssembly (planned)
- **Observability**: Prometheus + OpenTelemetry
- **CI/CD**: GitHub Actions (Phase 4)

---

## 🚀 Quick Start (Docker recommended)

```bash
# 1. Clone
git clone https://github.com/AnuBark/Digital-Co-Founder-BPO-Agent.git
cd Digital-Co-Founder-BPO-Agent

# 2. Create appsettings.json from template (or use environment variables)
cp appsettings.json.example appsettings.json

# 3. Start everything
docker compose up --build
