---
name: timeouts
description: Gives every command an agent starts a time limit, so that nothing runs forever. Use it on your own, without being asked, each time you start a build, a test run, a validation, a run of the app, a wait for the user's review, or any command in the background.
---

# Timeouts

A command must not run without a time limit. A command that runs forever blocks the slice and hides a hang.

1. Before you start a command, give it a time limit. Use one of these:
   - the timeout of the shell tool;
   - the time limit of a background task;
   - an option of the command.
2. Make the limit approximately two times the usual time of the command. If you do not know the usual time, use
   the time limits in [PROJECT.md](../PROJECT.md), then this table:

   | Command | Limit |
   | --- | --- |
   | One test project | 5 minutes |
   | All tests | 15 minutes |
   | A build | 10 minutes |
   | A run of the app | 11 minutes |
   | A full validation | 60 minutes |
   | The user's review | 4 hours |
   | One run of a worker of a swarm (`claude -p`) | 1 hour |
   | Other commands | 10 minutes |

   If a command waits for a lock or a turn, add the time that the wait has left to its limit.
3. If the tool has no limit and the command has no option for one, wrap the command.
   - In bash, use `timeout -k 10 <seconds> <command>`.
   - In PowerShell, use the lines below. For a script, the program is `pwsh` or `powershell`, with `-File <script>`
     in its arguments. On Linux and macOS, use `$p.Kill($true)` in place of `taskkill`.

     ```powershell
     $p = Start-Process <program> -ArgumentList <arguments> -NoNewWindow -PassThru -RedirectStandardOutput <log>
     $p | Wait-Process -Timeout <seconds> -ErrorAction SilentlyContinue
     if (-not $p.HasExited) { taskkill /T /F /PID $p.Id }
     ```

4. If a command that waits for the user reaches its limit, start it again with the same limit. An example is
   the user's review ([finish skill](../finish/SKILL.md) step 5). Do not do step 5.
5. If another command reaches its limit, do the steps below. A run of a worker of a swarm is different: the coordinator
   splits its slice ([parallelize skill](../parallelize/SKILL.md) step 6).
   1. Kill the command and each process that it started, for example an app that is still open.
   2. Tell the user what ran, for how long, and its last output.
   3. If its output shows progress, run it again one time, with a limit two times larger.
   4. If it reaches its limit again, treat the hang as a bug. Use the [investigate skill](../investigate/SKILL.md).
      Do not make the limit larger again.
