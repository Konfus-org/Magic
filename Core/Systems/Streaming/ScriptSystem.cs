namespace Magic.Systems.Streaming;

internal class ScriptSystem
{
    // TODO: this is a system that goes over all streamed in assets, see's if they have a 'ScriptContainer' component which should hold a list of ids to script assets.
    // it should then create the script instance on first load, and then call update on it every frame. if the script asset is changed, it should reload the script instance.
}
