using CSharpScriptingGem;
using Magic.Contexts.Assets;
using Magic.Interfaces;
using Xunit;

namespace Magic.IntegrationTests;

/// <summary>The scripting gem finding script classes among the loaded assemblies, this one included.</summary>
public sealed class CSharpScriptingTests
{
    [Fact]
    public void A_script_loads_as_the_class_named_like_its_file()
    {
        CSharpScripting scripting = new();
        Script script = new() { Path = "Scripts/NamedLikeItsFile.cs" };

        scripting.Load(script, []);

        Assert.Equal(typeof(NamedLikeItsFile), script.Type);
    }

    [Fact]
    public void A_script_no_class_is_named_like_fails_to_load()
    {
        CSharpScripting scripting = new();
        Script script = new() { Path = "Scripts/NoSuchClassAnywhere.cs" };

        Assert.Throws<InvalidOperationException>(() => scripting.Load(script, []));
    }

    [Fact]
    public void A_class_that_is_not_a_script_is_not_found()
    {
        CSharpScripting scripting = new();
        Script script = new() { Path = "Scripts/CSharpScriptingTests.cs" };

        Assert.Throws<InvalidOperationException>(() => scripting.Load(script, []));
    }

    private sealed class NamedLikeItsFile : IBehavior;
}
