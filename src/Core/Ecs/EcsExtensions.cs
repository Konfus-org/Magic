namespace Magic.Ecs;

/// <summary>
/// What any ECS does through its queries.
/// </summary>
public static class EcsExtensions
{
    extension(IEcs ecs)
    {
        /// <summary>
        /// Fills <paramref name="snapshot"/> with a copy of the entities that have a <typeparamref name="T1"/> and their
        /// components, in query order, replacing what it held: a bulk copy of each chunk written since it was last filled,
        /// on this thread, so a still world copies nothing. What reads it then needs nothing of the ECS, so it can on
        /// another thread.
        /// </summary>
        public void Snapshot<T1>(EcsSnapshot<T1> snapshot)
            where T1 : unmanaged, IComponent
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            EcsQuery<T1> query = ecs.Query<T1>();
            snapshot.Start();
            foreach (EcsChunk<T1> chunk in query)
            {
                snapshot.Add(chunk);
            }

            snapshot.End(query.Version);
        }

        /// <summary>
        /// <see cref="Snapshot{T1}"/> of the entities that have both components.
        /// </summary>
        public void Snapshot<T1, T2>(EcsSnapshot<T1, T2> snapshot)
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            EcsQuery<T1, T2> query = ecs.Query<T1, T2>();
            snapshot.Start();
            foreach (EcsChunk<T1, T2> chunk in query)
            {
                snapshot.Add(chunk);
            }

            snapshot.End(query.Version);
        }
    }
}
