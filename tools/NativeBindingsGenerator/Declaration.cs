namespace Magic.Tools.NativeBindingsGenerator;

internal enum DeclarationKind
{
    Constant,
    Type,
    Function,
}

/// <summary>
/// One C declaration as C# text, from one platform's parse. The same declaration from every platform must read the
/// same, which is what makes one binding serve them all; a declaration only one platform has is still declared.
/// <c>Owner</c> is the C name of what it is declared in, a prefix's static class or the struct its functions attach
/// to (<c>AttachesToStruct</c>, as <c>ImGuiIO_AddKeyEvent</c> does), empty at the namespace; <c>Name</c> is its C#
/// name, by which two declarations that would become one are found.
/// </summary>
internal sealed record Declaration(DeclarationKind Kind, string CName, string Owner, bool AttachesToStruct, string Name, string Text);

/// <summary>A construct the generator does not emit, with where it is, for the build log.</summary>
internal sealed record Skip(string What, string Where);
