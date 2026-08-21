# Copilot Instructions

## Project Guidelines

- Keep AG-UI request logging/debugging middleware strictly diagnostic and non-functional. Do not make application behavior, session persistence, or history continuity depend on it; prefer standard sample-aligned infrastructure with minimal bespoke behavior.
- Keep functional design contained in the relevant provider/factory or explicit application components.

## Agent Memory Management

- Retain broad conversational context, including user topics and assistant recommendations, while excluding system prompts, instructions, tool definitions, and tool/protocol content that is relayed separately.

## AG-UI Conversation History Guidelines *Important*

- The architecture for this application is one of client-side AG-UI calls with server-owned Cosmos-hosted AgentSession. The client should not retain or accumulate prior chat history between turns beyond transient per-turn tool processing. The server-hosted Cosmos AgentSession is the sole conversation-history owner.
- For this AG-UI application, client turns must send only the latest user message. The server-hosted Cosmos AgentSession is the sole conversation-history owner; client code must not retain or accumulate prior chat history between turns beyond transient per-turn tool processing.
- Create a fresh client AgentSession for every client-initiated user turn; retain it only for that RunStreamingAsync call and its internal tool loop. Never reuse a client session across turns because it accumulates/replays local chat history. Continue cross-turn server conversation via RunAgentInput.ThreadId and ParentRunId from the prior RunStartedEvent; the Cosmos-hosted AgentSession is the sole durable cross-turn history owner. Keep clear code comments protecting this invariant.

## C# Code Style Guidelines

- Use names such as `isStreaming` instead of underscore-prefixed field names.
- Qualify instance fields with `this.` in methods when distinguishing them from local variables.

## Testing Guidelines

- Integration tests should clean their scoped Cosmos records regardless of test outcome; the test database must be left clean.
- Do not run the full integration suite while the user is actively changing configuration; integration tests are slow. 

## Error Handling Guidelines

- Avoid empty catch blocks and exception-driven control flow where possible; when a catch is necessary, log the captured behavior.
- Prefer root-cause fixes over prompt-level or UI-level hacks that mask unexpected agent behavior; retain diagnostics and investigate the underlying cause instead.