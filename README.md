# Compass — AI Agent Governance Layer for Slack

Compass is a governance control plane for AI agents operating inside Slack. It sits at the intersection of the MCP protocol and Slack's agent orchestration layer, enforcing risk-scored policy on every agent action — whether triggered by a human or by another agent — before it reaches any tool.

---

## Architecture

```
                        Slack Workspace
┌───────────────────────────────────────────────────────────────┐
│                                                               │
│  Human / Slackbot Orchestrator                                │
│          ↓                                                    │
│  ┌─────────────────────────────────────────────────────┐      │
│  │           Compass Control Plane                     │      │
│  │                                                     │      │
│  │  ┌──────────────┐   ┌──────────────────────────┐   │      │
│  │  │  MCP Proxy   │   │  Slack Events Proxy       │   │      │
│  │  │  POST /mcp   │   │  POST /slack/events       │   │      │
│  │  └──────┬───────┘   └────────────┬─────────────┘   │      │
│  │         │                        │                  │      │
│  │         └──────────┬─────────────┘                  │      │
│  │                    ↓                                 │      │
│  │         GovernanceKernel + AIVSS Scorer              │      │
│  │         Policy Engine (YAML rules + OWASP AA01-10)  │      │
│  │         Ring Model (0–3) + Trust Score (0–1000)     │      │
│  │         Circuit Breaker (SRE layer)                 │      │
│  │                    ↓                                 │      │
│  │     ┌──────────────────────────────────┐            │      │
│  │     │  ALLOW → forward to Slack MCP    │            │      │
│  │     │  DENY  → return policy_denied    │            │      │
│  │     │  PENDING → Slack approval card   │            │      │
│  │     │            → Service Bus queue   │            │      │
│  │     └──────────────────────────────────┘            │      │
│  └─────────────────────────────────────────────────────┘      │
│                                                               │
│  Agent A → Agent B calls also pass through Compass            │
│  (confused deputy detection + ring elevation block)           │
│                                                               │
└───────────────────────────────────────────────────────────────┘
```

---

## Two Governance Gates

### Gate 1 — Slack Events Proxy (`POST /slack/events`)
Verifies Slack request signatures and evaluates every inbound Slack event through `GovernanceKernel` before the agent handler sees it. Blocks prompt injection, excessive agency, and unauthorized channel access.

### Gate 2 — MCP Proxy (`POST /mcp`)
Intercepts outbound MCP tool calls at the JSON-RPC layer. Only `method: "tools/call"` is governed — `initialize`, `tools/list`, and other non-mutating methods pass through. Governed tool calls are AIVSS-scored and either forwarded, blocked, or held for human approval.

---

## MCP Client Enforcement Model

Compass positions itself as the MCP server that Slack's agent runtime (acting as MCP client) calls before reaching any downstream tool. This gives the company — not the agent vendor — authority over what agents can do.

```
Slack Agent Runtime (MCP Client)
        ↓
  compass://check-action       ← Compass MCP Server
        ↓
  ALLOW  → downstream tool MCP server
  DENY   → error returned to agent
  PENDING → approval card sent in Slack, agent suspended
```

Compass exposes three MCP tools:

| Tool | Purpose |
|---|---|
| `check-action` | Score and decide on an agent action before execution |
| `get-approval-status` | Poll for human approval on a pending action |
| `audit-log` | Write an immutable audit entry after execution |

Denied calls return Slack-style JSON so existing agents require no changes:

```json
{ "ok": false, "error": "compass_policy_denied" }
```

---

## Multi-Agent Orchestration Enforcement

When Slackbot orchestrates multiple agents, agent-to-agent calls pass through the same governance gates as human-to-agent calls. Compass adds two additional checks for inter-agent calls:

### Confused Deputy Detection
Agent A cannot delegate an action it does not hold permission to perform itself. This blocks privilege escalation through the agent chain.

```
Email Agent (Ring 2) → requests DB export → Compass
                                               ↓
                         Ring 2 agent lacks DATA_EXFILTRATION permission
                                               ↓
                                           DENIED
```

### Ring Elevation Block
An agent cannot invoke an action whose required ring is lower (more trusted) than its own ring.

| Ring | Trust Level | Max Action Tier |
|---|---|---|
| 0 | Core system | CRITICAL |
| 1 | Verified enterprise | HIGH |
| 2 | Standard marketplace | MEDIUM |
| 3 | New / unverified | LOW |

New agents start at Ring 3 and earn trust through clean action history. A policy violation immediately drops the dynamic trust score (0–1000) and can trigger demotion.

