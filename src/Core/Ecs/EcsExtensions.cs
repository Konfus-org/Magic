namespace Magic.Ecs;

public static class EcsExtensions
{
    extension(IEcs ecs)
    {
        /// <summary>
        /// Makes every change recorded in <paramref name="changes"/>, in the order recorded, and empties it for reuse.
        /// A change to an entity that is no longer alive is skipped. Throws during a query's loop or a run, as the
        /// changes themselves would.
        /// </summary>
        public void Apply(EntityChanges changes)
        {
            ArgumentNullException.ThrowIfNull(changes);
            changes.Apply(ecs);
        }
    }
}
