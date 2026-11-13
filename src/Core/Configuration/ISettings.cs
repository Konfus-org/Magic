using System.Diagnostics.CodeAnalysis;

namespace Magic.Configuration;

/// <summary>
/// The settings of a gem: a <c>sealed class</c> of settable properties with a
/// <see cref="System.ComponentModel.CategoryAttribute"/>, each with its default and a doc comment.
/// </summary>
/// <remarks>
/// <para>
/// Write each doc comment in plain text, because <see cref="Settings"/> lists the property with it. A property can have
/// a category of its own, and a <see cref="RequireRestartAttribute"/> when the gem reads it only when the gem is made.
/// </para>
/// <para>
/// The <c>EntryPoint</c> of the gem takes the settings in its constructor, with the defaults and the changes from
/// earlier runs. It exports them as a public property, and gives them to the parts that read them.
/// </para>
/// <para>
/// <see cref="Settings"/> writes a changed property on this same object, one at a time, on the game thread. The render
/// thread can read one old value and one new value.
/// </para>
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1040:Avoid empty interfaces",
    Justification = "A marker: what makes a part a gem's settings, which Core lists and the services make."
)]
public interface ISettings;
