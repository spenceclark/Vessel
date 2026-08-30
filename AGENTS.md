# Documents to read

docs/brief.md - Overview of the project
docs/architecture - Technical architecture to use
docs/plan.md - The overall build out plan
docs/ui-spec.md - UI Specification & Design System
docs/phases/phase-x.md - The specific spec for each phase

If you asked to implement a phase - only concern with that phase spec, not anything in future phases.

## Non-negotiables

See .editorconfig for house nameing styles. Specifically private class level variables being with underscore and camel-case.

Violating any of these is either a security incident or unrecoverable. If a task appears to require it, **stop and ask**.

- Never delete, skip, or weaken a failing test to make a build pass. Fix the code or the fixture.

## How to work

- **Do not commit.** Leave the working tree for review.
- **Stop and report rather than guessing.** If scope, intent, or a design choice is ambiguous, stop at that point and report what is ambiguous. Do not pick an interpretation and proceed.
- **Sweep the whole tree for call sites**, not just the folder being changed. A rename or signature change is not done until every call site is updated.
- **Confirm before finalising on judgment calls.** Where a change involves a product or design decision rather than a mechanical one, propose it and wait.
- **Write findings in place.** Correct the affected text directly; do not append a correction note after incorrect content.
- **Implement what was asked.** If something adjacent looks wrong or missing, say so — do not fix it in the same change. Scope creep in an agent change is harder to review than a second change.
