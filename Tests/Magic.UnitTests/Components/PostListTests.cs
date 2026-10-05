using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Xunit;

namespace Magic.UnitTests.Components;

public sealed class PostListTests
{
    [Fact]
    public void The_list_ends_at_the_first_empty_handle()
    {
        PostList list = default;
        list[0] = new Handle<Post>(7);
        list[2] = new Handle<Post>(9);

        int count = list.Count;

        Assert.Equal(1, count);
    }

    [Fact]
    public void A_full_list_counts_every_slot()
    {
        PostList list = default;
        for (int i = 0; i < PostList.Capacity; i++)
            list[i] = new Handle<Post>((ulong)i + 1);

        int count = list.Count;

        Assert.Equal(PostList.Capacity, count);
    }
}
