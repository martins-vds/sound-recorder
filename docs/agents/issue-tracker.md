# Issue tracker: GitHub

Issues and specs live in `martins-vds/sound-recorder`. Use `gh` from this clone; verify the target with `gh repo view --json nameWithOwner` before creating issues. File work only in this repository.

## Operations

- Create: `gh issue create --repo martins-vds/sound-recorder --title "..." --body-file <path>`.
- Use a UTF-8 body file or PowerShell here-string for multiline text.
- Read: `gh issue view <number> --json number,title,body,labels,comments`.
- List: `gh issue list --state open --limit 100 --json number,title,body,labels`; paginate when needed.
- Comment: `gh issue comment <number> --body "..."`.
- Labels: `gh issue edit <number> --add-label "..."` / `--remove-label "..."`.
- Close: first post completion evidence with `gh issue comment`, then `gh issue close`. Ticket creation or a merged branch alone does not close work.

## Task graph

Use native sub-issues and blocking dependencies. On `gh` 2.94+, use `--parent` and `--blocked-by` during issue creation, or `--add-sub-issue` / `--add-blocked-by` during editing. On older versions, use `gh api` POST `repos/martins-vds/sound-recorder/issues/<parent>/sub_issues` with `sub_issue_id=<child-database-id>`, and POST `repos/martins-vds/sound-recorder/issues/<child>/dependencies/blocked_by` with `issue_id=<blocker-database-id>`. Obtain database IDs with `gh api repos/martins-vds/sound-recorder/issues/<number> --jq .id`.

If native links are unavailable, record `Part of #<parent>` and `Blocked by: #<blocker>` in child bodies and maintain a task list in the parent. A ticket is ready only when every blocker is closed. Read native blockers through the `dependencies/blocked_by` API and filter open states; verify body fallbacks too.

## Request surface

**PRs as a request surface: no.**

When a skill says publish to the issue tracker, create a GitHub issue. When it says fetch a ticket, read the issue and its comments. Spec/map issues link their child tickets and evidence; research, prototypes, and approval gates must remain distinct from feature implementation.
