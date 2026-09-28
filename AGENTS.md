# Repository Instructions

* DO NOT PUSH

## Coding Guidelines

* Prefer explicit record declarations with init-only properties. Do not use positional record syntax such as `record Item(string Name, int Count)`.
* Write braces for every `if` body, including single-statement branches.
* Do not use the empty property pattern `x is {}`.
* Keep each source file below 800 lines. Split large files by responsibility before they reach that limit.
* Format C# changes with CSharpier and verify them with `dotnet csharpier check`.
* Favor low-allocation, efficient code while keeping the implementation straightforward and maintainable.
* Update public API approval files when a public API change requires it.

## Progress Management

* Track each task, its progress, and unresolved issues in a separate Markdown file under `todo/`.
* Keep `todo/` in the local gitignore. Never commit or push its contents. Delete a task file when that task is complete.
* Prefer working on `main` when no other work is in progress. If work is already in progress, use a separate worktree and record its progress in `todo/`.
* Commit and push each completed task, then integrate it into `main` promptly.
* Periodically review `todo/` while working on `main`; delegate subtasks when delegation is available and useful. When a worktree task is complete, squash-merge it after the current main commit is ready, then remove its worktree and task file.
