---
name: new-gem
description: Adds a gem to Magic, from the template to a sample that runs it. Use it on your own, without being asked, whenever a slice needs a new plugin, renderer, importer, platform layer or other piece a project adds or replaces.
---

# New gem

1. **Check it should be a gem.** A gem is something a project adds or replaces
   ([Code Standards](../../CODE_STANDARDS.md#gems-hooks-and-services)). Look first for a gem or Core service that
   already does the job, or could with a small change. A new gem is a decision that needs the user's yes
   ([AGENTS.md](../../../AGENTS.md#decisions)). Name it for its role.
2. **Claim** `src/Gems/<Name>` and `src/Magic.slnx`, and the sample's `.magic` file if step 6 changes one
   ([AGENTS.md](../../../AGENTS.md#worktrees)).
3. **Make it from the template:** `Magic new gem <Name> -o src/Gems/<Name>` from the repository root
   ([Templates](../../TOOLS.md#templates)). Its csproj states its `Description`, `Version` and `Authors`,
   and nothing else does.
4. **Write the gem** as Code Standards' [Gems, hooks and services](../../CODE_STANDARDS.md#gems-hooks-and-services)
   says: a `public sealed` class implementing `IGem` and the Core interfaces it provides, a constructor that takes
   what it depends on and starts it, `Dispose` to end it, and only the hooks it needs. A C# binding to a C library
   follows the [native libraries](../../GEMS.md#native-libraries) rules.
5. **Write its section** in [Gems](../../GEMS.md), with no README: what a user of the gem must know, and the
   services and assets it uses, by role ([AGENTS.md](../../../AGENTS.md#documentation-rules)).
6. **Run it:** list it in the `.magic` file of a sample that shows it, or make a sample for it
   (`Magic new project <Name> -o samples/<Name>`), so validation runs it. A gem that draws is a `Visual` change;
   anything else is `Code`.
