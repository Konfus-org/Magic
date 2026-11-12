---
name: new-gem
description: Adds a gem to Magic, from the template to a sample that runs it. Use it on your own, without being asked, each time a slice needs a new plugin, renderer, importer, platform layer or other part that a project adds or replaces.
---

# New gem

1. **Make sure that it must be a gem.** A gem is a part that a project adds or replaces
   ([Code Standards](../../CODE_STANDARDS.md#gems-hooks-and-services)).
   - First, look for a gem or a Core service that does the job, or can do it after a small change.
   - A new gem is a decision. Get the user's yes ([AGENTS.md](../../../AGENTS.md#decisions)).
   - Name the gem for its role.
2. **Claim** `src/Gems/<Name>` and `src/Magic.slnx`. If step 6 changes the `.magic` file of a sample, claim it too
   ([AGENTS.md](../../../AGENTS.md#worktrees)).
3. **Make the gem from the template.** From the repository root, run `Magic new gem <Name> -o src/Gems/<Name>`
   ([Templates](../../TOOLS.md#templates)). Only its csproj gives its `Description`, `Version` and `Authors`.
4. **Write the gem** as [Gems, hooks and services](../../CODE_STANDARDS.md#gems-hooks-and-services) says:
   - A `public sealed class EntryPoint : IGem`, with only the hooks that it needs.
   - A constructor that gets what the gem needs and makes its parts.
   - A public get-only property for each part.
   - `Dispose`, which stops the parts.

   Each part is a class with the name of the Core interface that it implements, such as `Importer` or `Renderer`. A
   C# binding to a C library follows the [native libraries](../../GEMS.md#native-libraries) rules.
5. **Write its section** in [Gems](../../GEMS.md). Do not write a README. Say what a user of the gem must know, and
   the services and assets that it uses, by role ([AGENTS.md](../../../AGENTS.md#documentation-rules)).
6. **Run it.** Add it to the `.magic` file of a sample that shows it. Or make a sample for it:
   `Magic new project <Name> -o samples/<Name>`. Then validation runs it. A gem that draws is a `Visual` change. Any
   other gem is a `Code` change.