### Circuit Breaker (SRE Layer)
If an agent fails 3 consecutive times within 5 minutes, its circuit opens and all further calls from that agent are blocked for 10 minutes. This prevents a misbehaving agent from cascading failures through the workflow chain.

---

## AIVSS Risk Scoring

Every intercepted action is scored using the **OWASP AIVSS formula** (AI Vulnerability Scoring System v0.8) with **Microsoft Agent Governance Toolkit action classification**:

```
AIVSS Score = ((CVSS_Base + AARS) / 2) × ThreatMultiplier × MitigationFactor
```

| Microsoft Action Class | CVSS Base | Approval Tier |
|---|---|---|
| `READ_ONLY` | 1.5 | Auto-approve |
| `WRITE_BENIGN` | 3.0 | Auto-approve |
| `WRITE_SENSITIVE` | 5.0 | Team member |
| `PRIVILEGE_ESCALATION` | 7.0 | Manager |
| `DATA_EXFILTRATION` | 8.0 | Senior leader |
| `DESTRUCTIVE_DATA` | 9.5 | Dual approval / Block |

AARS adds 10 agentic amplification factors (Autonomy, Blast Radius, Reversibility, Multi-Agent Interaction, etc.) each scored 0 / 0.5 / 1.0.

Score thresholds:

```
0  – 30  → AUTO_APPROVE
31 – 60  → TEAM_APPROVAL
61 – 80  → MANAGER_APPROVAL
81 – 95  → SENIOR_APPROVAL
96 – 100 → BLOCKED
```

---

## Startup Order

1. `Program.cs` + DI container
2. Load `policies/slack-events.yaml`
3. Slack app registration + signing secret
4. `SlackEventController` — Gate 1 live
5. `curl` test: injection blocked
6. `McpProxyController` — Gate 2 live
7. `curl` test: destructive tool blocked
8. Ring model + trust scorer initialised (`trust/scores.json`)
9. Circuit breaker wired to agent registry (`data/agents.json`)
10. ngrok tunnel + Slack Events URL configured
11. Service Bus approval flow (`compass-approval-decisions` queue)
12. Audit logging (`data/audit.jsonl`)
13. AGT verification (`./agt-verification/run_verify.sh`)
14. Demo polish

---

## Local Setup

Create a Slack app at api.slack.com/apps, then copy the signing secret into local user secrets:

```bash
cd /Users/muskan/slack/compass/Compass.Api
dotnet user-secrets init
dotnet user-secrets set "Slack:SigningSecret" "<your-signing-secret>"
dotnet user-secrets set "Slack:BotToken" "<your-bot-token>"
```

Run the API:

```bash
dotnet run --project Compass.Api
```

Or run through Aspire to see the dashboard, logs, and resource status:

```bash
dotnet run --project Compass.AppHost
```

Aspire prints a dashboard login URL:

```
https://localhost:17000/login?t=...
```

Run the test scripts in another terminal:

```bash
cd /Users/muskan/slack/compass
SLACK_SIGNING_SECRET="<your-signing-secret>" ./scripts/test-slack-injection.sh
./scripts/test-mcp-approval.sh
./scripts/test-inter-agent-guard.sh      # confused deputy + ring elevation
./scripts/test-circuit-breaker.sh        # SRE circuit breaker
```

---

## Proxy Endpoints

### MCP Proxy

```
POST /mcp
```

`initialize`, `tools/list`, and all non-tool-call methods pass through without governance. Only `tools/call` is intercepted.

```
POST /mcp-client/register
```

Register an external MCP server under Compass governance. Once registered, all tool calls to that server are intercepted by Compass before execution.

### Slack Web API Proxy

```
POST /api/{slack_method}
```

Point `SLACK_API_URL` at `http://localhost:5000/api`. Calls like `chat.postMessage` become `POST /api/chat.postMessage` and are governed by method name before forwarding.

### Multi-Agent Orchestration Endpoints

```
GET  /admin/agents                   # List all registered agents + ring levels + trust scores
POST /admin/agents/{id}/ring         # Manually set agent ring level
POST /admin/agents/{id}/suspend      # Kill switch — immediately block all calls from this agent
GET  /admin/agents/{id}/circuit      # Check circuit breaker state for an agent
POST /admin/agents/{id}/circuit/reset # Manually reset a tripped circuit breaker
```

### Admin & Audit

```
GET /admin/status
GET /admin/agents
GET /admin/approvals
GET /admin/audit?count=50
```

---

## Proxy Tokens

For local demos, callers can pass a real Slack bot token directly.

For the full governance demo, use a Compass-issued token:

```
Authorization: Bearer compass-...
```

Compass resolves this to the real Slack token through `IProxyTokenStore`.

Local in-memory:

