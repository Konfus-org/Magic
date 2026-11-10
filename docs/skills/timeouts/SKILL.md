---
name: timeouts
description: Gives every command an agent starts a time limit, so that nothing runs forever. Use it on your own, without being asked, each time you start a build, a test run, a validation, a run of Magic, a wait for the user's review, or any command in the background.
---

# Timeouts

A command must not run without a time limit. A command that runs forever blocks the slice and hides a hang.

1. Before you start a command, give it a time limit. Use one of these:
   - the timeout of the shell tool;
   - the time limit of a background task;
   - an option of the command.
2. Make the limit approximately two times the usual time of the command. If you do not know the usual time, use
   this table:

   | Command | Limit |
   | --- | --- |
   | `dotnet build src/Magic.slnx` | 10 minutes |
   | One test project | 5 minutes |
   | All tests | 15 minutes |
   | `tools/Scripts/Run.ps1` | Its `-Seconds` and one minute more, or 11 minutes |
   | `tools/Scripts/Screenshot.ps1` | Two times its `-Seconds`, or 20 minutes |
   | `tools/Scripts/AgentTaskComplete.ps1` or `tools/Scripts/Validate.ps1` | 60 minutes |
   | `plannotator review` | 4 hours |
   | One run of a worker of a swarm (`claude -p`) | 1 hour |
   | Other commands | 10 minutes |

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
   `plannotator review` ([finish skill](../finish/SKILL.md) step 5). Do not do step 5.
5. If another command reaches its limit, do the steps below. A run of a worker of a swarm is different: the coordinator
   splits its slice ([parallelize skill](../parallelize/SKILL.md) step 7).
   1. Kill the command and each process that it started, for example a Magic that is still open.
   2. Tell the user what ran, for how long, and its last output.
   3. If its output shows progress, run it again one time, with a limit two times larger.
   4. If it reaches its limit again, treat the hang as a bug. Use the [investigate skill](../investigate/SKILL.md).
      Do not make the limit larger again.
