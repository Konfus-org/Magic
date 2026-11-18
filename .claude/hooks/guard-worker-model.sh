#!/usr/bin/env bash
# PreToolUse on shell commands: refuses a headless Claude Code run (claude -p, as a swarm's worker or a resume of one)
# that names no --model, or that names a Fable or Mythos model. A worker never runs on the coordinator's top model
# (the parallelize skill, Models). Without --model, a run takes the default model, which can be the top one.
input=$(cat)
command=$(printf '%s' "$input" | sed -n -E 's/.*"command"[[:space:]]*:[[:space:]]*"(([^"\\]|\\.)*)".*/\1/p')
[ -z "$command" ] && exit 0
command=${command,,}

# A headless run: claude, or claude.exe, then -p or --print anywhere in the command.
[[ $command =~ (^|[^a-z0-9_.-])claude(\.exe)?[\"\']?[[:space:]] ]] || exit 0
[[ $command =~ [[:space:]](-p|--print)([[:space:]]|$) ]] || exit 0

if ! [[ $command =~ --model[[:space:]=]+[\"\']?([a-z0-9._-]+) ]]; then
    echo "A worker run needs --model sonnet or opus: without it, it takes the default model (the parallelize skill, Models)." >&2
    exit 2
fi
model=${BASH_REMATCH[1]}
if [[ $model == *fable* || $model == *mythos* ]]; then
    echo "A worker never runs on $model: use --model sonnet or opus (the parallelize skill, Models)." >&2
    exit 2
fi
exit 0
