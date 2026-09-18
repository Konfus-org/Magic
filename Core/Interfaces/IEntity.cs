namespace Magic.Interfaces;

public interface IEntity
{
    uint Handle { get; }
    string Name { get; set; }

    IEntity Parent { get; set; }
    IEntity[] Children { get; set; }

    T GetComponent<T>();
    T AddComponent<T>();
    T RemoveComponent<T>();
}
