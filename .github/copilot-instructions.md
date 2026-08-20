# Copilot Instructions

## Project Guidelines

- Keep AG-UI request logging/debugging middleware non-functional. Do not make diagnostic middleware a required dependency for history persistence or application behavior; keep functional design contained in the relevant provider/factory or explicit application components.

## Memory Management

- Retain broad conversational context, including user topics and assistant recommendations, while excluding system prompts, instructions, tool definitions, and tool/protocol content that is relayed separately.

## Testing Guidelines

- Integration tests should clean their scoped Cosmos records regardless of test outcome; the test database must be left clean.

## Error Handling Guidelines

- Avoid empty catch blocks and exception-driven control flow where possible; when a catch is necessary, log the captured behavior.
- Prefer root-cause fixes over prompt-level or UI-level hacks that mask unexpected agent behavior; retain diagnostics and investigate the underlying cause instead.