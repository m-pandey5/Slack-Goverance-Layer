# Compass

Compass is a governance proxy for Slack agents. It starts with two gates:

1. `/slack/events` verifies Slack signatures and evaluates inbound Slack events through `GovernanceKernel`.
2. `/mcp-proxy` evaluates outbound MCP tool calls through `McpGateway` before forwarding allowed calls to Slack MCP.

## Startup Order

1. Program.cs + DI
2. `policies/slack-events.yaml`
3. Slack app registration
4. `SlackEventController`
5. curl test: injection blocked
6. `McpProxyController`
7. curl test: destructive tool blocked
8. ngrok tunnel + Slack Events URL
9. Service Bus approval flow
10. Audit logging
11. Demo polish

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
dotnet run --urls http://localhost:5000
```

In another terminal:

```bash
cd /Users/muskan/slack/compass
SLACK_SIGNING_SECRET="<your-signing-secret>" ./scripts/test-slack-injection.sh
./scripts/test-mcp-approval.sh
```
