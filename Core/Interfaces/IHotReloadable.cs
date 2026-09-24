namespace Magic.Interfaces;

public interface IHotReloadable
{
    byte[] Persist();
    void Restore(byte[] state);
}
