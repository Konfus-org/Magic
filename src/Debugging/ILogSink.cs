namespace Magic.Debugging;

public interface ILogSink
{
    void Write(in Log.Line line);
    void Flush();
}
