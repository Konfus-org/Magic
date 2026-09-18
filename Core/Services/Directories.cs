namespace Magic.Services;

public sealed class Directories
{
    public Directories(string root, string gems)
    {
        Root = root;
        Gems = gems;
    }

    public string Root { get; }
    public string Gems => Path.Combine(Root, field);
}
