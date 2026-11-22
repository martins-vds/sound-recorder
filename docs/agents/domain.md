# Domain docs

## Before exploring behavior

Read the root [GLOSSARY.md](../../GLOSSARY.md) and relevant [requirements](../../requirements). Before changing behavior, tests, requirements, or running quality analysis, read [testing instructions](../../.github/instructions/testing.instructions.md). Read any `docs/adr/` decisions relevant to the work.

This repository is single-context: root `GLOSSARY.md`, optional `docs/adr/`, application code under `src/`. If optional ADRs are absent, proceed silently; create them only when a decision warrants one.

## Vocabulary and conflicts

Use the glossary's exact terms in tickets, hypotheses, tests, and implementation. Surface contradictions with existing requirements or ADRs rather than silently overriding them. Resolve genuine vocabulary gaps through domain-modeling. A proposed design or feasibility ticket is not evidence of implemented behavior or release acceptance.
