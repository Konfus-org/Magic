using System.Diagnostics.CodeAnalysis;

namespace Magic.Configuration;

/// <summary>
/// A gem's settings: a <c>sealed class</c> of settable properties with a
/// <see cref="System.ComponentModel.CategoryAttribute"/>, each property with its default and a doc comment in plain
/// text, which is what <see cref="Settings"/> lists it with. A property may carry a category of its own, and
/// <see cref="RequireRestartAttribute"/> when the gem reads it only as it is made. The gem's <c>EntryPoint</c> takes it
/// in its constructor, the defaults with what was changed in earlier runs, exports it as a public property, and gives
/// it to the parts that read it. <see cref="Settings"/> writes a changed property on this same object, one at a time, on
/// the game thread; the render thread may read one old and one new.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1040:Avoid empty interfaces",
    Justification = "A marker: what makes a part a gem's settings, which Core lists and the services make."
)]
public interface ISettings;
