---
name: code-commentation
description: Guidelines for commenting C# code clearly and pragmatically. Apply automatically whenever C# is written or modified in this session (new files, refactoring, changes to existing signatures or bodies, Razor @code blocks included) — no explicit invocation required.
---

# code-commentation

Guidelines for clear and pragmatic code commentary in C# projects. Comments exist to document and to
clarify; they are applied consistently to all code written in a session.

## Trigger

Every write or modification of C#, without explicit invocation: a new file, a refactor or even a
single statement inside an existing body — see Retrofit policy. The `@code` blocks of Razor
components are C# and follow the same rules.

Loading this skill before the first edit is mandated by the repository's `CLAUDE.md`.

## Philosophy

- Every declaration (class, property, field, method, parameter) deserves a short comment
- **No walls of text, starting with IntelliSense**: an XML doc is **one line, one sentence** and says
  what the member achieves. The why never goes there: it goes in the body, above the statement it
  concerns. `<remarks>` is the exception, one sentence only, never several `<para>`. Three real
  sentences in a `<summary>` are a wall anyway: nobody reads them in a tooltip
- The subject of a comment is **what this instruction achieves in the domain** and why. Never the
  language construct it achieves it with
- Comments explain the **WHY** and the constraints, not the syntactic **WHAT** — the name and the
  signature already show the mechanics
- Never more than 6 lines per comment block
- Comments are flowing prose, not telegraphic notes; they read naturally in sequence
- **Comments are always in English** — consistency across the codebase
- **Never a comma before `and`/`or` in a series** (the Oxford comma): write `title, subtitle and
  components`, never `title, subtitle, and components`. Where removing it makes the sentence
  ambiguous, rewrite the sentence — typically split it in two — never restore the comma
- Precision over verbosity; every word counts
- No wall of text explaining the obvious; assume a competent reader

## Naming rules

- **Speaking names only**: every identifier (class, property, field, method, parameter) must be
  readable and well contextualised
- **No cryptic abbreviations**: avoid acronyms or shortened forms inferred while reasoning and
  comprehensible only to you
- **Contextualise, do not truncate**: a full and clear name instead of a clipped form or an initialism
- Naming is not the place to be terse; clarity beats brevity
- A name needing a comment to be understood is renamed, not justified
- **Pattern variables and locals**: the name is the noun of what they hold (`persistedAgentIntent`,
  `unhandledIntents`, `classificationError`). It is never the adjective or participle that comes
  handy while writing the pattern (`named`, `active`, `entered`, `misfit`, `closed`): such names make
  sense only inside the reasoning of whoever wrote them
- Short lambda parameters and conventional understandable abbreviations (`ctx`, `sb`, `req`, `msg`)
  are fine: they are not renamed

## Scope rules

### Classes and records
- **One-liner preferred**: state the role or the lifecycle responsibility
- **Multi-line (2-3)**: add constraints (threading model, initialization order, persistence behaviour)
- **Never**: explain what the name already says

### Properties and fields
- **Computed or storage-specific**: always commented
- **Self-evident**: skip only with zero ambiguity
- **Public contract boundaries**: always explain what the caller must know

### Methods
- **Public methods**: always commented — state the outcome, the preconditions, the side effects
- **Private methods**: a one-line comment on what they achieve, always; only a one-liner whose name
  already says everything is skipped
- Method size decides nothing: a 5-line helper has its body commented like a 50-line one (see Method
  bodies)

### Method bodies

The XML doc says what the member achieves; **the body is commented line by line**, on the line the
why refers to.

- **Every statement has its domain comment above it**, the seemingly obvious ones included. A step
  made of several consecutive lines, closed by a blank line, takes a single comment
- This applies to:
  - lookups and reads: why the value is there or what it means when it is missing;
  - assignments and declarations of locals holding a domain value;
  - calls to services, providers and LLMs: what they obtain and on whose behalf;
  - `return`: what it hands to the caller;
  - `if`/`else`, early exits, `continue`, `break`, `throw`, `catch` and `finally`.
- **Every `case` of a `switch` and every arm of a switch expression** has its comment: what that case
  does. Above the `switch` goes one line on what happens to the cases not listed
- The exception is a pure mapping `switch` (a provider onto its client, a serialised form onto its
  value) when the comment above the `switch` already states the rule: its arms stay uncommented,
  because a comment per arm would only repeat the arm
- **Every branch of an `if`/`else if`/`else` has its comment**, unless a two-way `if` is already
  fully explained by the comment above it
- Only these are exempt:
  - log lines, because the message speaks for itself;
  - the mechanical conversion of a value just computed (`string text = sb.ToString();`);
  - calls to the base lifecycle (`base.OnInitialized()`, `base.Dispose()`) and the
    `return Task.CompletedTask` of a synchronous handler;
  - the repetition of the same structural statement in several places: the why is said once, on
    the method being called;
  - braces and the end of the body.
- A comment that would stay true and identical above the same statement in another project is
  filler: if the line has no domain fact to tell and is among the exemptions, it stays uncommented.
  If it is not among the exemptions, the fact is searched until found: why the line is there, what
  breaks without it, why in this order
