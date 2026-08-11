# Issue tracker: GitHub

Issues and specs for this repo live as GitHub issues. Use the `gh` CLI for all operations.

## Conventions

- Create, read, comment on, label, and close issues with the corresponding `gh issue` commands.
- Infer the repository from `git remote -v` when running inside this checkout.
- Pull requests are not treated as a triage request surface by default.

## Publishing and fetching

- When a skill says to publish to the issue tracker, create a GitHub issue.
- When a skill says to fetch a ticket, read the issue and its comments with `gh issue view`.

## Wayfinding operations

- The map is an issue labelled `wayfinder:map`.
- Decision tickets are child issues labelled `wayfinder:research`, `wayfinder:prototype`, `wayfinder:grilling`, or `wayfinder:task`.
- Prefer GitHub sub-issues and native issue dependencies. If unavailable, use a task list on the map and a `Blocked by: #<number>` line on blocked tickets.
- An open, unassigned, unblocked child issue is on the frontier.
- Claim a ticket by assigning it to the current developer before beginning work.
- Resolve a ticket by commenting with the answer, closing it, and appending a linked one-line gist to the map's Decisions-so-far section.
