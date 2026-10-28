# Samples

Projects that show what Magic does and that `tools/Scripts/Validate.ps1` runs. Each folder is one project: its
`.magic` file and the csproj that holds its own code.

Run one from its csproj, which builds Magic and opens the project headless:

```powershell
dotnet run --project samples/HelloWorld
```

Type `exit` to end the run.

Make another with the project template: `Magic new project <Name> -o samples/<Name>`
([Templates](../tools/README.md#templates)).