- "Obvious" is not a reason for silence. It is a reason for a short comment: a lookup says why it
  cannot fail, a `return` says what it hands over
- The comment stays a domain fact, never filler: the rule asks for coverage, not paraphrase (see
  Antipatterns)

### Parameters
- **Only where the name is ambiguous or the contract non-obvious**
- Document required against optional, valid ranges, side effects

### Positional records (primary constructor)
- Every primary-constructor parameter is a public property in all but name: apply the Property and
  Field rules
- Comment a parameter only where it is not obvious from its name; for purely self-describing records
  (few fields, names already speaking) a single class-level comment may be enough
- Do not restate at parameter level a constraint already declared at class level

### Interfaces
- Document the **contract**, not the implementation: what an implementer must guarantee, never how a
  particular implementation does it
- Every interface member follows the public-method rules

### Enums
- Comment the enum where its purpose is not obvious from the name
- Comment individual members only where the meaning is not deducible from the member name (values
  carrying thresholds or special behaviour)

## Antipatterns

- ❌ **Filler comment**: any comment paraphrasing the mechanics of the language, a construct or an
  API instead of stating a fact specific to THIS code. It is not about `await` or any particular
  construct — it happens identically on an `if`, a `foreach`, a cast, a LINQ call or a lock. The test
  holds everywhere: if the comment would stay true and unchanged pasted above the same construct in
  any file of any project, it is filler — delete it without mercy. A comment earns its place only by
  stating what THIS instruction does and why, verifiably and specifically to the domain or the file
  (e.g. "the client is pooled, the tool list is not: every agent creation asks the server again") —
  never a generic fact about the language or the library
- ❌ **Comment explaining the chosen mechanics instead of the effect**: opening with the construct —
  `// Returned rather than awaited`, `// Rebuilt rather than edited`, `// Built once and reused`,
  `// In a finally because…`, `// The removed value is discarded` — then stopping there. It is the
  first genuinely technical thing that comes to mind and it is useless: the reader already sees the
  construct; what they do not see is what the instruction achieves. Test: delete every construct and
  API name from the comment; if no verifiable domain fact is left, rewrite it. The technical note is
  admissible only **after** the fact and only where it is not deducible
- ❌ Commenting what the code already says explicitly (e.g. `// Increment i`)
- ❌ Multi-paragraph explanations of local logic — extract a named method instead
- ❌ Repeating a global policy in every method that touches it — state it once at class level
- ❌ A "TODO" with no date or decision owner — turn it into a task or resolve it now
- ❌ Defensive comments against hypothetical rewrites — trust future readers to refactor if needed
- ❌ Commented-out code left in the file — delete it, git history is the history
- ❌ A comment contradicting or duplicating XML doc already on the same member

## Tone

- **Clinical**, not conversational (no "we", no "I", no emoji, no personality)
- **Imperative** for requirements (`must be`, `is required`)
- **Descriptive** for state (`is computed from`, `holds the result of`)
- **One sentence per line** unless two are syntactically coupled

## Placement

- The comment goes **above** the declaration, never inline
- An expression-bodied member takes its comment after the `=>` that closes the signature line, with
  the expression on the next line; no `//` goes between the XML doc and the declaration or before
  a `=>` that opens a line
- The body of a braceless `if`/`else` takes its comment between the condition and the statement
- For a multi-line comment, open it on the first line, not on a line of its own
- Use XML doc on **public API** only; one line, one sentence (see Philosophy)

## Complex business logic

- State **preconditions and postconditions** as a contract
- Highlight **state transitions** where the code is FSM-like
- Explain the **invariants** that would break if violated

## Retrofit policy

- When touching (changing the signature, body or value of) an **existing uncommented** declaration,
  add the missing comment under these rules
- Do not touch **adjacent but unmodified** comments or declarations merely to align them: the
  retrofit applies to the line already being changed, it is not licence to rewrite the file
- Where a file has a pre-existing commenting style different from these guidelines, keep local
  consistency for untouched code; apply these rules to new or modified code only

## Validation

A section of code is complete when:
1. Every class, interface, enum and record carries a comment, as does every public method or property
2. No section leaves you asking "wait, why does it do that?" without checking the comment
3. The comments read as flowing narrative, not scattered fragments
4. No comment exceeds 6 lines
5. Every comment passes the test "would somebody with domain knowledge but no familiarity with this
   exact code understand it?"
6. Every comment is in English
7. Every identifier is a speaking name, with no cryptic acronym or abbreviation
8. No dead or commented-out code is left in the file
9. No comment contains a comma before `and`/`or`
10. Every statement of a body has its comment, as does every `case` and every switch arm. The only
    exceptions are those listed in "Method bodies": logs, mechanical conversions, base lifecycle,
    synchronous handlers with nothing to await and structural repetitions
11. No pattern variable or local is named with an adjective or participle lacking a noun
12. No XML doc exceeds one sentence; no `<remarks>` exceeds one sentence