```bash
dotnet user-secrets set "ProxyTokens:Mappings:compass-demo-token" "<xoxb-real-token>"
```

Azure Redis (encrypted at rest):

```bash
dotnet user-secrets set "ProxyTokens:RedisConnectionString" "<azure-cache-for-redis-connection-string>"
dotnet user-secrets set "ProxyTokens:Mappings:compass-demo-token" "<xoxb-real-token>"
```

---

## Approval Flow

```
Destructive MCP tool call received
  → GovernanceKernel scores with AIVSS → score > threshold
  → Compass stores pending request (data/approvals.json)
  → Slack approval card sent to approver channel (Block Kit)
  → Admin clicks Approve / Deny in Slack
  → Compass publishes decision to Service Bus queue
  → ServiceBusApprovalDecisionWorker consumes decision
  → APPROVED: replays stored MCP payload to Slack MCP
  → DENIED: request marked rejected, audit entry written
```

Without Service Bus (local demo): decision executes immediately via local publisher.

Configure:

```bash
dotnet user-secrets set "Approvals:Channel" "<CHANNEL_ID>"
dotnet user-secrets set "ServiceBus:ConnectionString" "<azure-service-bus-connection-string>"
dotnet user-secrets set "ServiceBus:ApprovalDecisionsQueue" "compass-approval-decisions"
```

---

## Slash Commands

Configure the Slack command Request URL:

```
https://<ngrok-url>/slack/commands
```

Supported commands:

```
/compass status             # Governance layer health + active policy version
/compass agents             # All registered agents, rings, trust scores
/compass approvals          # Pending approval requests
/compass trust              # Dynamic trust score leaderboard
/compass suspend <agent-id> # Kill switch — immediately suspend an agent
/compass circuit <agent-id> # Check / reset circuit breaker for an agent
```

---

## AGT Verification

Install AGT CLI:

```bash
pip install agent-governance-toolkit-cli
```

Run all checks:

```bash
./agt-verification/run_verify.sh
```

Individual checks:

```bash
agt doctor                                                        # Installation health
agt verify --evidence ./agt-verification/evidence/agt-evidence.json --strict   # OWASP AA01–10
agt lint-policy policies/                                         # YAML policy validation
agt red-team scan ./prompts/ --min-grade B                        # Prompt injection audit
dotnet test --filter "Category=AGT_Conformance" --verbosity normal             # 992 conformance tests
agt verify --component mcp-gateway                                # MCP interception active
```

Expected OWASP output:

```
✓ AA01 Agent Goal Hijacking        COVERED  ← GovernanceKernel prompt injection block
✓ AA02 Excessive Agency            COVERED  ← Ring model + action tier limits
✓ AA03 Insecure Tool Execution     COVERED  ← MCP proxy pre-flight check
✓ AA04 Memory Poisoning            COVERED  ← Audit trail anomaly detection
✓ AA05 Supply Chain                COVERED  ← Agent registry + signed identity
✓ AA06 Cascading Failures          COVERED  ← Circuit breaker per agent
✓ AA07 Inter-Agent Communication   COVERED  ← MCP proxy governs agent-to-agent calls
✓ AA08 Identity & Privilege Abuse  COVERED  ← Confused deputy detection + ring elevation block
✓ AA09 Human-Agent Trust           COVERED  ← Structured Block Kit approval card
✓ AA10 Rogue Agents                COVERED  ← Dynamic trust score + circuit breaker
Grade: A
```

---

## Local Data Files

```
data/agents.json       # Agent registry — ring levels, permissions, trust scores
data/approvals.json    # Pending and resolved approval requests
data/audit.jsonl       # Immutable append-only audit log (tamper-evident)
trust/scores.json      # Dynamic trust score history per agent
policies/
  slack-events.yaml    # Gate 1 policy — inbound event rules
  default_policy.yaml  # Gate 2 policy — MCP action rules (OWASP-aligned)
```

---

## Research Foundation

| Standard | How Compass Implements It |
|---|---|
| OWASP Top 10 for Agentic AI (2026) | AA01–10 fully covered, verified by `agt verify` |
| OWASP AIVSS v0.8 | Risk scoring formula for every intercepted action |
| Microsoft AGT Action Taxonomy | `DESTRUCTIVE_DATA`, `DATA_EXFILTRATION`, `PRIVILEGE_ESCALATION` classification |
| Microsoft AGT Ring Model | 0–3 ring trust tiers + 0–1000 dynamic score |
| EU AI Act Article 14 | Human oversight mandate — approve / reject / block controls |
| NIST AI RMF | Risk tiers map to approval thresholds |
| Orseau & Armstrong (2016) | Safe interruptibility at MCP transport layer |
